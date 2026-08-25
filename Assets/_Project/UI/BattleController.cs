using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 战斗操作总控（测试期外挂，跑通后整理给后端正式化）：
    /// - 阶段状态机：按钮三形态 / 模式重置
    /// - 执行镜像：发 ExecuteRequest 后 UI 侧镜像逐槽（可选格查询为只读自建实例）
    /// - 选格：移动=高亮可选格；攻击=全盘可点
    /// - 表现协议：表现事件帧缓冲合并 → 播动画 → 发 PresentationFinished
    /// - 手牌：HandChanged 重建 + 拖拽部署（预览立绘跟随 + 吸附 + 合法格高亮）
    /// </summary>
    public class BattleController : MonoBehaviour
    {
        // ========== 注入 ==========
        BattleFlow _flow;
        GameState _state;
        UIManager _uiManager; // 2026-08-12 架构重构：BattlePanel 注册/切换显示用
        BoardRules _boardRules;
        IntentResolver _intentResolver;
        BattlePanel _panel;

        /// <summary>当前绑定战斗（2026-08-26：Bootstrap.OnPhaseChanged 幂等守卫校验用——胜利推进下一关后旧控制器仍存在但绑定已销毁的旧 flow → 据此销毁重建）。</summary>
        public BattleFlow Flow => _flow;

        // 回合进度条已迁移：Assets/_Project/UI/Widgets/WaveProgressBar.cs（BackgroundCanvas 心电图式，2026-08-24）

        // ========== 状态 ==========
        HashSet<int> _batchFlashAttackers; // 表现组内攻击者闪白去重（#6 前端部分：AOE 多目标只闪攻击者一次——架构 §四.7 组内并行）
        // ====== 遗物栏（2026-08-14：Btn_Relic 切换 Grp_RelicDisplay；图标占位色块 + hover 描述）======
        Button _relicBtn;
        RectTransform _relicDisplay;   // Grp_RelicDisplay（横向列表容器——布局用户已设）
        bool _relicListShown;
        int _relicListGen;             // 列表重建代际（快速连点防旧协程写入——2026-08-14 M1）
        UnityEngine.EventSystems.PointerEventData _relicPointerData; // 复用指针数据（点击外部检测——避免每帧分配）
        readonly List<UnityEngine.EventSystems.RaycastResult> _relicRaycastResults = new(); // 复用射线结果
        bool _executing;             // 执行镜像进行中
        int _execPieceId = -1;
        List<Template> _execProgram;
        int _execIndex;
        bool _awaitingCell;          // 等玩家选格
        bool _isMoveSelect;          // true=移动选格 false=攻击选格
        List<Vector2Int> _cellOptions = new List<Vector2Int>();
        GameObject _highlightRoot;   // 移动可选格高亮容器
        // ====== 插入执行（免费行动"获得即立即执行"——2026-08-24 定案：提示条 + 自动锁定 + 弹目标选择 + 允许空放）======
        readonly Queue<int> _pendingForcedExecs = new Queue<int>(); // 免费行动待强制执行队（镜像后端 _pendingImmediateExecutes 时机）
        bool _freeActionTipShowing;                                      // 免费行动提示防连发
        const float FreeActionTipDuration = 2.0f;                        // 免费行动提示显示时长（秒）
        // ========== 玩法面板·骰子（2026-08-24：Grp_FloorPlay_Dice——投掷/点数直线移动；方向选择 = 场上高亮可达格点击，与移动选格同操作习惯；后端契约 StateChanged("dice-move-select") → OnDiceDirectionSelected）==========

        void EnsureDiceRefs()
        {
            if (_grpFloorPlayDice != null) return;
            if (_grpPlayRoot == null) _grpPlayRoot = FindSceneTransform("Grp_Play");
            if (_grpPlayRoot == null) return;
            var dicePanel = _grpPlayRoot.Find("Grp_FloorPlay_Dice");
            _grpFloorPlayDice = dicePanel != null ? dicePanel.gameObject : null;
            if (_grpFloorPlayDice == null) return;
            var roll = FindDeep(_grpFloorPlayDice.transform, "Btn_RollDice");
            _rollDiceBtn = roll != null ? roll.GetComponent<Button>() : null;
            if (_rollDiceBtn != null)
            {
                _rollDiceBtn.onClick.RemoveListener(OnRollDiceClicked); // 每场战斗重接线（防重复监听）
                _rollDiceBtn.onClick.AddListener(OnRollDiceClicked);
            }
            var move = FindDeep(_grpFloorPlayDice.transform, "Btn_DiceMove");
            _diceMoveBtn = move != null ? move.GetComponent<Button>() : null;
            if (_diceMoveBtn != null)
            {
                _diceMoveBtn.onClick.RemoveListener(OnDiceMoveClicked);
                _diceMoveBtn.onClick.AddListener(OnDiceMoveClicked);
            }
            _diceValueText = GetTmp(_grpFloorPlayDice, "Txt_DiceValue");
            _diceHintText = GetTmp(_grpFloorPlayDice, "Txt_DiceHint");
            _diceFaces = new Image[6];
            for (int i = 0; i < 6; i++)
            {
                var face = FindDeep(_grpFloorPlayDice.transform, "Img_DiceFace_" + (i + 1));
                _diceFaces[i] = face != null ? face.GetComponent<Image>() : null;
            }
        }

        /// <summary>骰子面板刷新：显隐（按玩法激活）+ 点数 + 按钮可用性 + 提示。</summary>
        void RefreshDicePanel()
        {
            EnsureDiceRefs();
            bool active = _state != null && _state.IsStyleActive(StyleRegistry.Dice);
            if (_grpFloorPlayDice != null && _grpFloorPlayDice.activeSelf != active) _grpFloorPlayDice.SetActive(active);
            if (!active)
            {
                CancelDiceMoveSelect();
                RefreshGrpPlayVisibility();
                return;
            }
            if (_diceValueText != null) _diceValueText.text = _state.DiceValue.ToString();
            if (_diceFaces != null)
            {
                for (int i = 0; i < _diceFaces.Length; i++)
                {
                    if (_diceFaces[i] != null) _diceFaces[i].gameObject.SetActive(_state.DiceValue == i + 1);
                }
            }
            bool canAct = _state.Phase == BattlePhase.PlayerTurn && !_executing && !_presentationPlaying && !_diceMoveSelecting;
            if (_rollDiceBtn != null) _rollDiceBtn.interactable = canAct;
            if (_diceMoveBtn != null) _diceMoveBtn.interactable = canAct && _state.DiceValue > 0;
            if (_diceHintText != null)
            {
                _diceHintText.text = _diceMoveSelecting
                    ? "点数直线移动：点选场上可达格"
                    : _state.DiceValue > 0
                        ? "点数 " + _state.DiceValue + "：可启动直线移动"
                        : "投掷骰子获得点数（执行类行动 1 AP）";
            }
            RefreshGrpPlayVisibility();
        }

        void OnRollDiceClicked()
        {
            if (_flow == null || _state == null) return;
            if (_state.Phase != BattlePhase.PlayerTurn || _executing || _presentationPlaying || _diceMoveSelecting) return;
            _flow.OnPlayerRequestRollDice(new RollDiceRequest()); // 执行类行动 1 AP——后端校验/落账
            RefreshDicePanel();
        }

        void OnDiceMoveClicked()
        {
            if (_flow == null || _state == null) return;
            if (_state.Phase != BattlePhase.PlayerTurn || _executing || _presentationPlaying || _diceMoveSelecting) return;
            if (_state.DiceValue <= 0)
            {
                if (_diceHintText != null) _diceHintText.text = "先投掷获得点数";
                return;
            }
            _flow.OnPlayerRequestDiceMove(new DiceMoveRequest()); // 不耗 AP 消耗点数 → 后端发 StateChanged("dice-move-select")
        }

        /// <summary>点数直线移动选方向：场上高亮 4 向可达终点（镜像后端校验——逐格界内非障碍 + 终点非占用），点格反推方向。</summary>
        void EnterDiceMoveSelect()
        {
            EnsureDiceRefs();
            if (_state == null) return;
            int pieceId = _selectedPieceId >= 0 ? _selectedPieceId : _execPieceId; // 启动时玩家正在操作的棋子
            var piece = _state.GetPiece(pieceId);
            if (piece == null || piece.side != Side.Player) return;
            // 后端重定向（未进入普通执行）：退出普通执行镜像，防与方向选择冲突
            if (_executing)
            {
                _executing = false;
                _execPieceId = -1;
                _execProgram = null;
                _execIndex = 0;
                _awaitingCell = false;
                _selectResultDirty = false;
            }
            _diceMoveSelecting = true;
            _diceMovePieceId = pieceId;
            int steps = _state.DiceMoveSteps;
            var reachable = new List<Vector2Int>();
            _diceMoveDirections.Clear();
            foreach (var dir in new[] { Direction.Up, Direction.Down, Direction.Left, Direction.Right })
            {
                var cursor = piece.position;
                bool ok = true;
                for (int i = 0; i < steps; i++)
                {
                    cursor += DiceDirectionToVector(dir);
                    if (!_boardRules.IsInsideBoard(cursor) || _state.IsBlocked(cursor)) { ok = false; break; }
                }
                if (!ok) continue;
                if (_state.GetPieceAt(cursor) != null) continue; // 终点占用——不可达（后端同判定）
                reachable.Add(cursor);
                _diceMoveDirections[cursor] = dir;
            }
            if (reachable.Count == 0)
            {
                CancelDiceMoveSelect(); // 无可达方向——取消（后端亦会校验失败重选）
                return;
            }
            ShowHighlights(reachable, null); // 绿块——与移动选格同视觉（原操作习惯）
            RefreshDicePanel();
        }

        void CancelDiceMoveSelect()
        {
            if (!_diceMoveSelecting && _diceMovePieceId < 0 && _diceMoveDirections.Count == 0) return;
            _diceMoveSelecting = false;
            _diceMovePieceId = -1;
            _diceMoveDirections.Clear();
            ClearHighlights();
            RefreshDicePanel();
        }

        static Vector2Int DiceDirectionToVector(Direction dir)
        {
            switch (dir)
            {
                case Direction.Up: return Vector2Int.up;
                case Direction.Down: return Vector2Int.down;
                case Direction.Left: return Vector2Int.left;
                case Direction.Right: return Vector2Int.right;
                default: return Vector2Int.zero;
            }
        }

        // ====== 玩法面板·围棋（2026-08-24：Grp_FloorPlay_Go——手牌式拖拽部署，牌不消耗）======
        Transform _grpPlayRoot;      // Grp_Play（关卡玩法容器——与棋子信息区同位置切换显隐）
        GameObject _grpFloorPlayGo;  // Grp_FloorPlay_Go
        // ====== 玩法面板·代币（2026-08-24：Grp_FloorPlay_Token——购买弃牌区牌复制入手牌）======
        GameObject _grpFloorPlayToken; // Grp_FloorPlay_Token
        TMP_Text _tokenCountText;      // Txt_TokenCount_K (1)——数值（Txt_TokenCount_K = 标签"拥有代币："）
        Button _buyTokenBtn;           // Btn_BuyToken（购买——打开牌库面板购买模式）
        GameObject _goCard;          // Piece_Handcard（围棋棋子牌——拖拽源）
        CanvasGroup _goCardCg;       // 拖拽中半透明（牌不消耗，停留面板）
        TMP_Text _goCountText, _goNextText, _goHintText; // Txt_Count / Txt_Next / Txt_Hint(动态提示)；Txt_Tip 常驻文本预制体已设——代码不写
        bool _draggingGo;            // 围棋拖拽进行中
        Vector2Int _goPreviewCell = new Vector2Int(-1, -1);
        Button _goBuyBtn;            // Btn_BuyGo（能力「买子」——花代币 +1 次部署；围棋+代币激活时显示）
        TMP_Text _goBuyBtnText;      // 按钮文本（"花 X 代币 +1 次"）
        // ====== 玩法面板·骰子（2026-08-24：Grp_FloorPlay_Dice——投掷/点数直线移动；方向选择 = 场上高亮可达格点击，与移动选格同操作习惯）======
        GameObject _grpFloorPlayDice;  // Grp_FloorPlay_Dice
        Button _rollDiceBtn;           // Btn_RollDice（投掷——执行类行动 1 AP）
        Button _diceMoveBtn;           // Btn_DiceMove（点数直线移动启动）
        TMP_Text _diceValueText;       // Txt_DiceValue（当前点数）
        TMP_Text _diceHintText;        // Txt_DiceHint（动态提示）
        bool _diceMoveSelecting;       // 点数直线移动选方向中（场上高亮）
        int _diceMovePieceId = -1;     // 移动选择中的棋子
        readonly Dictionary<Vector2Int, Direction> _diceMoveDirections = new Dictionary<Vector2Int, Direction>(); // 可达格 → 方向
        Image[] _diceFaces;                // Img_DiceFace_1~6（点数对应图——预制体已挂子图，代码按点数显隐）
        // ====== 玩法面板·麻将（2026-08-27：Grp_FloorPlay_Mahjong——牌山两张手牌卡 + 番数 + 和牌按钮；手牌麻将卡点击=摸切、拖拽=打墙）======
        GameObject _grpFloorPlayMahjong;            // Grp_FloorPlay_Mahjong
        TMP_Text _mahjongFanText;                   // Txt_MahjongFan（番数值）
        TMP_Text _mahjongScoreText1, _mahjongScoreText2; // Txt_MahjongScore_1/_2（牌山数字——与手牌卡同步显示）
        Button _huBtn;                              // Btn_Hu（和牌——雀头+番数>0 可用）
        GameObject _mahjongCardTemplate;            // Piece_Handcard 模板（Addressables 缓存）
        readonly GameObject[] _mahjongPanelCards = new GameObject[2]; // 牌山 1/2 号位运行时生成的手牌卡实例
        readonly Transform[] _mahjongCardAnchors = new Transform[2];   // 占位实例锚点（位置/缩放）
        readonly List<GameObject> _mahjongWallViews = new List<GameObject>(); // 场墙视觉（占位灰块）
        int _mahjongDragValue = -1;                 // 麻将拖拽打墙中的点数（-1=未拖）
        GameObject _mahjongDragCard;                // 拖拽中的麻将卡
        GameObject _mahjongWallPreview;             // 打墙预览块
        Vector2Int _mahjongWallPreviewCell = new Vector2Int(-1, -1);
        static Sprite _mahjongWallSprite;
        // ====== Grp_Mode（玩法介绍按钮 + 玩法区——2026-08-26：介绍按钮 → FloorPlayDetailePanel；槽位填充玩法预制体/None；选中棋子整组隐藏）======
        Transform _grpModeRoot;          // Grp_Mode（整组显隐）
        Transform _grpIntroductionRoot;  // Grp_Introduction（3 个介绍按钮）
        readonly List<Button> _introButtons = new List<Button>();
        readonly List<TMP_Text> _introButtonTexts = new List<TMP_Text>();
        readonly List<string> _modeActiveStyles = new List<string>(); // 当前激活玩法（按 StyleRegistry.All 固定顺序，≤3）
        string _modeKey = "";            // 激活玩法集合指纹（槽位重建判变用）
        int _modeBuildGeneration;        // 槽位重建代际（防陈旧协程写入）
        readonly List<GameObject> _modeSlots = new List<GameObject>(); // Grp_Play 下槽位实例（玩法面板/None）
        GameObject _grpPlayNoneTemplate; // Grp_Play_None 模板（Addressables 缓存）
        readonly Dictionary<string, GameObject> _playPanelTemplates = new Dictionary<string, GameObject>(); // styleId → Grp_FloorPlay_*
        int _selectedPieceId = -1;
        // 敌方升变预告可能早于 Piece View 创建：按 pieceId 缓存，视觉出现后补应用。
        readonly Dictionary<int, PromoteAnnouncement> _pendingPromotionWarnings = new Dictionary<int, PromoteAnnouncement>();
        readonly BattleViewRegistry _pieceViews = new BattleViewRegistry();
        readonly List<GameObject> _shockWallViews = new List<GameObject>(); // 震击墙视觉（2026-08-26）

        // 表现队列（帧缓冲合并同槽事件）
        readonly List<System.Func<IEnumerator>> _presentations = new List<System.Func<IEnumerator>>();
        bool _presentationPlaying;
        bool _selectResultDirty;     // 选格后帧内是否有表现事件（判落账成败）

        // 部署预览
        GameObject _previewPiece;
        Vector2Int _previewCell = new Vector2Int(-1, -1);
        bool _draggingCard;
        bool _draggingPromotionCard;
        int _dragDefId = -1;
        int _dragCardInstanceId = -1; // 拖拽起始卡的运行实例 id（部署/升变精确消费）
        GameObject _dragCard; // 拖拽中的卡片（失败时恢复，避免整体重建闪烁）

        // 信息面板（Main 1 场景 UI 根下的 3D TMP 文本，用户已拼）
        TMP_Text _infoName, _infoType, _infoValue, _infoDurability, _infoAbilities;
        TMP_Text _infoOther; // 单节点多行 buff 区（Txt_Other——护盾/免费行动/临时能力/升变，\n 分隔）
        Transform _pieceInfoRoot; // Grp_Piece / 旧名 Piece：只隐藏单位信息，不影响常驻 3D TMP。
        // Main/UI 下的 3D TMP 计分字段（标题 *_K 由场景维护，数值节点由控制器刷新）。
        TMP_Text _totalScoreText, _waveScoreText, _baseScoreText, _multiplierText, _turnScoreText;
        bool _scoreRefsWarningLogged;
        int _lastHandCount = -1;
        int _lastPlayerScore = -1;
        SpriteRenderer[] _infoProgramBlocks = new SpriteRenderer[4]; // 行为逻辑块（SpriteRenderer）
        List<Template> _infoProgram; // 当前信息面板显示的程序（浮窗内容源）
        DG.Tweening.Tween _phaseFlashTween; // 准备完成按钮文字闪动 tween（显式管理，防销毁后访问）
        bool _phaseTipShowing;             // 阶段按钮 hover 提示是否正在显示（2026-08-23 准备引导）
        bool _apEmptyTipShowing;           // AP 耗尽悬浮提示正在显示（2026-08-24 可选挂点——防连发刷屏）
        const float ApEmptyTipDuration = 1.6f; // AP 耗尽提示显示时长（秒）

        // ====== 通用变亮通道（HintRequested——2026-08-23：E5 资格等提示；视觉与升变预告红框相互独立）======
        GameObject _qualifyCardHighlight;      // 当前 E5 资格高亮的手牌卡（CardQualify targetId=牌 instanceId）
        Vector3 _qualifyCardBaseScale;         // 高亮前卡片缩放（恢复用）
        Color _qualifyCardBaseColor = Color.white;
        bool _qualifyCardHasColor;
        DG.Tweening.Tween _abilityFlashTween;  // 左面能力面板（Txt_Abilities）金色脉冲

        // ========== 生命周期 ==========
        void OnDestroy()
        {
            EventCenter.Instance.RemoveEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.ActionPointChanged, OnAPChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.HandChanged, OnHandChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.PieceMoved, OnPieceMoved);
            EventCenter.Instance.RemoveEventListener(GameEvent.DamageDealt, OnDamageDealt);
            EventCenter.Instance.RemoveEventListener(GameEvent.PieceDeployed, OnPieceDeployed);
            EventCenter.Instance.RemoveEventListener(GameEvent.PieceDied, OnPieceDied);
            EventCenter.Instance.RemoveEventListener(GameEvent.PiecePromoted, OnPiecePromoted);
            EventCenter.Instance.RemoveEventListener(GameEvent.PromoteAnnounced, OnPromoteAnnounced);
            EventCenter.Instance.RemoveEventListener(GameEvent.StateChanged, OnStateChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.BuffsChanged, OnBuffsChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.ExtraActionGranted, OnExtraActionGranted);
            EventCenter.Instance.RemoveEventListener(GameEvent.RelicObtained, OnRelicObtained);
            EventCenter.Instance.RemoveEventListener(GameEvent.HintRequested, OnHintRequested);
            if (_abilityFlashTween != null) { _abilityFlashTween.Kill(); _abilityFlashTween = null; }
            ClearCardQualifyHighlight();
            if (_handPosTween != null) _handPosTween.Kill();
            if (_handSizeTween != null) _handSizeTween.Kill();
            if (_phaseFlashTween != null) { _phaseFlashTween.Kill(); _phaseFlashTween = null; }
            if (_phaseTipShowing) { _phaseTipShowing = false; TooltipManager.Instance.Hide(); }
            if (_apEmptyTipShowing) { _apEmptyTipShowing = false; TooltipManager.Instance.Hide(); }
            if (_freeActionTipShowing) { _freeActionTipShowing = false; TooltipManager.Instance.Hide(); }
            _diceMoveSelecting = false;
            _diceMovePieceId = -1;
            _diceMoveDirections.Clear();
            _pendingForcedExecs.Clear();
            foreach (var go in _mahjongWallViews) if (go != null) Destroy(go);
            _mahjongWallViews.Clear();
            DestroyMahjongWallPreview();
            _pendingPromotionWarnings.Clear();
            foreach (var go in _shockWallViews) if (go != null) Destroy(go);
            _shockWallViews.Clear();
            if (_buyTokenBtn != null) _buyTokenBtn.onClick.RemoveListener(OnBuyTokenClicked);
            // 遗物按钮监听对称清理（L1——不依赖下个 BC 的 Init RemoveAllListeners 兜底）
            if (_relicBtn != null) _relicBtn.onClick.RemoveListener(ToggleRelicList);
            // UI 架构重构 §五：面板局内复用——只解绑不销毁（面板生命周期归 Bootstrap：局结束统一销毁）
            _panel = null;
            // 清理本战斗注册的视觉；不扫描全场景，避免误伤其他系统/调试对象。
            ClearHighlights();
            _pieceViews.DestroyAll();
        }

        /// <summary>退出战斗请求（Bootstrap 订阅——回主菜单）。</summary>
        public event System.Action OnExitRequested;

        public void Init(BattleFlow flow, GameState state, UIManager uiManager, BattlePanel panel)
        {
            _flow = flow;
            _state = state;
            _uiManager = uiManager;
            _boardRules = new BoardRules();
            _intentResolver = new IntentResolver(_boardRules);

            // 隐藏场景 Canvas 里的面板预览实例（运行时面板全部由 Addressables 加载，场景里的都是拼面板残留）
            var sceneCanvas = FindObjectOfType<Canvas>();
            if (sceneCanvas != null && sceneCanvas.transform.parent != null) sceneCanvas = null; // 只处理根 Canvas
            if (sceneCanvas != null)
            {
                foreach (Transform child in sceneCanvas.transform)
                {
                    if (child.GetComponent<PanelBase>() != null) child.gameObject.SetActive(false);
                }
            }

            EventCenter.Instance.AddEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
            EventCenter.Instance.AddEventListener(GameEvent.ActionPointChanged, OnAPChanged);
            EventCenter.Instance.AddEventListener(GameEvent.HandChanged, OnHandChanged);
            EventCenter.Instance.AddEventListener(GameEvent.PieceMoved, OnPieceMoved);
            EventCenter.Instance.AddEventListener(GameEvent.DamageDealt, OnDamageDealt);
            EventCenter.Instance.AddEventListener(GameEvent.PieceDeployed, OnPieceDeployed);
            EventCenter.Instance.AddEventListener(GameEvent.PieceDied, OnPieceDied);
            EventCenter.Instance.AddEventListener(GameEvent.PiecePromoted, OnPiecePromoted);
            EventCenter.Instance.AddEventListener(GameEvent.PromoteAnnounced, OnPromoteAnnounced);
            EventCenter.Instance.AddEventListener(GameEvent.StateChanged, OnStateChanged);
            EventCenter.Instance.AddEventListener(GameEvent.BuffsChanged, OnBuffsChanged); // buff 变化 → 刷新选中棋子信息面板
            EventCenter.Instance.AddEventListener(GameEvent.ExtraActionGranted, OnExtraActionGranted); // 免费行动授予 → 刷新并播放提示音
            EventCenter.Instance.AddEventListener(GameEvent.HintRequested, OnHintRequested); // 通用变亮通道（2026-08-23：E5 资格等提示）
            EventCenter.Instance.AddEventListener(GameEvent.RelicObtained, OnRelicObtained); // 道中获得遗物/能力 → 刷新全局能力栏

            // UI 架构重构 §五：面板局内缓存（Bootstrap 管理生命周期）——每场绑定不创建
            // 防御：面板未就绪（Bootstrap 保证——不应发生）——先检查再订阅（验收 B：防防御路径订阅残留）
            _panel = panel;
            if (_panel == null) return;
            panel.ResetCheatCount(); // 2026-08-26 测试自动过关：Ctrl+设置×10 计数每场战斗重置（防跨场累计误触发）
            panel.SetFloorName(Bootstrap.FloorDisplayName(state != null ? state.CurrentFloor : 0)); // 2026-08-26 左上角关卡名称（白模/Demo/ALPHA/BETA）
            _uiManager.RegisterPanel(panel); // 幂等覆盖（重复注册无害）
            PanelTransition.ShowWithLoading(_uiManager, "Battle");
            // ⚠️ 面板局内复用（UI 架构重构 §五）：旧 BC 的按钮监听残留在复用面板上——
            // 每场绑定前必须 RemoveAllListeners（否则第 2 场起点按钮触发多次回调）
            if (_panel.PhaseButton != null)
            {
                _panel.PhaseButton.onClick.RemoveAllListeners();
                _panel.PhaseButton.onClick.AddListener(OnPhaseButtonClicked);
                // 2026-08-23：准备阶段 hover 引导 tip（未部署完初始棋子时显示）
                var phaseHover = _panel.PhaseButton.gameObject.GetComponent<PhaseButtonHoverTip>();
                if (phaseHover == null) phaseHover = _panel.PhaseButton.gameObject.AddComponent<PhaseButtonHoverTip>();
                phaseHover.Init(this);
            }
            if (_panel.DrawButton != null)
            {
                _panel.DrawButton.onClick.RemoveAllListeners();
                _panel.DrawButton.onClick.AddListener(OnDrawButtonClicked);
            }
            if (_panel.ExitButton != null)
            {
                _panel.ExitButton.onClick.RemoveAllListeners();
                _panel.ExitButton.onClick.AddListener(() =>
                {
                    _panel.ExitButton.interactable = false; // 立即反馈——收尾延后 1 帧期间按钮置灰（防"点了没反应"）
                    OnExitRequested?.Invoke();
                });
            }
            // 遗物栏（2026-08-14）：Btn_Relic 切换列表显示；首次显示时填充图标
            _relicBtn = FindDeep(_panel.transform, "Btn_Relic")?.GetComponent<Button>();
            _relicDisplay = FindDeep(_panel.transform, "Grp_RelicDisplay") as RectTransform;
            if (_relicBtn != null)
            {
                _relicBtn.onClick.RemoveAllListeners();
                _relicBtn.onClick.AddListener(ToggleRelicList);
            }
            if (_relicDisplay != null) _relicDisplay.gameObject.SetActive(false); // 默认隐藏
            RefreshAll();
            _lastHandCount = _state.Hand != null ? _state.Hand.Count : 0;
            UpdateHandPositionByPhase(); // 初始阶段即应用手牌区状态（准备阶段高度 250）
            ClearPieceInfo(); // 初始：信息面板隐藏（无选中/无临时状态）
            // 补齐开局已有棋子视觉（首波部署早于控制器创建——PieceDeployed 事件已丢）
            SyncExistingPieces();
            // 2026-08-23：控制器异步创建时首波部署事件已丢失、视觉由上方补齐——若后端已在等该部署表现回执
            // （token != -1）→ 立即补回执（视觉同步即表现完成），防"无回执 → 3s 超时降级"
            var pendingToken = _flow != null ? _flow.CurrentPresentationToken : default;
            if (pendingToken.actionId != -1)
            {
                EventCenter.Instance.EventTrigger(GameEvent.PresentationFinished, new PresentationInfo
                {
                    SessionId = pendingToken.sessionId,
                    ActionId = pendingToken.actionId
                });
                Debug.Log($"[Battle] 开局视觉同步后补发部署表现回执 token=({pendingToken.sessionId},{pendingToken.actionId})");
            }
        }

        // ====== 遗物栏逻辑（2026-08-14）======

        /// <summary>
        /// 切换遗物列表。2026-08-15 重构（用户反馈）：原全屏 backdrop 点击层遮挡视线/hover——
        /// 改为无遮挡：只显示列表；关闭改由 Update 全局"点击外部"检测（见 UpdateRelicListOutsideClick）。
        /// </summary>
        void ToggleRelicList()
        {
            if (_relicDisplay == null) return;
            _relicListShown = !_relicListShown;
            if (_relicListShown)
            {
                RefreshRelicList();
                _relicDisplay.gameObject.SetActive(true);
                RefreshRelicListCanvasOrder(); // 列表置顶（在战斗面板其他 UI 之上可点/hover）
            }
            else
            {
                _relicDisplay.gameObject.SetActive(false);
                ResetRelicListCanvasOrder();
            }
        }

        /// <summary>
        /// 点击外部关闭（2026-08-15 替代 backdrop 全屏点击层——不遮挡视线、不阻塞 hover）：
        /// 列表显示时，左键按下 → raycast 命中点：Btn_Relic 上 → 跳过（按钮自身 toggle 关闭）；
        /// 遗物图标/列表内 → 保持（可继续 hover 看详情）；其他任何地方 → 关列表。
        /// 点击自然穿透到下层 UI（无全屏拦截）。
        /// </summary>
        void UpdateRelicListOutsideClick()
        {
            if (!_relicListShown) return;
            var mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;
            var es = EventSystem.current;
            if (es == null) return;
            if (_relicPointerData == null) _relicPointerData = new UnityEngine.EventSystems.PointerEventData(es);
            _relicPointerData.position = mouse.position.ReadValue();
            _relicRaycastResults.Clear();
            es.RaycastAll(_relicPointerData, _relicRaycastResults);
            foreach (var r in _relicRaycastResults)
            {
                if (r.gameObject == null) continue;
                if (_relicBtn != null && r.gameObject == _relicBtn.gameObject) return; // Btn_Relic 自己处理开关（防双关）
                if (_relicDisplay != null && r.gameObject.transform.IsChildOf(_relicDisplay)) return; // 列表/图标内 → 保持
            }
            CloseRelicList();
        }

        void CloseRelicList()
        {
            if (!_relicListShown) return;
            _relicListShown = false;
            _relicDisplay.gameObject.SetActive(false);
            ResetRelicListCanvasOrder();
        }

        /// <summary>列表 Canvas 置顶（层之上可点）；关闭时还原（防面板局内复用 sortingOrder 残留，M5）。</summary>
        void RefreshRelicListCanvasOrder()
        {
            if (_relicDisplay == null) return;
            var listCanvas = _relicDisplay.GetComponent<Canvas>();
            if (listCanvas == null) listCanvas = _relicDisplay.gameObject.AddComponent<Canvas>();
            listCanvas.overrideSorting = true;
            listCanvas.sortingOrder = 60; // 高于手牌层(50)，低于 Tooltip(1000)
            // ⚠️ 2026-08-15：overrideSorting 子 Canvas 必须有 GraphicRaycaster——否则该层不参与 EventSystem
            // 射线检测 → RelicIconHover 收不到 OnPointerEnter → hover 描述浮窗失效（用户反馈）
            if (_relicDisplay.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
            {
                _relicDisplay.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            }
        }

        /// <summary>还原列表 Canvas 层级（关闭列表时调用——避免 overrideSorting/sortingOrder 残留）。</summary>
        void ResetRelicListCanvasOrder()
        {
            if (_relicDisplay == null) return;
            var listCanvas = _relicDisplay.GetComponent<Canvas>();
            if (listCanvas != null)
            {
                listCanvas.overrideSorting = false; // 关闭时不强制置顶（回到面板默认渲染顺序）
            }
        }

        /// <summary>填充遗物列表：_state.Relics 每个 → Image.prefab 实例进 Grp_RelicDisplay（占位色块 + hover 描述）。</summary>
        void RefreshRelicList()
        {
            if (_relicDisplay == null || _state == null) return;
            _relicListGen++; // 代际递增：废弃旧 BuildRelicIcons 协程（M1——防连点并发重建叠加）
            // 清空重建（数据可能变化）——收集到临时 List 再 Destroy，避免延迟销毁期间新旧两批 icon 并存
            var toDestroy = new List<Transform>();
            foreach (Transform child in _relicDisplay) toDestroy.Add(child);
            foreach (var child in toDestroy) Destroy(child.gameObject);
            toDestroy.Clear();
            if (_state.Relics.Count == 0) return;
            StartCoroutine(BuildRelicIcons(_relicListGen));
        }

        System.Collections.IEnumerator BuildRelicIcons(int gen)
        {
            // 2026-08-23：原 Addressables "Image" 模板已删除（断链）——改为代码生成色块，不依赖 prefab（见 docs/能力事件显示-修复参考_20260823.md）
            if (_relicDisplay == null || _state == null) yield break;
            // 代际校验：yield 期间列表被重建/关闭 → 放弃（防写脏数据，M1）
            if (gen != _relicListGen || !_relicListShown) yield break;
            foreach (var relic in _state.Relics)
            {
                var go = new GameObject($"RelicIcon_{relic.name}");
                go.transform.SetParent(_relicDisplay, false);
                var img = go.AddComponent<UnityEngine.UI.Image>();
                if (img != null)
                {
                    img.color = ItemGettingPanel.RelicTint(relic); // 占位色块（与获取弹窗同色）
                    img.raycastTarget = true; // 可 hover/点击
                }
                // hover 描述浮窗（TooltipManager）
                var hover = go.GetComponent<RelicIconHover>();
                if (hover == null) hover = go.AddComponent<RelicIconHover>();
                hover.Init(relic);
            }
        }

        /// <summary>递归按名查找（容错 prefab 层级嵌套——BattlePanel 布局随美术调整）。</summary>
        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

        /// <summary>同步棋盘已有棋子视觉（开局首波/后续会话补齐——事件早于监听时视觉不创建）。</summary>
        void SyncExistingPieces()
        {
            foreach (var kv in _state.Pieces)
            {
                var piece = kv.Value;
                if (_pieceViews.Get(piece.Id) != null) continue;
                var view = PieceViewFactory.CreatePieceView(piece.Id, piece.DefId, piece.side, piece.position,
                    piece.side == Side.Player ? PieceViewFactory.TintFor(piece.DefId) : PieceViewFactory.TintFor(piece.DefId + 1));
                _pieceViews.Register(piece.Id, view);
                if (piece.element != Element.None) ApplyElementOutline(piece.Id); // 五行：开局/续战同步静态描边
            }
            if (_state.PromoteAnnouncements != null)
            {
                foreach (var announcement in _state.PromoteAnnouncements)
                {
                    if (announcement != null) CacheOrApplyPromotionWarning(announcement);
                }
            }
            ApplyPendingPromotionWarnings();
            RebuildShockWalls(); // 2026-08-26：震击墙（开局/续战同步）
        }

        void Update()
        {
            // 遗物列表"点击外部关闭"（2026-08-15：无遮挡方案——列表显示时全局监听下一次点击）
            UpdateRelicListOutsideClick();

            // 2026-08-12：activeInputHandler=2（纯 Input System）——旧 Input.GetMouseButtonDown 失效（恒 false/抛异常）
            // → 迁移 InputSystem API（与 BattleResultPanel/PieceEditPanel 一致）
            if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

            // 射线打 Tile（棋子无碰撞，Tile 有 BoxCollider）
            var ray = Camera.main != null ? Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue()) : default;
            if (ray.origin == default || !Physics.Raycast(ray, out var hit, 200f)) return;
            var cell = PieceViewFactory.CellFromWorld(hit.point);
            HandleBoardClick(cell);
        }

        // ========== 棋盘点击 ==========
        void HandleBoardClick(Vector2Int cell)
        {
            if (_state.Phase == BattlePhase.GameOver)
            {
                return; // 战斗结束：输入抑制（收尾延后窗口内禁止点击——规则层也拒绝，双保险）
            }
            if (_presentationPlaying)
            {
                return; // 表现播放中：所有操作等动画播完（防时序错乱）
            }
            if (_awaitingCell)
            {
                OnCellPicked(cell); // 执行中：当前槽选格
                return;
            }

            if (_diceMoveSelecting)
            {
                // 点数直线移动：只响应可达格（场上高亮——与移动选格同操作习惯）
                if (_diceMoveDirections.TryGetValue(cell, out var diceDir))
                {
                    _diceMoveSelecting = false;
                    _diceMovePieceId = -1;
                    ClearHighlights();
                    _flow.OnDiceDirectionSelected(diceDir);
                    RefreshDicePanel(); // 点数已消耗 → 面板刷新（先投掷提示）
                }
                return;
            }

            if (_executing) return; // 执行中（含免费行动强制执行）——锁定棋子：禁止改选/取消/新执行

            // 执行候选：玩家回合、非执行中、已选中我方单位
            bool canExecute = !_executing && _state.Phase == BattlePhase.PlayerTurn && _selectedPieceId >= 0;
            if (canExecute)
            {
                var sel = _state.GetPiece(_selectedPieceId);
                canExecute = sel != null && sel.side == Side.Player;
            }

            var piece = _state.GetPieceAt(cell);
            if (piece != null)
            {
                // 已选中我方单位时点敌方棋子 = 执行目标（槽0 范围校验；非法格落入选中切换）
                if (canExecute && piece.side != Side.Player && TryExecuteSelected(cell)) return;
                // 任意阶段：敌我棋子均可选中/取消（选中敌方仅查看信息与范围，不可执行）
                if (_selectedPieceId == piece.Id)
                {
                    ClearSelection();
                }
                else
                {
                    _selectedPieceId = piece.Id;
                    ShowPieceInfo(piece.Id);
                    PreviewRange(piece.Id); // 移动绿块 + 攻击红框同时显示
                }
                return;
            }

            // 空格：已选中我方单位 → 尝试执行（非法格不取消选中）；否则取消选中
            if (canExecute)
            {
                TryExecuteSelected(cell);
                return;
            }
            ClearSelection();
        }

        /// <summary>选中后显示首个有候选逻辑块的范围（移动=绿块；攻击=红框）。</summary>
        void PreviewRange(int pieceId)
        {
            var piece = _state.GetPiece(pieceId);
            if (piece == null) return;
            var program = piece.GetProgram(_state);
            if (program == null || program.Count == 0)
            {
                ClearHighlights();
                return;
            }

            // 镜像 TryExecuteSelected：跳过不产生行动的效果/Skip 槽及无候选动作槽。
            int index = 0;
            while (index < program.Count)
            {
                var slot = program[index];
                if (slot is SkipTemplate || slot is EffectTemplate) { index++; continue; }
                if (slot is MoveTemplate move && _intentResolver.GetMoveOptions(_state, piece, move).Count == 0) { index++; continue; }
                if (slot is AttackTemplate atk && _boardRules.GetAttackableCells(_state, piece, atk).Count == 0) { index++; continue; }
                break;
            }
            if (index >= program.Count)
            {
                ClearHighlights();
                return;
            }

            switch (program[index])
            {
                case MoveTemplate move:
                    ShowHighlights(_intentResolver.GetMoveOptions(_state, piece, move), null);
                    break;
                case AttackTemplate atk:
                    ShowHighlights(null, _boardRules.GetAttackableCells(_state, piece, atk));
                    break;
                default:
                    ClearHighlights();
                    break;
            }
        }

        /// <summary>点击目标格触发执行：格在首个有候选槽可选范围内 → 发 ExecuteRequest + 选格。</summary>
        bool TryExecuteSelected(Vector2Int cell)
        {
            var piece = _state.GetPiece(_selectedPieceId);
            if (piece == null) return false;
            var program = piece.GetProgram(_state);
            if (program == null || program.Count == 0) return false;

            // 镜像预推进：与规则层 Skip 判定一致，对齐到第一个需要选格的槽（空候选 Move/Attack + Skip 槽全部跳过）
            int execIndex = 0;
            while (execIndex < program.Count)
            {
                var s = program[execIndex];
                if (s is SkipTemplate || s is EffectTemplate) { execIndex++; continue; }
                if (s is MoveTemplate mm && _intentResolver.GetMoveOptions(_state, piece, mm).Count == 0) { execIndex++; continue; }
                if (s is AttackTemplate aa && _boardRules.GetAttackableCells(_state, piece, aa).Count == 0) { execIndex++; continue; }
                break;
            }
            if (execIndex >= program.Count) return false; // 全程序无候选：无可执行内容

            // 首个有候选槽的目标校验（与规则层契约一致：范围外不执行，防镜像死锁）
            var slot0 = program[execIndex];
            if (slot0 is MoveTemplate move)
            {
                var opts = _intentResolver.GetMoveOptions(_state, piece, move);
                if (!opts.Contains(cell)) return false; // 非可选格：不执行
            }
            else if (slot0 is AttackTemplate atk)
            {
                var opts = _boardRules.GetAttackableCells(_state, piece, atk);
                if (!opts.Contains(cell)) return false;
            }

            _executing = true;
            _execPieceId = _selectedPieceId;
            _execProgram = program;
            _execIndex = execIndex;
            _flow.OnPlayerRequestExecute(new ExecuteRequest(_selectedPieceId));
            _flow.OnPlayerCellSelected(cell); // 首个有候选槽选格（规则层已等待）
            StartCoroutine(WaitSelectResult());
            return true;
        }

        // ========== 执行镜像 ==========

        /// <summary>镜像逐槽推进：与规则层同步（Skip/无选项自动跳过；Move/Attack 槽等选格）。</summary>
        void AdvanceExec()
        {
            while (_executing)
            {
                var piece = _state.GetPiece(_execPieceId);
                if (piece == null || _execIndex >= _execProgram.Count)
                {
                    FinishExec();
                    return;
                }
                var slot = _execProgram[_execIndex];
                if (slot is EffectTemplate)
                {
                    _execIndex++;
                    continue;
                }
                switch (slot)
                {
                    case MoveTemplate move:
                        var opts = _intentResolver.GetMoveOptions(_state, piece, move);
                        if (opts.Count == 0)
                        {
                            _execIndex++;
                            continue; // 规则层同判定：自动跳过
                        }
                        EnterCellSelect(opts, isMove: true);
                        return;
                    case AttackTemplate atk:
                        // 与规则层同判定：无候选走 Skip（防两侧错位 + 永久选格死锁）
                        if (_boardRules.GetAttackableCells(_state, piece, atk).Count == 0)
                        {
                            _execIndex++;
                            continue;
                        }
                        EnterCellSelect(null, isMove: false); // 攻击全盘可点
                        return;
                    default: // SkipTemplate 等
                        _execIndex++;
                        continue;
                }
            }
        }

        void EnterCellSelect(List<Vector2Int> options, bool isMove)
        {
            _awaitingCell = true;
            _isMoveSelect = isMove;
            if (isMove)
            {
                _cellOptions = options ?? new List<Vector2Int>();
                ShowHighlights(_cellOptions, null); // 绿块
            }
            else
            {
                // 攻击：任意格可点，但显示攻击范围（红框）
                var piece = _state.GetPiece(_execPieceId);
                if (piece != null && _execProgram != null && _execIndex < _execProgram.Count
                    && _execProgram[_execIndex] is AttackTemplate atk)
                {
                    _cellOptions = _boardRules.GetAttackableCells(_state, piece, atk);
                    ShowHighlights(null, _cellOptions); // 红框
                }
                else
                {
                    _cellOptions = new List<Vector2Int>();
                    ClearHighlights();
                }
            }
        }

        void OnCellPicked(Vector2Int cell)
        {
            if (_isMoveSelect && !_cellOptions.Contains(cell)) return; // 移动只允许可选格
            _flow.OnPlayerCellSelected(cell);
            StartCoroutine(WaitSelectResult());
        }

        /// <summary>选格后帧缓冲：规则层同步落账并发表现事件 → 成功则表现队列接管；失败则回退选格态（防死锁）。</summary>
        IEnumerator WaitSelectResult()
        {
            yield return null;
            if (_selectResultDirty)
            {
                _selectResultDirty = false;
                _awaitingCell = false;
                ClearHighlights();
                // 表现播完（PresentationLoop 末尾）会触发 AdvanceAfterPresentation
            }
            else if (_executing && _state.Phase == BattlePhase.PlayerTurn)
            {
                _awaitingCell = true; // 仍在本回合执行中：回退当前槽选格态（规则层拒绝/无事件）
            }
            else
            {
                _awaitingCell = false; // 执行已结束/阶段已切换：陈旧请求，不再 re-arm（防永久吞点击）
            }
        }

        void FinishExec()
        {
            _executing = false;
            _execPieceId = -1;
            _execProgram = null;
            // 退出逻辑链：清选中 + 清高亮（单位已行动过，再显示范围会误导玩家）
            ClearSelection();
            ClearHighlights();
            // 清选格态（防陈旧 WaitSelectResult 协程 re-arm 吞掉后续棋盘点击）
            _awaitingCell = false;
            _selectResultDirty = false;
            RefreshAP();
            RefreshDrawPile();
            TryStartForcedExec(); // 插入执行：整段执行收尾后强制下一免费行动（镜像后端 FinishExecute→TryFlushImmediateExecutes）
        }

        /// <summary>表现组播完后：镜像推进下一槽（规则层已 AdvanceSlot 到等待/结束）。</summary>
        void AdvanceAfterPresentation()
        {
            if (!_executing) return;
            _execIndex++;
            AdvanceExec();
        }

        // ========== 表现时间常量（大审查 R3：魔法数字集中——调手感只改此处）==========
        const float MoveDuration = 0.25f;  // 移动动画时长
        const float MoveWait = 0.3f;       // 移动后等待
        const float AttackFlash = 0.06f;   // 攻击者挥动闪白
        const float HitFlash = 0.08f;      // 目标受击闪白
        const float DamageWait = 0.15f;    // 伤害表现后等待
        const float DeployWait = 0.1f;     // 部署表现等待
        const float DeathFade = 0.2f;      // 死亡淡出/缩放
        const float DeathWait = 0.25f;     // 死亡后销毁等待

        // ========== 表现协议 ==========
        void EnqueuePresentation(System.Func<IEnumerator> play)
        {
            _presentations.Add(play);
            if (!_presentationPlaying) StartCoroutine(PresentationLoop());
        }

        IEnumerator PresentationLoop()
        {
            _presentationPlaying = true;
            yield return null; // 帧缓冲：合并同槽伴随事件（DamageDealt+PieceDied）→ 同批为一组
            while (_presentations.Count > 0)
            {
                // 组内并行（架构 §四.7：同槽表现并行、槽间串行——AOE 多目标同时闪白）
                var batch = new List<System.Func<IEnumerator>>(_presentations);
                _presentations.Clear();
                // 在本组启动时快照 token。回执是同步事件，会推进规则层并可能生成下一组 token，不能在回执后再读取。
                var batchToken = _flow != null ? _flow.CurrentPresentationToken : default;
                _batchFlashAttackers = new HashSet<int>(); // 组内攻击者只闪一次
                int pending = batch.Count;
                foreach (var play in batch)
                {
                    StartCoroutine(PlayWithCount(play, () => pending--));
                }
                // 前端兜底（2026-08-23）：慢机/重帧时表现协程可能 >3s 才播完 → 后端超时降级（LogError）。
                // 批次等待用 scaled time（暂停中不触发），超过 2s 未播完 → 强制收尾回执（防后端 3s 超时；迟到动画无害）
                float batchStartTime = UnityEngine.Time.time;
                while (pending > 0)
                {
                    if (UnityEngine.Time.time - batchStartTime > 2f)
                    {
                        pending = 0; // 放弃等待：直接回执（token 仍是本组快照——后端正常推进）
                        Debug.LogWarning($"[BattleController] 表现批次 {batch.Count} 项 2s 未播完——前端强制收尾回执（表现协程疑似卡住，本次已放行）");
                        break;
                    }
                    yield return null; // 组内全部完成 → 回执一次
                }
                _batchFlashAttackers = null;
                EventCenter.Instance.EventTrigger(GameEvent.PresentationFinished, new PresentationInfo
                {
                    SessionId = batchToken.sessionId,
                    ActionId = batchToken.actionId
                });
                if (_executing) AdvanceAfterPresentation();
            }
            _presentationPlaying = false;
            RefreshDrawPile();
            TryStartForcedExec(); // 插入执行：表现排空后（后端 _waitingPresentation 结束同帧）触发待执行免费行动
        }

        /// <summary>组内并行子协程：播完计数（finally 保证异常/中断也计数——防组等待卡死）。</summary>
        System.Collections.IEnumerator PlayWithCount(System.Func<IEnumerator> play, System.Action onDone)
        {
            try
            {
                yield return play();
            }
            finally
            {
                onDone();
            }
        }

        void OnPieceMoved(object data)
        {
            var info = (MoveInfo)data;
            _selectResultDirty = true;
            EnqueuePresentation(() => PlayMove(info));
            // 非执行中选中单位被移动（敌方回合/波次调度）：按新位置刷新范围
            if (!_executing && info.PieceId == _selectedPieceId)
            {
                PreviewRange(_selectedPieceId);
            }
        }

        void OnDamageDealt(object data)
        {
            var info = (DamageInfo)data;
            _selectResultDirty = true;
            EnqueuePresentation(() => PlayDamage(info));
            // 伤害不会必然触发 Buff/死亡事件；选中单位存活时也必须刷新耐久显示。
            if (info.AttackerId == _selectedPieceId || info.TargetId == _selectedPieceId)
            {
                var selected = _state.GetPiece(_selectedPieceId);
                if (selected != null) FillInfo(selected.def, selected);
            }
        }

        void OnPieceDeployed(object data)
        {
            var info = (DeployInfo)data;
            EnqueuePresentation(() => PlayDeploy(info));
            if (info.Side == Side.Player)
            {
                // 手牌变化由 Resolver.HandChanged 统一驱动；此处只保留阶段按钮刷新兜底。
                RefreshPhaseButton();
            }
        }

        void OnPieceDied(object data)
        {
            var info = (DeathInfo)data;
            _pendingPromotionWarnings.Remove(info.PieceId);
            var dyingView = FindPromotionView(info.PieceId);
            if (dyingView != null) dyingView.HideWarning();
            // 选中单位死亡：清选中 + 清高亮（防残留高亮指向死棋子）
            if (info.PieceId == _selectedPieceId)
            {
                ClearSelection();
                ClearHighlights();
            }
            RefreshScore(); // 击杀敌方后 BaseScore 已落账；表现播放期间也应显示本回合累计
            EnqueuePresentation(() => PlayDeath(info));
        }

        void OnPromoteAnnounced(object data)
        {
            if (data is PromoteAnnouncement announcement) CacheOrApplyPromotionWarning(announcement);
        }

        void OnPiecePromoted(object data)
        {
            if (!(data is PromoteInfo info)) return;
            _pendingPromotionWarnings.Remove(info.PieceId);
            var pieceView = _pieceViews.Get(info.PieceId);
            if (pieceView != null)
            {
                PieceViewFactory.UpdatePortrait(pieceView, info.NewDefId);
                var outline = FindPromotionView(info.PieceId);
                if (outline != null) outline.PlayPromotionFlash();
            }
            AudioManager.Instance.PlaySFX(AudioRefs.SfxPromote);
            if (info.PieceId == _selectedPieceId)
            {
                var piece = _state.GetPiece(info.PieceId);
                if (piece != null) FillInfo(piece.def, piece);
                PreviewRange(info.PieceId);
            }
            // 手牌变化由 Resolver.HandChanged 统一驱动；属性升变可能改变倍率，立即刷新计分。
            RefreshScore();
        }

        void CacheOrApplyPromotionWarning(PromoteAnnouncement announcement)
        {
            if (announcement == null) return;
            var outline = FindPromotionView(announcement.pieceId);
            if (outline == null)
            {
                _pendingPromotionWarnings[announcement.pieceId] = announcement;
                return;
            }
            _pendingPromotionWarnings.Remove(announcement.pieceId);
            outline.ShowWarning();
        }

        void ApplyPendingPromotionWarnings()
        {
            if (_pendingPromotionWarnings.Count == 0) return;
            var ids = new List<int>(_pendingPromotionWarnings.Keys);
            foreach (var pieceId in ids)
            {
                if (_pendingPromotionWarnings.TryGetValue(pieceId, out var announcement))
                    CacheOrApplyPromotionWarning(announcement);
            }
        }

        PromotionOutlineView FindPromotionView(int pieceId)
        {
            var pieceView = _pieceViews.Get(pieceId);
            var portrait = pieceView != null ? pieceView.transform.Find("Portrait") : null;
            if (portrait == null) return null;
            var outline = portrait.GetComponent<PromotionOutlineView>();
            if (outline == null)
            {
                var renderer = portrait.GetComponent<SpriteRenderer>();
                if (renderer == null) return null;
                outline = portrait.gameObject.AddComponent<PromotionOutlineView>();
                outline.Initialize(renderer);
            }
            return outline;
        }

        /// <summary>五行（2026-08-25）：棋子带元素 → 静态描边（复用升变预告描边组件，五色不闪烁）。</summary>
        void ApplyElementOutline(int pieceId)
        {
            var piece = _state != null ? _state.GetPiece(pieceId) : null;
            if (piece == null || piece.element == Element.None) return;
            var outline = FindPromotionView(pieceId);
            if (outline != null) outline.SetElementColor(ElementColors.ColorOf(piece.element));
        }

        /// <summary>震击墙渲染（2026-08-26）：按 GameState.ShockWalls 生成深灰半透明方块（不可破坏墙；无美术前占位视觉——区别麻将墙暂无视觉）。</summary>
        void RebuildShockWalls()
        {
            foreach (var go in _shockWallViews) if (go != null) Destroy(go);
            _shockWallViews.Clear();
            if (_state == null || _state.ShockWalls == null) return;
            foreach (var cell in _state.ShockWalls)
            {
                var go = new GameObject($"ShockWall_{cell.x}_{cell.y}");
                go.transform.position = PieceViewFactory.CellToWorld(cell);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = ShockWallSprite();
                sr.color = new Color(0.25f, 0.25f, 0.3f, 0.9f);
                sr.sortingOrder = 300; // 棋子（400+）之下
                _shockWallViews.Add(go);
            }
        }

        static Sprite _shockWallSprite;
        static Sprite ShockWallSprite()
        {
            if (_shockWallSprite != null) return _shockWallSprite;
            var tex = new Texture2D(100, 100, TextureFormat.RGBA32, false);
            var px = new Color[100 * 100];
            for (int i = 0; i < px.Length; i++) px[i] = Color.white;
            tex.SetPixels(px);
            tex.Apply();
            _shockWallSprite = Sprite.Create(tex, new Rect(0, 0, 100, 100), new Vector2(0.5f, 0.5f), 100f); // 1×1 单位
            return _shockWallSprite;
        }

        /// <summary>buff 变化（护盾/免费行动/临时能力）：目标是当前选中棋子 → 刷新信息面板（Txt_Other buff 区实时更新）。</summary>
        void OnBuffsChanged(object data)
        {
            if (!(data is int pieceId)) return;
            ApplyElementOutline(pieceId); // 2026-08-26：提纯/变换外力改写属性 → 五行描边实时刷新（后端逐棋发 BuffsChanged）
            if (pieceId != _selectedPieceId || _selectedPieceId < 0) return;
            var piece = _state.GetPiece(_selectedPieceId);
            if (piece != null && _infoName != null)
            {
                FillInfo(piece.def, piece);
            }
        }

        void OnExtraActionGranted(object data)
        {
            OnBuffsChanged(data);
            AudioManager.Instance.PlaySFX(AudioRefs.SfxFreeAction);
            // 2026-08-24 定案：玩家侧免费行动 = 插入执行（后端强制）——提示条 + 自动锁定 + 弹目标选择 + 允许空放
            if (!(data is int pieceId)) return;
            var grantedPiece = _state != null ? _state.GetPiece(pieceId) : null;
            if (grantedPiece == null || grantedPiece.side != Side.Player) return; // 仅玩家侧（敌方 AI 自动选格）
            _pendingForcedExecs.Enqueue(pieceId);
            ShowFreeActionTip(pieceId);
            TryStartForcedExec();
        }

        /// <summary>插入执行镜像启动：与后端 TryFlushImmediateExecutes 同时机（玩家回合 + 空闲）——出队强制该棋执行（free 由后端落账）。</summary>
        void TryStartForcedExec()
        {
            while (_pendingForcedExecs.Count > 0)
            {
                if (_executing || _presentationPlaying) return; // 非空闲——执行/表现收尾后再触发
                if (_state == null || _state.Phase != BattlePhase.PlayerTurn) return; // 与后端一致：仅玩家回合
                int pieceId = _pendingForcedExecs.Dequeue();
                var piece = _state.GetPiece(pieceId);
                if (piece == null || piece.side != Side.Player) continue; // 已不在/非玩家——跳过
                var program = piece.GetProgram(_state);
                if (program == null || program.Count == 0) continue; // 无程序——跳过
                // 自动选中并锁定该棋子（锁定 = 执行中 OnCellClicked 禁止改选）
                _selectedPieceId = pieceId;
                ShowPieceInfo(pieceId);
                _executing = true;
                _execPieceId = pieceId;
                _execProgram = program;
                _execIndex = 0;
                AdvanceExec(); // 与后端 AdvanceSlot 同步：跳无效槽 → 弹目标选择（高亮）
                return; // 一次一个（执行收尾点再次触发——镜像后端）
            }
        }

        /// <summary>免费行动提示条（2026-08-24 插入执行定案——提示 + 自动锁定；Tooltip 短暂显示防连发）。</summary>
        void ShowFreeActionTip(int pieceId)
        {
            if (_freeActionTipShowing) return;
            var piece = _state != null ? _state.GetPiece(pieceId) : null;
            if (piece == null) return;
            var view = _pieceViews.Get(pieceId);
            if (view == null) return;
            _freeActionTipShowing = true;
            TooltipManager.Instance.Show("免费行动！该棋子立即执行", view.transform.position);
            StartCoroutine(HideFreeActionTipLater());
        }

        IEnumerator HideFreeActionTipLater()
        {
            yield return new WaitForSeconds(FreeActionTipDuration);
            _freeActionTipShowing = false;
            TooltipManager.Instance.Hide();
        }

        void OnRelicObtained(object data)
        {
            RefreshEventAbilities();
            RefreshGoPanel(); // 速攻/买子等能力 → 围棋容量/面板刷新
            RefreshDicePanel(); // 玩法激活/能力 → 骰子面板刷新
        }

        // ========== 玩法面板·围棋（2026-08-24：手牌式拖拽部署——牌不消耗；Grp_FloorPlay_Go 与棋子信息区同位置切换显隐）==========

        void EnsureGoRefs()
        {
            if (_grpPlayRoot != null && _grpFloorPlayGo != null) return; // 2026-08-26：Grp_Play 槽位重建后需能重新解析（原仅判 root——重建销毁面板后漏解析）
            _grpPlayRoot = FindSceneTransform("Grp_Play");
            if (_grpPlayRoot == null) return;
            var goPanel = _grpPlayRoot.Find("Grp_FloorPlay_Go");
            _grpFloorPlayGo = goPanel != null ? goPanel.gameObject : null;
            if (_grpFloorPlayGo == null) return;
            var card = FindDeep(_grpFloorPlayGo.transform, "Piece_Handcard");
            _goCard = card != null ? card.gameObject : null;
            _goCountText = GetTmp(_grpFloorPlayGo, "Txt_Count");
            _goNextText = GetTmp(_grpFloorPlayGo, "Txt_Next");
            _goHintText = GetTmp(_grpFloorPlayGo, "Txt_Hint"); // Txt_Tip 常驻文本预制体预设——不解析不写入
            var buyBtnTransform = FindDeep(_grpFloorPlayGo.transform, "Btn_BuyGo");
            _goBuyBtn = buyBtnTransform != null ? buyBtnTransform.GetComponent<Button>() : null;
            _goBuyBtnText = _goBuyBtn != null ? _goBuyBtn.GetComponentInChildren<TMP_Text>(true) : null;
            if (_goBuyBtn != null)
            {
                _goBuyBtn.onClick.RemoveListener(OnBuyGoClicked); // 每场战斗重接线（防重复监听）
                _goBuyBtn.onClick.AddListener(OnBuyGoClicked);
                var tip = _goBuyBtn.GetComponent<GoBuyButtonTip>();
                if (tip == null) tip = _goBuyBtn.gameObject.AddComponent<GoBuyButtonTip>();
                tip.Init(this, _goBuyBtn);
            }
            if (_goCard != null)
            {
                var drag = _goCard.GetComponent<GoCardDrag>();
                if (drag == null) drag = _goCard.AddComponent<GoCardDrag>();
                drag.Init(this); // 每场战斗重接线（防旧控制器野引用）
                _goCardCg = _goCard.GetComponent<CanvasGroup>();
                if (_goCardCg == null) _goCardCg = _goCard.AddComponent<CanvasGroup>();
            }
        }

        Transform FindSceneTransform(string name)
        {
            foreach (var go in FindObjectsOfType<GameObject>(true))
            {
                if (go.name == name) return go.transform;
            }
            return null;
        }

        /// <summary>Grp_Mode 整体刷新：介绍按钮（未加载=禁用"未加载"）+ Grp_Play 槽位填充（玩法面板/None）+ 整组显隐（选中棋子隐藏——与棋子信息区同位置切换，李毕契约）。</summary>
        void RefreshGrpPlayVisibility()
        {
            RefreshFloorMode();
        }

        // ====== Grp_Mode（2026-08-26：玩法详情按钮 + 玩法区预制体填充）======
        void EnsureGrpModeRefs()
        {
            if (_grpModeRoot != null && _grpIntroductionRoot != null && _introButtons.Count >= 3) return;
            if (_grpModeRoot == null) _grpModeRoot = FindSceneTransform("Grp_Mode");
            if (_grpIntroductionRoot == null && _grpModeRoot != null) _grpIntroductionRoot = _grpModeRoot.Find("Grp_Introduction");
            if (_grpIntroductionRoot != null)
            {
                _introButtons.Clear();
                _introButtonTexts.Clear();
                for (int i = 0; i < 3; i++)
                {
                    var btnT = FindDeep(_grpIntroductionRoot, "Btn_DetailedIntroduction" + (i + 1));
                    var btn = btnT != null ? btnT.GetComponent<Button>() : null;
                    _introButtons.Add(btn);
                    TMP_Text txt = btnT != null ? btnT.GetComponentInChildren<TMP_Text>(true) : null;
                    _introButtonTexts.Add(txt);
                    if (btn != null)
                    {
                        int idx = i;
                        btn.onClick.RemoveListener(() => OnIntroButtonClicked(idx));
                        btn.onClick.AddListener(() => OnIntroButtonClicked(idx));
                    }
                }
            }
        }

        /// <summary>激活玩法列表（按 StyleRegistry.All 固定顺序，上限 3——第 2-4 关各一次玩法选择）。</summary>
        List<string> GetActiveStylesOrdered()
        {
            var list = new List<string>(3);
            if (_state == null) return list;
            foreach (var style in StyleRegistry.All)
            {
                if (_state.IsStyleActive(style)) list.Add(style);
                if (list.Count >= 3) break;
            }
            return list;
        }

        /// <summary>玩法面板预制体后缀（与 Addressables 注册地址 Grp_FloorPlay_* 对齐——styleId 为小写，面板名首字母大写；Element 面板名 = WuXing）。</summary>
        static string PlayPanelPrefabSuffix(string styleId)
        {
            switch (styleId)
            {
                case StyleRegistry.Element: return "WuXing";
                case StyleRegistry.Mahjong: return "Mahjong";
                case StyleRegistry.Dice: return "Dice";
                case StyleRegistry.Go: return "Go";
                case StyleRegistry.Token: return "Token";
                default: return styleId;
            }
        }

        // ========== 玩法面板·麻将（2026-08-27：牌山两张手牌卡 + 番数 + 和牌按钮；手牌麻将卡点击=摸切、拖拽=打墙）==========

        void EnsureMahjongRefs()
        {
            if (_grpFloorPlayMahjong != null) return;
            if (_grpPlayRoot == null) _grpPlayRoot = FindSceneTransform("Grp_Play");
            if (_grpPlayRoot == null) return;
            var panel = _grpPlayRoot.Find("Grp_FloorPlay_Mahjong");
            _grpFloorPlayMahjong = panel != null ? panel.gameObject : null;
            if (_grpFloorPlayMahjong == null) return;
            var fan = FindDeep(_grpFloorPlayMahjong.transform, "Txt_MahjongFan");
            _mahjongFanText = fan != null ? fan.GetComponent<TMP_Text>() : null;
            var s1 = FindDeep(_grpFloorPlayMahjong.transform, "Txt_MahjongScore_1");
            _mahjongScoreText1 = s1 != null ? s1.GetComponent<TMP_Text>() : null;
            var s2 = FindDeep(_grpFloorPlayMahjong.transform, "Txt_MahjongScore_2");
            _mahjongScoreText2 = s2 != null ? s2.GetComponent<TMP_Text>() : null;
            var hu = FindDeep(_grpFloorPlayMahjong.transform, "Btn_Hu");
            _huBtn = hu != null ? hu.GetComponent<Button>() : null;
            if (_huBtn != null)
            {
                _huBtn.onClick.RemoveListener(OnHuClicked); // 每场战斗重接线（防重复监听）
                _huBtn.onClick.AddListener(OnHuClicked);
            }
            // 占位手牌卡（李毕拼的 Piece_Handcard 实例）= 牌山 1/2 号位锚点；运行时隐藏，由生成卡接管位置
            int anchorIdx = 0;
            foreach (var pv in _grpFloorPlayMahjong.GetComponentsInChildren<HandCardView>(true))
            {
                if (anchorIdx >= 2) break;
                if (pv.transform.parent == _grpFloorPlayMahjong.transform)
                {
                    _mahjongCardAnchors[anchorIdx] = pv.transform;
                    pv.gameObject.SetActive(false);
                    anchorIdx++;
                }
            }
            if (anchorIdx == 0) // 兜底：按名找直接子节点（模板无 HandCardView 时）
            {
                foreach (Transform child in _grpFloorPlayMahjong.transform)
                {
                    if (anchorIdx >= 2) break;
                    if (child.name == "Piece_Handcard")
                    {
                        _mahjongCardAnchors[anchorIdx] = child;
                        child.gameObject.SetActive(false);
                        anchorIdx++;
                    }
                }
            }
            StartCoroutine(EnsureMahjongCardTemplate());
        }

        IEnumerator EnsureMahjongCardTemplate()
        {
            if (_mahjongCardTemplate != null || _grpFloorPlayMahjong == null) yield break;
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Piece_Handcard");
            yield return handle;
            if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && handle.Result != null)
            {
                _mahjongCardTemplate = handle.Result;
                RefreshMahjongPanel();
            }
        }

        /// <summary>牌山/番数/和牌按钮刷新（StateChanged("mahjong-score"/"mahjong-hu") 与槽位重建后调用）。</summary>
        void RefreshMahjongPanel()
        {
            if (_grpFloorPlayMahjong == null) return;
            if (_mahjongFanText != null) _mahjongFanText.text = _state != null ? _state.FanCount.ToString() : "0";
            var score = _state != null ? _state.MahjongScore : null;
            int GetScore(int i) => score != null && i < score.Count ? score[i] : 0;
            if (_mahjongScoreText1 != null) _mahjongScoreText1.text = GetScore(0).ToString();
            if (_mahjongScoreText2 != null) _mahjongScoreText2.text = GetScore(1).ToString();
            for (int i = 0; i < 2; i++)
            {
                bool has = score != null && i < score.Count;
                if (!has)
                {
                    if (_mahjongPanelCards[i] != null) _mahjongPanelCards[i].SetActive(false);
                    continue;
                }
                if (_mahjongPanelCards[i] == null && _mahjongCardTemplate != null && i < _mahjongCardAnchors.Length && _mahjongCardAnchors[i] != null)
                {
                    var anchor = _mahjongCardAnchors[i];
                    var view = UIComponentFactory.CreateHandCard(_mahjongCardTemplate, anchor.parent, MahjongCardData(score[i], true)); // 牌山 1x2
                    var card = view.gameObject;
                    card.name = $"MahjongPanelCard_{i + 1}";
                    card.transform.position = anchor.position;
                    card.transform.localScale = anchor.localScale;
                    card.transform.SetSiblingIndex(anchor.GetSiblingIndex());
                    if (card.GetComponent<CardHoverScale>() == null) card.AddComponent<CardHoverScale>(); // 仅放大不上浮（李毕定案）
                    _mahjongPanelCards[i] = card;
                }
                if (_mahjongPanelCards[i] != null)
                {
                    _mahjongPanelCards[i].SetActive(true);
                    var view = _mahjongPanelCards[i].GetComponent<HandCardView>();
                    if (view != null) view.Bind(MahjongCardData(score[i], true)); // 牌山 1x2
                }
            }
            if (_huBtn != null) _huBtn.interactable = CanHu();
        }

        /// <summary>麻将牌卡数据（牌山/手牌共用——复用 Piece_Handcard：名字=麻将、价值=点数）。
        /// 占地：牌山 1x2，手牌麻将 1x1；类型位 = 麻将点数图标（2026-08-27）。</summary>
        HandCardViewData MahjongCardData(int point, bool isWall = false)
        {
            return new HandCardViewData(Color.white, "", "麻将", point.ToString(), "", null,
                Element.None, isWall ? Footprint.Size1x2 : Footprint.Size1x1, "mahjong_type_" + point);
        }

        void OnHuClicked()
        {
            if (!CanHu() || _flow == null) return;
            _flow.OnPlayerRequestHu(new HuRequest());
        }

        /// <summary>和牌条件（与后端一致）：番数 > 0 且手牌有雀头（任意两牌价值相同）。</summary>
        bool CanHu()
        {
            if (_state == null || _state.FanCount <= 0 || _state.Hand == null) return false;
            var counts = new Dictionary<int, int>();
            foreach (var c in _state.Hand)
            {
                int v = c.IsMahjong ? c.value : ValueOfDef(c.defId);
                counts.TryGetValue(v, out int n);
                counts[v] = n + 1;
                if (n + 1 >= 2) return true;
            }
            return false;
        }

        int ValueOfDef(int defId)
        {
            var def = ConfigTable.Find<PieceDef>(defId);
            return def != null ? GetEffectiveValue(def) : 0;
        }

        // ====== 麻将场墙视觉（2026-08-27：占位灰块——同震击墙做法；打出/破坏都刷新）======

        void RebuildMahjongWalls()
        {
            foreach (var go in _mahjongWallViews) if (go != null) Destroy(go);
            _mahjongWallViews.Clear();
            if (_state == null || _state.MahjongWalls == null) return;
            foreach (var kv in _state.MahjongWalls)
            {
                var go = new GameObject($"MahjongWall_{kv.Key.x}_{kv.Key.y}");
                go.transform.position = PieceViewFactory.CellToWorld(kv.Key);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = MahjongWallSprite();
                sr.color = new Color(0.55f, 0.5f, 0.35f, 0.9f);
                sr.sortingOrder = 300; // 棋子（400+）之下
                _mahjongWallViews.Add(go);
            }
        }

        static Sprite MahjongWallSprite()
        {
            if (_mahjongWallSprite != null) return _mahjongWallSprite;
            var tex = new Texture2D(100, 100, TextureFormat.RGBA32, false);
            var px = new Color[100 * 100];
            for (int i = 0; i < px.Length; i++) px[i] = Color.white;
            tex.SetPixels(px);
            tex.Apply();
            _mahjongWallSprite = Sprite.Create(tex, new Rect(0, 0, 100, 100), new Vector2(0.5f, 0.5f), 100f); // 1×1 单位
            return _mahjongWallSprite;
        }

        // ====== 麻将战斗音（2026-08-27：SfxMahjongTile 统一——打出/牌山[音高随点数]/和牌）======

        void PlayMahjongStateSfx(string key)
        {
            if (key == "mahjong-wall")
            {
                AudioManager.Instance.PlaySFX(AudioRefs.SfxMahjongTile, 1f, 0.9f); // 打出墙体
            }
            else if (key == "mahjong-hu")
            {
                AudioManager.Instance.PlaySFX(AudioRefs.SfxMahjongTile, 1f, 0.7f); // 和牌
            }
            else if (key == "mahjong-score" && _state != null && _state.MahjongScore.Count > 0)
            {
                int point = _state.MahjongScore[_state.MahjongScore.Count - 1];
                AudioManager.Instance.PlaySFX(AudioRefs.SfxMahjongTile, 1f, 0.8f + point * 0.08f); // 牌山填数（音高随点数）
            }
        }

        // ====== 麻将手牌卡交互（2026-08-27 定案：点击=摸切、拖拽=打墙）======

        void AddMahjongCardDrag(GameObject card, int value, int instanceId)
        {
            var drag = card.AddComponent<MahjongCardDrag>();
            drag.Init(this, value);
        }

        public void OnMahjongCardClicked(int value)
        {
            if (_state == null || _flow == null) return;
            if (_executing || _presentationPlaying) return;
            if (_state.Phase != BattlePhase.PlayerTurn || _state.PlayerAP < 1) return;
            if (!_state.IsStyleActive(StyleRegistry.Mahjong)) return;
            _flow.OnPlayerRequestMochi(new MochiRequest(value)); // 摸切：填牌山+抽一张（1 AP——后端校验）
        }

        public void OnMahjongDragStart(int value, GameObject card)
        {
            if (_state == null || _flow == null) return;
            if (_executing || _presentationPlaying) return;
            if (_state.Phase != BattlePhase.PlayerTurn || _state.PlayerAP < 1) return;
            if (!_state.IsStyleActive(StyleRegistry.Mahjong)) return;
            _mahjongDragValue = value;
            _mahjongDragCard = card;
            SetHandLayoutDragging(true); // 拖拽期间冻结手牌 hover/让位
            CreateMahjongWallPreview();
            if (card != null)
            {
                var cg = card.GetComponent<CanvasGroup>();
                if (cg == null) cg = card.AddComponent<CanvasGroup>();
                cg.alpha = 0.4f;
            }
        }

        public void OnMahjongDrag(Vector2 screenPos)
        {
            if (_mahjongDragValue < 0 || _mahjongWallPreview == null) return;
            var cell = ScreenToBoardCell(screenPos);
            _mahjongWallPreviewCell = cell;
            if (cell.x >= 0) _mahjongWallPreview.transform.position = PieceViewFactory.CellToWorld(cell);
        }

        public void OnMahjongDragEnd()
        {
            int value = _mahjongDragValue;
            var card = _mahjongDragCard;
            _mahjongDragValue = -1;
            _mahjongDragCard = null;
            DestroyMahjongWallPreview();
            SetHandLayoutDragging(false);
            if (card != null)
            {
                var cg = card.GetComponent<CanvasGroup>();
                if (cg != null) cg.alpha = 1f;
            }
            if (value < 0 || _flow == null) return;
            if (_mahjongWallPreviewCell.x >= 0 && IsMahjongWallCellValid(_mahjongWallPreviewCell))
            {
                _flow.OnPlayerRequestPlayMahjong(new PlayMahjongRequest(value, _mahjongWallPreviewCell)); // 打墙：1×2 竖两格（1 AP）
                StartCoroutine(RecoverMahjongCardIfFailed(value, card));
            }
            else
            {
                RebuildHand(); // 未落棋盘/非法格：整体重建回手（麻将卡无部署恢复链——重建最稳）
            }
            _mahjongWallPreviewCell = new Vector2Int(-1, -1);
        }

        IEnumerator RecoverMahjongCardIfFailed(int value, GameObject card)
        {
            yield return new WaitForSeconds(0.5f);
            if (HasMahjongInHand(value)) RebuildHand(); // 请求失败（AP 不足/格非法）→ 回手
        }

        bool HasMahjongInHand(int value)
        {
            if (_state == null || _state.Hand == null) return false;
            foreach (var c in _state.Hand) if (c.IsMahjong && c.value == value) return true;
            return false;
        }

        /// <summary>打墙落格校验（与后端一致：1×2 竖＝本格+下格；非敌方部署区/空/无墙）。</summary>
        bool IsMahjongWallCellValid(Vector2Int cell)
        {
            if (_state == null) return false;
            var second = cell + Vector2Int.down;
            if (cell.y >= 6 || second.y >= 6) return false; // 敌方部署区（最上 2 行）拒绝
            return !_state.Pieces.ContainsKey(cell) && !_state.Pieces.ContainsKey(second)
                && !_state.MahjongWalls.ContainsKey(cell) && !_state.MahjongWalls.ContainsKey(second);
        }

        Vector2Int ScreenToBoardCell(Vector2 screenPos)
        {
            var cam = Camera.main;
            if (cam == null) return new Vector2Int(-1, -1);
            var ray = cam.ScreenPointToRay(screenPos);
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (!plane.Raycast(ray, out float enter)) return new Vector2Int(-1, -1);
            return PieceViewFactory.CellFromWorld(ray.GetPoint(enter));
        }

        void CreateMahjongWallPreview()
        {
            if (_mahjongWallPreview != null) return;
            var go = new GameObject("MahjongWallPreview");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = MahjongWallSprite();
            sr.color = new Color(1f, 1f, 1f, 0.5f);
            sr.sortingOrder = 600;
            _mahjongWallPreview = go;
        }

        void DestroyMahjongWallPreview()
        {
            if (_mahjongWallPreview != null) Destroy(_mahjongWallPreview);
            _mahjongWallPreview = null;
        }

        /// <summary>数字选择面板（2026-08-27 能力交互）：出千 1-6（diceRig=true）→ OnDiceNumberSelected；宝牌 1-9 由 Bootstrap 全局弹（RelicObtained）。</summary>
        void ShowSelectNumberPick(bool diceRig)
        {
            if (_uiManager == null) return;
            var panel = _uiManager.GetPanel("SelectNumber") as SelectNumberPanel;
            if (panel == null) return;
            _uiManager.PushOverlay("SelectNumber"); // 先激活面板（协程依赖 active）再构建按钮
            if (diceRig) panel.ShowDiceRigPick();
            else panel.ShowBaopaiPick();
        }

        /// <summary>Grp_Mode 刷新：① 介绍按钮（已加载=玩法名可点 / 未加载=禁用+未加载）② 槽位重建（激活集合变化时）③ 整组显隐（选中棋子隐藏）。</summary>
        void RefreshFloorMode()
        {
            EnsureGrpModeRefs();
            _modeActiveStyles.Clear();
            _modeActiveStyles.AddRange(GetActiveStylesOrdered());
            for (int i = 0; i < 3; i++)
            {
                bool loaded = i < _modeActiveStyles.Count;
                var btn = i < _introButtons.Count ? _introButtons[i] : null;
                if (btn != null) btn.interactable = loaded;
                var txt = i < _introButtonTexts.Count ? _introButtonTexts[i] : null;
                if (txt != null) txt.text = loaded ? DisplayNames.OfStyle(_modeActiveStyles[i]) : "未加载";
            }
            string key = string.Join(",", _modeActiveStyles);
            if (key != _modeKey)
            {
                _modeKey = key;
                StartCoroutine(RebuildModeSlots(new List<string>(_modeActiveStyles), ++_modeBuildGeneration));
            }
            bool show = _selectedPieceId < 0;
            if (_grpModeRoot != null && _grpModeRoot.gameObject.activeSelf != show)
            {
                _grpModeRoot.gameObject.SetActive(show);
            }
        }

        /// <summary>重建 Grp_Play 槽位：按激活玩法顺序 3 槽——玩法面板 / Grp_Play_None 填补空缺（Addressables 加载模板，保持原名供 Find）。</summary>
        System.Collections.IEnumerator RebuildModeSlots(List<string> active, int generation)
        {
            if (_grpPlayRoot == null) _grpPlayRoot = FindSceneTransform("Grp_Play");
            if (_grpPlayRoot == null) yield break;
            // 清空旧槽位（运行时重建——场景摆放实例同此替换，不落盘）
            for (int i = _grpPlayRoot.childCount - 1; i >= 0; i--)
            {
                var child = _grpPlayRoot.GetChild(i);
                if (child != null) Destroy(child.gameObject);
            }
            _modeSlots.Clear();
            for (int i = 0; i < 3; i++)
            {
                if (generation != _modeBuildGeneration) yield break; // 陈旧代际：放弃
                string style = i < active.Count ? active[i] : null;
                GameObject template = null;
                if (style != null)
                {
                    if (!_playPanelTemplates.TryGetValue(style, out template) || template == null)
                    {
                        var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Grp_FloorPlay_" + PlayPanelPrefabSuffix(style));
                        yield return handle;
                        if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && handle.Result != null)
                        {
                            template = handle.Result;
                            _playPanelTemplates[style] = template;
                        }
                    }
                }
                else
                {
                    if (_grpPlayNoneTemplate == null)
                    {
                        var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Grp_Play_None");
                        yield return handle;
                        if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && handle.Result != null)
                        {
                            _grpPlayNoneTemplate = handle.Result;
                        }
                        template = _grpPlayNoneTemplate;
                    }
                    else
                    {
                        template = _grpPlayNoneTemplate;
                    }
                }
                if (template == null) continue;
                var inst = Instantiate(template, _grpPlayRoot);
                inst.name = template.name; // 保持原名（Grp_FloorPlay_* / Grp_Play_None——按名 Find）
                inst.transform.SetAsLastSibling();
                _modeSlots.Add(inst);
                if (style == StyleRegistry.Mahjong) EnsureMahjongRefs(); // 2026-08-27 麻将面板接线
            }
            RefreshFloorMode(); // 槽位就绪后再刷（key 已一致——不会重复重建；主要补显隐）
            // ⚠️ 2026-08-26 修复（a9b8db1 回归）：玩法面板补绑放在**协程内**（槽位实例化完成后）——
            // 不能放 RefreshFloorMode 内：RefreshDicePanel 内部会回调 RefreshGrpPlayVisibility → RefreshFloorMode
            // → 与 RefreshFloorMode→RefreshDicePanel 构成**同步无限递归**（StackOverflow——实测 21:16 爆栈刷屏）
            EnsureMahjongRefs();
            RefreshMahjongPanel();
            EnsureDiceRefs();
            RefreshDicePanel();
        }

        void OnIntroButtonClicked(int index)
        {
            if (index < 0 || index >= _modeActiveStyles.Count) return; // 未加载槽位按钮禁用——防御
            OpenFloorPlayDetail(_modeActiveStyles[index]);
        }

        void OpenFloorPlayDetail(string styleId)
        {
            if (_uiManager == null) return;
            var panel = _uiManager.GetPanel("FloorPlayDetaile");
            if (panel is FloorPlayDetailePanel detail)
            {
                detail.Bind(DisplayNames.OfStyle(styleId), FloorPlayDetailePanel.GetDescription(styleId));
                _uiManager.PushOverlay("FloorPlayDetaile");
            }
            else
            {
                Debug.LogWarning("[Battle] 玩法详情面板未注册——无法打开");
            }
        }

        // ========== 玩法面板·代币（2026-08-24：Grp_FloorPlay_Token——购买弃牌区牌复制入手牌）==========

        void EnsureTokenRefs()
        {
            if (_grpPlayRoot == null) _grpPlayRoot = FindSceneTransform("Grp_Play");
            if (_grpPlayRoot == null || _grpFloorPlayToken != null) return;
            var panel = _grpPlayRoot.Find("Grp_FloorPlay_Token");
            _grpFloorPlayToken = panel != null ? panel.gameObject : null;
            if (_grpFloorPlayToken == null) return;
            _tokenCountText = GetTmp(_grpFloorPlayToken, "Txt_TokenCount_K (1)"); // 数值节点（Txt_TokenCount_K = 标签"拥有代币："）
            var buyBtnTransform = FindDeep(_grpFloorPlayToken.transform, "Btn_BuyToken");
            _buyTokenBtn = buyBtnTransform != null ? buyBtnTransform.GetComponent<Button>() : null;
            if (_buyTokenBtn != null)
            {
                _buyTokenBtn.onClick.RemoveListener(OnBuyTokenClicked); // 每场战斗重接线（防重复监听）
                _buyTokenBtn.onClick.AddListener(OnBuyTokenClicked);
            }
        }

        /// <summary>代币面板刷新：显隐（按玩法激活）+ 数量 + 购买列表（复用围棋刷新时机——激活/状态/阶段变化）。</summary>
        void RefreshTokenPanel()
        {
            EnsureTokenRefs();
            bool active = _state != null && _state.IsStyleActive(StyleRegistry.Token);
            if (_grpFloorPlayToken != null && _grpFloorPlayToken.activeSelf != active) _grpFloorPlayToken.SetActive(active);
            if (!active)
            {
                RefreshGrpPlayVisibility();
                return;
            }
            if (_tokenCountText != null) _tokenCountText.text = _state.TokenCount.ToString();
            RefreshGrpPlayVisibility();
        }

        /// <summary>购买按钮：打开牌库面板购买模式（2026-08-24 复用——弃牌区选牌点击即购买，标题改提示）。</summary>
        void OnBuyTokenClicked()
        {
            if (_uiManager == null) return;
            var panel = _uiManager.GetPanel("DeckLibrary");
            if (panel is DeckLibraryPanel deck)
            {
                deck.EnterBuyMode(OnTokenBuyPicked);
                _uiManager.PushOverlay("DeckLibrary");
            }
            else
            {
                Debug.LogWarning("[Battle] 牌库面板未注册——代币购买无法打开");
            }
            RefreshTokenPanel();
        }

        /// <summary>购买回调（牌库面板购买模式——弃牌区牌点击；discardIndex = DiscardView 顺序）。</summary>
        void OnTokenBuyPicked(int discardIndex)
        {
            if (_flow == null) return;
            _flow.OnPlayerRequestBuyToken(new BuyTokenRequest(discardIndex)); // 后端校验费用/余额；成功复制入手牌 + 发 StateChanged("token")
            RefreshTokenPanel();
        }

        /// <summary>围棋面板刷新：显隐（按玩法激活）+ 剩余次数/下次颜色/提示。</summary>
        void RefreshGoPanel()
        {
            EnsureGoRefs();
            RefreshTokenPanel(); // 代币面板随围棋刷新时机一并刷新（玩法激活/状态/阶段变化）
            bool active = _state != null && _state.IsStyleActive(StyleRegistry.Go);
            if (_grpFloorPlayGo != null && _grpFloorPlayGo.activeSelf != active) _grpFloorPlayGo.SetActive(active);
            if (!active)
            {
                RefreshGrpPlayVisibility();
                return;
            }
            int used = _state.GoDeployCount;
            int cap = _state.GoDeployCapacity();
            int remain = Mathf.Max(0, cap - used);
            if (_goCountText != null) _goCountText.text = remain.ToString();
            string next = !_state.GoEverDeployed ? "蓝" : (_state.GoLastColor == Side.Player ? "红" : "蓝");
            if (_goNextText != null) _goNextText.text = next;
            RefreshGoBuyButton(); // 买子按钮显隐/置灰/文本
            if (_goHintText != null)
            {
                // Txt_Hint = 动态提示（能力提示——速攻/升值/假定/买子，按持有能力；空=无能力）
                _goHintText.text = BuildGoAbilityHints();
            }
            RefreshGrpPlayVisibility();
        }

        /// <summary>买子费用（前端只读镜像——与 Resolver.GoBuyCost 同口径：遗物 effects TokenBuyGo value；未持有 = -1）。</summary>
        int GoBuyCost()
        {
            if (_state == null || _state.Relics == null) return -1;
            foreach (var relic in _state.Relics)
            {
                if (relic == null || relic.effects == null) continue;
                foreach (var e in relic.effects)
                {
                    if (e != null && e.type == RelicEffectType.TokenBuyGo) return Mathf.Max(1, e.value);
                }
            }
            return -1;
        }

        /// <summary>买子按钮：持有「买子」能力 && 围棋+代币玩法激活 → 显示；代币不足置灰（hover 提示原因）。</summary>
        void RefreshGoBuyButton()
        {
            if (_goBuyBtn == null) return;
            int cost = GoBuyCost();
            bool show = cost > 0
                && _state.IsStyleActive(StyleRegistry.Go)
                && _state.IsStyleActive(StyleRegistry.Token);
            if (_goBuyBtn.gameObject.activeSelf != show) _goBuyBtn.gameObject.SetActive(show);
            if (!show) return;
            if (_goBuyBtnText != null) _goBuyBtnText.text = "花 " + cost + " 代币 +1 次";
            _goBuyBtn.interactable = _state.TokenCount >= cost;
        }

        void OnBuyGoClicked()
        {
            if (_flow == null) return;
            _flow.OnPlayerRequestBuyGo(new BuyGoRequest()); // 后端校验费用/余额；成功发 StateChanged("go")
            RefreshGoPanel();
        }

        /// <summary>买子按钮 hover 提示（未激活代币玩法 → 说明原因；代币不足 → 提示差额；正常 → 说明作用）。
        /// public：GoBuyButtonTip 与 GoCardDrag 为命名空间级组件类（同 HandCardDrag——非嵌套），需公开访问。</summary>
        public string BuildGoBuyTip()
        {
            if (_state == null) return null;
            int cost = GoBuyCost();
            if (!_state.IsStyleActive(StyleRegistry.Token))
                return "买子需要激活代币玩法（代币：购买弃牌区牌获得）";
            if (cost > 0 && _state.TokenCount < cost)
                return "代币不足（需 " + cost + "，当前 " + _state.TokenCount + "）";
            if (cost > 0)
                return "花 " + cost + " 代币 +1 次部署（当回合）";
            return "买子：花代币 +1 次部署";
        }

        /// <summary>能力提示行（Txt_Hint——让玩家知道当前围棋被哪些能力强化；空=无能力）。</summary>
        string BuildGoAbilityHints()
        {
            if (_state == null) return "";
            var lines = new List<string>();
            if (_state.HasRelicEffect(RelicEffectType.GoDeployExtra))
                lines.Add("速攻：每回合 " + _state.GoDeployLimit() + " 次");
            if (_state.HasRelicEffect(RelicEffectType.GoValueUp))
                lines.Add("升值：场上围棋价值 +" + _state.GoValueBonus);
            if (_state.HasRelicEffect(RelicEffectType.GoPromote))
                lines.Add("假定：围棋可升变");
            int cost = GoBuyCost();
            if (cost > 0)
                lines.Add("买子：花 " + cost + " 代币 +1 次部署");
            return string.Join("\n", lines);
        }

        /// <summary>围棋卡拖拽门槛：玩法激活 + 玩家回合 + 本回合容量未满 + 非执行/表现中。</summary>
        bool CanDragGoCard()
        {
            return _state != null && _state.IsStyleActive(StyleRegistry.Go)
                && _state.Phase == BattlePhase.PlayerTurn
                && _state.GoDeployCount < _state.GoDeployCapacity()
                && !_executing && !_presentationPlaying;
        }

        /// <summary>围棋拖拽开始：创建棋盘预览（GoPiece 代码内建 def——占位立绘；牌不消耗仅半透明）。</summary>
        public void OnGoDragStart()
        {
            if (!CanDragGoCard()) return;
            if (_previewPiece != null) Destroy(_previewPiece);
            _draggingGo = true;
            PieceViewFactory.EnsureSprites();
            _previewPiece = PieceViewFactory.CreatePieceView(-1, GoPiece.DefId, Side.Player, new Vector2Int(-9, -9), Color.white);
            SetPreviewAlpha(0.6f);
            var shadow = _previewPiece.transform.Find("Shadow");
            if (shadow != null) shadow.gameObject.SetActive(false);
            _previewPiece.transform.position = new Vector3(0f, -50f, 0f); // 隐藏待命
            if (_goCardCg != null) _goCardCg.alpha = 0.5f;
        }

        /// <summary>围棋拖拽跟随：任意空格吸附（非占用/非障碍/界内——后端同口径）。</summary>
        public void OnGoDrag(Vector2 screenPos)
        {
            if (!_draggingGo || _previewPiece == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            var ray = cam.ScreenPointToRay(screenPos);
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (!plane.Raycast(ray, out float enter))
            {
                _goPreviewCell = new Vector2Int(-1, -1);
                return;
            }
            Vector3 boardPoint = ray.GetPoint(enter);
            var cell = PieceViewFactory.CellFromWorld(boardPoint);
            if (IsGoCell(cell))
            {
                _previewPiece.transform.position = PieceViewFactory.CellToWorld(cell);
                _goPreviewCell = cell;
                RefacePreview();
                return;
            }
            boardPoint.y = Mathf.Clamp(boardPoint.y, 0.05f, 5f);
            _previewPiece.transform.position = boardPoint;
            RefacePreview();
            _goPreviewCell = new Vector2Int(-1, -1);
        }

        bool IsGoCell(Vector2Int cell)
        {
            if (cell.x < 0 || cell.x >= 8 || cell.y < 0 || cell.y >= 8) return false; // 8×8 棋盘
            if (_state.Pieces.ContainsKey(cell)) return false; // 任意空格（非占用）
            return !_state.IsBlocked(cell); // 障碍/麻将墙体不可落子（后端同口径）
        }

        /// <summary>围棋拖拽结束：落子（DeployGoRequest——不耗 AP；后端校验容量/围杀）→ 清理预览 → 刷新面板。</summary>
        public void OnGoDragEnd()
        {
            if (!_draggingGo) return;
            _draggingGo = false;
            if (_goPreviewCell.x >= 0)
            {
                _flow.OnPlayerRequestDeployGo(new DeployGoRequest(_goPreviewCell));
            }
            if (_previewPiece != null) Destroy(_previewPiece);
            _previewPiece = null;
            _goPreviewCell = new Vector2Int(-1, -1);
            if (_goCardCg != null) _goCardCg.alpha = 1f;
            RefreshGoPanel();
        }

        // ========== 表现动画（DOTween 优先，测试最小可用）==========
        IEnumerator PlayMove(MoveInfo info)
        {
            var go = _pieceViews.Get(info.PieceId);
            if (go != null)
            {
                AudioManager.Instance.PlaySFX(AudioRefs.SfxMove); // 移动音效（占位——资源就绪后发声）
                var to = PieceViewFactory.CellToWorld(info.To);
                go.transform.DOMove(to, MoveDuration).SetEase(Ease.OutQuad);
                yield return new WaitForSeconds(MoveWait);
            }
            yield return null;
        }

        /// <summary>按后端随伤害事件透传的本次攻击类型选择对应音效；未知类型回退近战音。</summary>
        static string GetAttackSfx(AttackMode attackMode)
        {
            return attackMode switch
            {
                AttackMode.Melee => AudioRefs.SfxAttackMelee,
                AttackMode.MeleeAOE => AudioRefs.SfxAttackMeleeAoe,
                AttackMode.DirectFire => AudioRefs.SfxAttackDirect,
                AttackMode.Arcing => AudioRefs.SfxAttackArcing,
                AttackMode.Spell => AudioRefs.SfxAttackSpell,
                _ => AudioRefs.SfxAttackMelee,
            };
        }

        IEnumerator PlayDamage(DamageInfo info)
        {
            var target = _state.GetPiece(info.TargetId);
            // DamageDealt 在护盾结算后发出；若目标仍有护盾且本次有伤害，则此次伤害包含护盾抵挡。
            bool shieldBlocked = target != null && info.Damage > 0 && target.shieldCount > 0;
            // 攻击者挥动闪白（动作反馈——2026-08-12 恢复：dacb39b 改闪目标时攻击者动作被整体删除；
            // 含空挥 TargetId=-1（AttackerId 所有攻击路径均有效））
            // ⚠️ 组内去重（#6 前端部分）：AOE 多目标同组并行——同一攻击者只闪一次（HashSet.Add 首次 true）
            if (_batchFlashAttackers == null || _batchFlashAttackers.Add(info.AttackerId))
            {
                AudioManager.Instance.PlaySFX(GetAttackSfx(info.AttackMode)); // 攻击（挥击）音效——按本次攻击类型分发，同组只播一次
                var attacker = _pieceViews.Get(info.AttackerId);
                if (attacker != null)
                {
                    var asr = attacker.transform.Find("Portrait")?.GetComponent<SpriteRenderer>();
                    if (asr != null)
                    {
                        var aOrig = asr.color;
                        asr.color = Color.white;
                        yield return new WaitForSeconds(AttackFlash); // 攻击者动作短闪
                        asr.color = aOrig;
                    }
                }
            }
            // 目标受击闪烁（被攻击方；空挥 TargetId=-1 跳过）
            var go = _pieceViews.Get(info.TargetId);
            if (go != null)
            {
                AudioManager.Instance.PlaySFX(shieldBlocked ? AudioRefs.SfxShield : AudioRefs.SfxHit); // 护盾抵挡与受击音区分（逐目标）
                var sr = go.transform.Find("Portrait")?.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    // ⚠️ 恢复原色而非重算 TintFor（创建色/恢复色不一致会颜色漂移）
                    var original = sr.color;
                    sr.color = Color.white;
                    yield return new WaitForSeconds(HitFlash);
                    sr.color = original;
                }
            }
            yield return new WaitForSeconds(DamageWait);
        }

        IEnumerator PlayDeploy(DeployInfo info)
        {
            // 复用场景已有视觉（敌方 BoardBuilder 摆的）或新建
            if (_pieceViews.Get(info.PieceId) == null)
            {
                var existing = FindEnemyVisualAt(info.Cell);
                if (existing != null)
                {
                    existing.name = $"Piece_{info.PieceId}";
                    _pieceViews.Register(info.PieceId, existing);
                }
                else
                {
                    var view = PieceViewFactory.CreatePieceView(info.PieceId, info.DefId, info.Side, info.Cell,
                        info.Side == Side.Player ? PieceViewFactory.TintFor(info.DefId) : PieceViewFactory.TintFor(info.DefId + 1));
                    _pieceViews.Register(info.PieceId, view);
                }
            }
            if (_pendingPromotionWarnings.TryGetValue(info.PieceId, out var pendingWarning))
                CacheOrApplyPromotionWarning(pendingWarning);
            ApplyElementOutline(info.PieceId); // 五行：部署即静态描边（玩家/敌方统一）
            AudioManager.Instance.PlaySFX(AudioRefs.SfxDeploy); // 部署音效（占位）
            yield return new WaitForSeconds(DeployWait);
        }

        IEnumerator PlayDeath(DeathInfo info)
        {
            var go = _pieceViews.Get(info.PieceId);
            if (go != null)
            {
                AudioManager.Instance.PlaySFX(AudioRefs.SfxDeath); // 死亡音效（占位）
                var sr = go.transform.Find("Portrait")?.GetComponent<SpriteRenderer>();
                if (sr != null) sr.material.DOFade(0f, DeathFade); // material 版扩展（DOTween.dll 内）
                go.transform.DOScale(0f, DeathFade);
                yield return new WaitForSeconds(DeathWait);
                // ⚠️ 2026-08-16：销毁前杀该 Transform 上的 tween（PlayMove 等可能仍在跑），防销毁后访问告警
                DOTween.Kill(go.transform);
                // 材质 tween 同杀：sr.material.DOFade 的 target 是 Material 实例，Kill(transform) 覆盖不到
                if (sr != null && sr.material != null) DOTween.Kill(sr.material);
                Destroy(go);
                _pieceViews.Remove(info.PieceId);
            }
            yield return null;
        }

        GameObject FindEnemyVisualAt(Vector2Int cell)
        {
            foreach (var obj in FindObjectsOfType<GameObject>())
            {
                if (obj.name.StartsWith("EnemyPiece_"))
                {
                    var pos = obj.transform.position;
                    if (PieceViewFactory.CellFromWorld(pos) == cell) return obj;
                }
            }
            return null;
        }

        // ========== 行为逻辑浮窗（UI 浮窗，sprite 左上角对齐浮窗右上角）==========
        public void ShowBehaviorTooltip(int slotIndex, Vector3 leftTopWorld)
        {
            if (_infoProgram == null || slotIndex < 0 || slotIndex >= _infoProgram.Count) return;
            // 2026-08-13 重构：通用 TooltipManager（单实例——加载/定位/防出屏收敛；世界坐标 = 行为块左上角）
            TooltipManager.Instance.Show(new TooltipViewData(SlotDetailDesc(_infoProgram[slotIndex])), leftTopWorld);
        }

        public void HideBehaviorTooltip()
        {
            TooltipManager.Instance.Hide();
        }

        // ========== 选格高亮
        // ========== 选格高亮（移动=实心绿块 0.8，攻击=空心红框 边框厚 0.1——可叠加同时显示）==========
        void ShowHighlights(List<Vector2Int> moves, List<Vector2Int> attacks)
        {
            ClearHighlights();
            bool hasMove = moves != null && moves.Count > 0;
            bool hasAttack = attacks != null && attacks.Count > 0;
            if (!hasMove && !hasAttack) return;
            _highlightRoot = new GameObject("RangeHighlights");
            if (hasMove)
            {
                foreach (var cell in moves) CreateHighlightBlock(cell, HighlightMaterial(0.2f, 0.8f, 0.3f));
            }
            if (hasAttack)
            {
                foreach (var cell in attacks) CreateHighlightFrame(cell, HighlightMaterial(0.8f, 0.2f, 0.2f));
            }
        }

        /// <summary>实心块（0.8×0.8，移动格用）。</summary>
        void CreateHighlightBlock(Vector2Int cell, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "Highlight";
            go.transform.SetParent(_highlightRoot.transform, true);
            go.transform.position = new Vector3(cell.x - 3.5f, 0.01f, cell.y - 3.5f);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = new Vector3(0.8f, 0.8f, 1f);
            SetupHighlightMesh(go, mat);
        }

        /// <summary>空心框（边框厚 0.1，攻击格用）。</summary>
        void CreateHighlightFrame(Vector2Int cell, Material mat)
        {
            float cx = cell.x - 3.5f, cz = cell.y - 3.5f;
            const float half = 0.45f;   // 框内边半宽
            const float thick = 0.1f;   // 边框厚度（窄框——与移动实心块可叠加显示）
            AddFrameBar(new Vector3(cx, 0.01f, cz + half), new Vector3(1f, thick, 1f), mat); // 上
            AddFrameBar(new Vector3(cx, 0.01f, cz - half), new Vector3(1f, thick, 1f), mat); // 下
            AddFrameBar(new Vector3(cx - half, 0.01f, cz), new Vector3(thick, 1f, 1f), mat); // 左
            AddFrameBar(new Vector3(cx + half, 0.01f, cz), new Vector3(thick, 1f, 1f), mat); // 右
        }

        void AddFrameBar(Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "FrameBar";
            go.transform.SetParent(_highlightRoot.transform, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = scale;
            SetupHighlightMesh(go, mat);
        }

        void SetupHighlightMesh(GameObject go, Material mat)
        {
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        static Material _highlightMatGreen;
        static Material _highlightMatRed;
        static Material HighlightMaterial(float r, float g, float b)
        {
            if (r > 0.5f)
            {
                if (_highlightMatRed == null)
                {
                    _highlightMatRed = new Material(Shader.Find("Unlit/Color"));
                    _highlightMatRed.color = new Color(r, g, b, 0.4f);
                }
                return _highlightMatRed;
            }
            if (_highlightMatGreen == null)
            {
                _highlightMatGreen = new Material(Shader.Find("Unlit/Color"));
                _highlightMatGreen.color = new Color(r, g, b, 0.4f);
            }
            return _highlightMatGreen;
        }

        void ClearHighlights()
        {
            if (_highlightRoot != null)
            {
                DestroyImmediate(_highlightRoot); // 同帧 Clear+Show 不叠加
                _highlightRoot = null;
            }
        }

        // ========== 事件监听 ==========
        void OnPhaseChanged(object data)
        {
            // 2026-08-12：实际实现为每场战斗重建（Bootstrap.OnPhaseChanged 守卫 + DestroyBattleController 每次销毁）——
            // 此处 ShowPanel 为幂等兜底（面板重建后需确保显示）
            if (_state.Phase == BattlePhase.Placement && _panel != null && _uiManager != null)
            {
                PanelTransition.ShowWithLoading(_uiManager, "Battle");
            }
            RefreshAll();
            ClearSelection();
            ClearHighlights(); // 阶段切换必清高亮
            // 阶段切换重置执行镜像（防执行中结束回合致新回合软锁）
            _executing = false;
            _execPieceId = -1;
            _execProgram = null;
            _execIndex = 0;
            _awaitingCell = false;
            _selectResultDirty = false;
            UpdateHandPositionByPhase();
            CancelDiceMoveSelect(); // 阶段切换：取消骰子方向选择态
            if (_state.Phase == BattlePhase.PlayerTurn) TryStartForcedExec(); // 插入执行：回合切换后（后端同守卫）触发
            if (_uiManager != null)
            {
                var numPanel = _uiManager.GetPanel("SelectNumber");
                if (numPanel != null && numPanel.IsVisible) _uiManager.PopOverlay("SelectNumber"); // 2026-08-27 数字选择未选作废（不跨回合）
            }

            // 阶段展示信号：下一帧通知规则层（动画优先——无动画的阶段切换至少展示一帧）
            if (data is BattlePhase phase)
            {
                StartCoroutine(NotifyPhaseDisplayed(phase));
            }
        }

        System.Collections.IEnumerator NotifyPhaseDisplayed(BattlePhase phase)
        {
            yield return null;
            EventCenter.Instance.EventTrigger(GameEvent.PhaseDisplayed, phase);
        }

        /// <summary>阶段驱动手牌区状态：准备/我方回合展开（拖部署需要），敌方回合收起（无操作空间）。</summary>
        void UpdateHandPositionByPhase()
        {
            if (_panel == null || _panel.HandRoot == null) return;
            var rt = _panel.HandRoot;
            // 我方回合也可部署单位——只有敌方回合收起手牌
            bool expanded = _state.Phase == BattlePhase.Placement || _state.Phase == BattlePhase.PlayerTurn;
            float targetH = expanded ? 210f : 170f;
            float targetY = expanded ? -40f : -90f;
            // 显式通知布局控制器收起/展开状态（上浮修正依赖）
            var layout = rt.GetComponent<HandLayoutController>();
            if (layout != null) layout.SetCollapsed(!expanded);
            if (_handPosTween != null) _handPosTween.Kill();
            if (_handSizeTween != null) _handSizeTween.Kill();
            var sd = rt.sizeDelta;
            _handPosTween = DOTween.To(() => rt.anchoredPosition, v => rt.anchoredPosition = v,
                new Vector2(rt.anchoredPosition.x, targetY), 0.2f);
            _handSizeTween = DOTween.To(() => rt.sizeDelta, v => rt.sizeDelta = v,
                new Vector2(sd.x, targetH), 0.2f); // 独立跟踪（面板销毁/阶段切换时一并 Kill）
        }

        PieceType GetEffectiveType(PieceDef def)
        {
            if (def == null) return PieceType.Deployable;
            return _state != null ? _state.GetEffectiveType(def.Id) : def.pieceType;
        }

        int GetEffectiveValue(PieceDef def)
        {
            if (def == null) return 0;
            return _state != null ? _state.GetEffectiveValue(def.Id) : def.value;
        }

        List<Template> GetDisplayProgram(PieceDef def)
        {
            if (def == null) return null;
            if (_state != null && _state.TryGetCurrentProgram(def.Id, out var edited)) return edited;
            return def.programSet != null && def.programSet.Count > 0 ? def.programSet[0].slots : null;
        }

        /// <summary>手牌是否还有初始棋子（摆放前置判断；2026-08-20 牌结构：仅棋子牌——麻将牌非棋子不计）。</summary>
        bool HasInitialInHand()
        {
            foreach (var card in _state.Hand)
            {
                if (!card.IsPiece) continue;
                var def = ConfigTable.Find<PieceDef>(card.defId);
                if (def != null && GetEffectiveType(def) == PieceType.Initial) return true;
            }
            return false;
        }

        void OnAPChanged(object data)
        {
            RefreshAP();
            RefreshDrawPile();
        }

        /// <summary>通用状态通知（规则层字符串信号——如 placement-incomplete：摆放未完成拒绝结束）。</summary>
        void OnStateChanged(object data)
        {
            if (data is string s)
            {
                if (s == "placement-incomplete")
                {
                    RefreshPhaseButton(); // 刷新按钮状态（提示继续摆放）
                }
                else if (s == "ap-empty")
                {
                    ShowApEmptyTip(); // 行动点耗尽 → "请结束回合"悬浮提示（2026-08-24 可选挂点）
                }
                else if (s == "style" || s == "go" || s == "token" || s == "dice")
                {
                    RefreshGoPanel(); // 玩法激活/围棋状态变化（次数/颜色/容量/买子可买性）
                    RefreshDicePanel(); // 玩法激活/骰子状态（点数/按钮可用性）
                }
                else if (s == "dice-move-select")
                {
                    EnterDiceMoveSelect(); // 点数直线移动：场上高亮可达格 → 点格选方向（后端契约）
                }
                else if (s == "dice-rig-select")
                {
                    ShowSelectNumberPick(true); // 2026-08-27 出千：1-6 自选（后端契约）
                }
                else if (s == "shock-walls")
                {
                    RebuildShockWalls(); // 2026-08-26：能力「震击」墙生成
                }
                else if (s == "request-rejected")
                {
                    // 2026-08-26 后端拒绝信号：执行请求被拒（行动经济已行动等）→ 结束前端执行等待态
                    // （防 _executing 悬挂 → 全场点击被吞/棋子点不了；FinishExec 清选中/高亮/选格态）
                    FinishExec();
                }
                else if (s == "mahjong-score" || s == "mahjong-wall" || s == "mahjong-hu")
                {
                    RefreshMahjongPanel();   // 2026-08-27 麻将：牌山/番数/和牌按钮
                    RebuildMahjongWalls();   // 打出/破坏墙视觉
                    PlayMahjongStateSfx(s);  // 牌山/打出/和牌音
                }
            }
            RefreshDrawPile();
            RefreshScore(); // score / mahjong-hu 等 StateChanged 信号均可安全刷新
        }

        void OnHandChanged(object data)
        {
            if (data == null) return; // AddToEnemyWavePool 也发 HandChanged(null)——敌方侧变化不重建玩家手牌
            int handCount = _state.Hand != null ? _state.Hand.Count : 0;
            if (_lastHandCount >= 0 && handCount > _lastHandCount)
            {
                AudioManager.Instance.PlaySFX(AudioRefs.SfxDraw);
            }
            _lastHandCount = handCount;
            RebuildHand();
            RefreshPhaseButton(); // 手牌变化 → 摆放前置状态可能变化（按钮可用性）
            RefreshDrawPile();
        }

        // ========== UI 刷新 ==========
        void RefreshAll()
        {
            RefreshPhaseButton();
            RefreshAP();
            RebuildHand();
            RefreshDrawPile();
            RefreshScore();
            RefreshEventAbilities();
            RefreshGoPanel();
            RefreshDicePanel(); // 玩法面板：骰子显隐/点数/提示
            RefreshFloorMode(); // Grp_Mode：介绍按钮/槽位填充/整组显隐（2026-08-26）
        }

        /// <summary>
        /// 刷新 Main/UI 的实时计分 3D 文本。
        /// Txt_TurnScore 表示按当前倍率预估、将在本次结算获得的本回合分数。
        /// </summary>
        void RefreshEventAbilities()
        {
            if (_state == null) return;
            EnsureInfoRefs();
            var names = new List<string>();
            if (_state.Relics != null)
            {
                foreach (var relic in _state.Relics)
                {
                    if (relic == null) continue;
                    // 2026-08-23：新能力模型（RelicEffectSpec 组合）无 abilities——回退显示遗物 displayName
                    if (relic.abilities != null && relic.abilities.Count > 0)
                    {
                        foreach (var ability in relic.abilities)
                        {
                            if (ability == null) continue;
                            string name = DisplayNames.OfAbilityType(ability.type);
                            if (!names.Contains(name)) names.Add(name);
                        }
                    }
                    else if (!string.IsNullOrEmpty(relic.displayName))
                    {
                        if (!names.Contains(relic.displayName)) names.Add(relic.displayName);
                    }
                }
            }
            Set(_infoAbilities, names.Count > 0 ? string.Join("、", names) : "无");
        }

        // ========== 通用变亮通道（HintRequested——2026-08-23：E5 资格等提示）==========

        /// <summary>通用变亮/提示通道：CardQualify（E5 抽牌即战资格）→ 手牌变亮 + 左面能力面板变亮；0=取消。</summary>
        void OnHintRequested(object data)
        {
            if (!(data is HintPayload hp)) return;
            if (hp.kind == HintKind.CardQualify)
            {
                ApplyCardQualifyHighlight(hp.targetId); // 真值以 GameState.EditedCardQualifyId 为准（payload 是即时刷新信号）
            }
        }

        /// <summary>按 GameState 真值回填资格高亮（开局/读档/手牌重建后调用）。</summary>
        void RefreshQualifyHighlight()
        {
            ApplyCardQualifyHighlight(_state != null ? _state.EditedCardQualifyId : 0);
        }

        /// <summary>E5 资格：手牌卡变亮（scale 放大 + 金色 tint）+ 左面能力面板金色脉冲；targetId=牌 instanceId，0=取消。</summary>
        void ApplyCardQualifyHighlight(int cardInstanceId)
        {
            ClearCardQualifyHighlight();
            bool on = cardInstanceId != 0;
            if (on && _panel != null && _panel.HandRoot != null)
            {
                foreach (var drag in _panel.HandRoot.GetComponentsInChildren<HandCardDrag>(true))
                {
                    if (drag.CardInstanceId == cardInstanceId)
                    {
                        var go = drag.gameObject;
                        _qualifyCardHighlight = go;
                        _qualifyCardBaseScale = go.transform.localScale;
                        go.transform.localScale = _qualifyCardBaseScale * 1.15f;
                        var img = go.GetComponent<UnityEngine.UI.Image>();
                        if (img != null)
                        {
                            _qualifyCardHasColor = true;
                            _qualifyCardBaseColor = img.color;
                            img.color = new Color(1f, 0.9f, 0.55f, img.color.a); // 金色高亮（视觉自决——与升变预告红框独立）
                        }
                        break;
                    }
                }
            }
            ApplyAbilityPanelHighlight(on);
        }

        void ClearCardQualifyHighlight()
        {
            if (_qualifyCardHighlight != null)
            {
                _qualifyCardHighlight.transform.localScale = _qualifyCardBaseScale;
                var img = _qualifyCardHighlight.GetComponent<UnityEngine.UI.Image>();
                if (img != null && _qualifyCardHasColor) img.color = _qualifyCardBaseColor;
                _qualifyCardHighlight = null;
            }
        }

        /// <summary>左面能力面板（Txt_Abilities——道中能力栏）金色脉冲；与手牌高亮独立开关。</summary>
        void ApplyAbilityPanelHighlight(bool on)
        {
            EnsureInfoRefs();
            if (_infoAbilities == null) return;
            if (on)
            {
                if (_abilityFlashTween != null) return; // 已在闪
                var baseColor = _infoAbilities.color;
                var flashColor = new Color(1f, 0.85f, 0.25f);
                _abilityFlashTween = DOTween.To(() => baseColor, c => _infoAbilities.color = c, flashColor, 0.45f)
                    .SetLoops(-1, LoopType.Yoyo).SetEase(Ease.InOutSine);
            }
            else
            {
                if (_abilityFlashTween == null) return;
                _abilityFlashTween.Kill();
                _abilityFlashTween = null;
                _infoAbilities.color = Color.white; // 恢复
            }
        }

        void RefreshScore()
        {
            if (_state == null) return;
            if (_lastPlayerScore >= 0 && _state.PlayerScore > _lastPlayerScore
                && (_state.CurrentFloorConfig?.scoreDeductEnabled ?? false))
            {
                AudioManager.Instance.PlaySFX(AudioRefs.SfxScore);
            }
            _lastPlayerScore = _state.PlayerScore;
            EnsureScoreRefs();
            int waveScore = _state.WaveScores != null && _state.WaveScores.Count > 0
                ? _state.WaveScores[_state.WaveScores.Count - 1]
                : 0;
            int turnScore = _state.BaseScore * _state.ScoreMultiplier;
            if (_totalScoreText != null) _totalScoreText.text = _state.PlayerScore.ToString();
            if (_waveScoreText != null) _waveScoreText.text = waveScore.ToString();
            if (_baseScoreText != null) _baseScoreText.text = _state.BaseScore.ToString();
            if (_multiplierText != null) _multiplierText.text = _state.ScoreMultiplier.ToString();
            if (_turnScoreText != null) _turnScoreText.text = turnScore.ToString();
        }

        void RefreshDrawPile()
        {
            if (_panel == null || _state == null) return;
            int remaining = _state.DrawPile != null ? _state.DrawPile.Count : 0;
            bool canDraw = _state.Phase == BattlePhase.PlayerTurn
                && _state.PlayerAP >= 1
                && remaining > 0
                && !_executing
                && !_presentationPlaying;
            _panel.SetDrawPile(remaining, canDraw);
        }

        void OnDrawButtonClicked()
        {
            if (_flow == null || _state == null) return;
            RefreshDrawPile();
            int remaining = _state.DrawPile != null ? _state.DrawPile.Count : 0;
            if (_state.Phase != BattlePhase.PlayerTurn || _state.PlayerAP < 1
                || remaining <= 0 || _executing || _presentationPlaying) return;
            _flow.OnPlayerRequestDraw(new DrawCardRequest());
            RefreshDrawPile();
        }

        void RefreshPhaseButton()
        {
            if (_panel == null || _panel.PhaseButton == null) return;
            Debug.Log($"[Battle] RefreshPhaseButton phase={_state.Phase} eventNameSet=true");
            var btn = _panel.PhaseButton;
            var txt = _panel.PhaseButtonText;
            bool placementReady = _state.Phase == BattlePhase.Placement && !HasInitialInHand();
            switch (_state.Phase)
            {
                case BattlePhase.Placement:
                    // 摆放前置（规则层）：手牌还有初始棋子时禁用（文字恒为"结束准备"）
                    btn.interactable = placementReady;
                    if (txt != null) txt.text = "结束准备";
                    if (_panel != null) _panel.SetEventName("我方准备");
                    break;
                case BattlePhase.PlayerTurn:
                    btn.interactable = true;
                    if (txt != null) txt.text = "结束回合";
                    if (_panel != null) _panel.SetEventName("我方回合");
                    break;
                case BattlePhase.EnemyTurn:
                    btn.interactable = false;
                    if (txt != null) txt.text = "等待中"; // 按钮文字限 4 字
                    if (_panel != null) _panel.SetEventName("敌方回合");
                    break;
                case BattlePhase.GameOver:
                    // 2026-08-13 需求：按钮全程可见（不隐藏）——战斗结束等结算确认：置灰"等待中"
                    // （防"中途胜利复用控制器"时按钮被隐藏 → 新摆放阶段无按钮卡死）
                    btn.gameObject.SetActive(true);
                    btn.interactable = false;
                    if (txt != null) txt.text = "等待中";
                    break;
                default:
                    btn.gameObject.SetActive(false);
                    break;
            }
            // 2026-08-23：准备完成 → 按钮文字闪动提醒玩家激活；未完成/非准备 → 停止闪动 + 关闭残留提示
            SetPhaseButtonFlash(placementReady);
            if (!placementReady && _phaseTipShowing)
            {
                _phaseTipShowing = false;
                TooltipManager.Instance.Hide();
            }
        }

        /// <summary>阶段按钮 hover 进入/离开（2026-08-23：准备阶段未部署完初始棋子时显示引导 tip）。</summary>
        void OnPhaseButtonHover(bool enter)
        {
            if (!enter)
            {
                if (_phaseTipShowing)
                {
                    _phaseTipShowing = false;
                    TooltipManager.Instance.Hide();
                }
                return;
            }
            if (_panel == null || _panel.PhaseButton == null
                || _state.Phase != BattlePhase.Placement || !HasInitialInHand())
            {
                return; // 仅"准备阶段且还有初始棋子未部署"时提示
            }
            var canvas = _panel.PhaseButton.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, _panel.PhaseButton.transform.position);
            _phaseTipShowing = true;
            TooltipManager.Instance.ShowAtScreen("部署完所有初始棋子后激活此按钮", screen);
        }

        /// <summary>行动点耗尽提示（2026-08-24 可选挂点——StateChanged("ap-empty")：后端 AP≤0 时每次行动尝试都会发，需防连发）。</summary>
        void ShowApEmptyTip()
        {
            if (_panel == null || _panel.PhaseButton == null
                || _state == null || _state.Phase != BattlePhase.PlayerTurn)
            {
                return; // 仅玩家回合提示有实际意义
            }
            if (_apEmptyTipShowing)
            {
                return; // 防连发刷屏（AP=0 时每次请求都会触发）
            }
            var canvas = _panel.PhaseButton.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, _panel.PhaseButton.transform.position);
            if (_phaseTipShowing)
            {
                _phaseTipShowing = false; // 同通道互斥：先收起准备引导再显示 AP 提示
                TooltipManager.Instance.Hide();
            }
            TooltipManager.Instance.ShowAtScreen("行动点耗尽，请结束回合", screen);
            _apEmptyTipShowing = true;
            StartCoroutine(HideApEmptyTipLater());
        }

        IEnumerator HideApEmptyTipLater()
        {
            yield return new WaitForSeconds(ApEmptyTipDuration);
            _apEmptyTipShowing = false;
            TooltipManager.Instance.Hide();
        }

        /// <summary>准备完成 → 阶段按钮文字闪动提醒（2026-08-23；显式管理 tween 防销毁后访问）。</summary>
        void SetPhaseButtonFlash(bool on)
        {
            var txt = _panel != null ? _panel.PhaseButtonText : null;
            if (txt == null) return;
            if (on)
            {
                if (_phaseFlashTween != null) return; // 已在闪动
                // TMP 无 DOColor 扩展（DOTween TMP 模块未启用）——用 DOTween.To 颜色补间（基色→亮金往复）
                var baseColor = txt.color;
                var flashColor = new Color(1f, 0.85f, 0.25f);
                _phaseFlashTween = DOTween.To(() => baseColor, c => txt.color = c, flashColor, 0.45f)
                    .SetLoops(-1, LoopType.Yoyo).SetEase(Ease.InOutSine);
            }
            else
            {
                if (_phaseFlashTween == null) return;
                _phaseFlashTween.Kill();
                _phaseFlashTween = null;
                txt.color = Color.white; // 恢复原色
            }
        }

        /// <summary>阶段按钮 hover 检测组件（2026-08-23：准备阶段引导 tip——纯转发输入）。</summary>
        public class PhaseButtonHoverTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            private BattleController _owner;

            public void Init(BattleController owner)
            {
                _owner = owner;
            }

            public void OnPointerEnter(PointerEventData eventData)
            {
                _owner?.OnPhaseButtonHover(true);
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                _owner?.OnPhaseButtonHover(false);
            }
        }

        void RefreshAP()
        {
            if (_panel != null) _panel.SetAP(_state.PlayerAP, _state.PlayerAPMax);
        }

        void OnPhaseButtonClicked()
        {
            if (_presentationPlaying) return; // 表现播放中禁点阶段按钮
            switch (_state.Phase)
            {
                case BattlePhase.Placement:
                    EventCenter.Instance.EventTrigger(GameEvent.PlacementFinished);
                    break;
                case BattlePhase.PlayerTurn:
                    _flow.OnPlayerEndTurn();
                    break;
            }
        }

        void ClearSelection()
        {
            _selectedPieceId = -1;
            ClearPieceInfo();
            ClearHighlights(); // 取消选中必清棋格提示（防残留误导）
            RefreshFloorMode(); // 取消选中 → 恢复 Grp_Mode（玩法区/介绍按钮；2026-08-26）
        }

        // ========== 场上信息面板（Main 1 场景 UI 根下的 3D 文本）==========
        GameObject _infoRoot;

        void EnsureScoreRefs()
        {
            if (_totalScoreText != null && _waveScoreText != null && _baseScoreText != null
                && _multiplierText != null && _turnScoreText != null) return;

            var ui = _infoRoot != null ? _infoRoot : GameObject.Find("UI");
            if (ui == null)
            {
                foreach (var go in FindObjectsOfType<GameObject>(true))
                {
                    if (go.name == "UI" && go.transform.parent == null) { ui = go; break; }
                }
            }
            if (ui == null) return;

            _infoRoot = ui;
            _totalScoreText = GetTmp(ui, "Txt_TotalScore");
            _waveScoreText = GetTmp(ui, "Txt_WaveScore");
            _baseScoreText = GetTmp(ui, "Txt_BaseScore");
            _multiplierText = GetTmp(ui, "Txt_Multiplier");
            _turnScoreText = GetTmp(ui, "Txt_TurnScore");
            if (!_scoreRefsWarningLogged && (_totalScoreText == null || _waveScoreText == null
                || _baseScoreText == null || _multiplierText == null || _turnScoreText == null))
            {
                _scoreRefsWarningLogged = true;
                Debug.LogWarning("[Battle] 实时计分节点缺失：需要 Main/UI 下的 Txt_TotalScore、Txt_WaveScore、Txt_BaseScore、Txt_Multiplier、Txt_TurnScore");
            }
        }

        void EnsureInfoRefs()
        {
            if (_infoName != null) return;
            var ui = GameObject.Find("UI");
            if (ui == null)
            {
                // ⚠️ GameObject.Find 只找 active 对象——UI 根 inactive 时兜底遍历（含 inactive）
                // （场景资产 UI 默认 active；编辑器内可能被误勾掉。ShowPieceInfo 时会 SetActive(true) 强制显示）
                foreach (var go in FindObjectsOfType<GameObject>(true))
                {
                    if (go.name == "UI" && go.transform.parent == null) { ui = go; break; }
                }
            }
            if (ui == null) return;
            _infoRoot = ui;
            _pieceInfoRoot = ui.transform.Find("Grp_Piece") ?? ui.transform.Find("Piece");
            _infoName = GetTmp(ui, "Txt_Name");
            _infoType = GetTmp(ui, "Txt_Type");
            _infoValue = GetTmp(ui, "Txt_Value");
            _infoDurability = GetTmp(ui, "Txt_Durability");
            _infoAbilities = GetTmp(ui, "Txt_Abilities");
            _infoOther = GetTmp(ui, "Txt_Other"); // 单节点多行 buff 区（2026-08-11：场景无 Txt_Other1~3，改为单 Txt_Other）
            EnsureScoreRefs();
            for (int i = 0; i < 4; i++)
            {
                var t = FindDeep(ui.transform, $"Txt_BehaviorLogic{i + 1}");
                _infoProgramBlocks[i] = t != null ? t.GetComponent<SpriteRenderer>() : null;
                if (_infoProgramBlocks[i] != null && t.GetComponent<Collider>() == null)
                {
                    // collider 尺寸对齐 sprite 世界尺寸（除以 localScale 得局部尺寸）——默认 (1,1,1) 在 0.16 缩放下只有 0.16 世界尺寸，hover 命中率极低
                    var sr = _infoProgramBlocks[i];
                    var bc = t.gameObject.AddComponent<BoxCollider>();
                    float sx = t.localScale.x != 0 ? t.localScale.x : 1f;
                    float sy = t.localScale.y != 0 ? t.localScale.y : 1f;
                    bc.size = new Vector3(sr.bounds.size.x / sx, sr.bounds.size.y / sy, 0.2f);
                }
            }
            // 注：Txt_Other_K 是标题（“其他”），Txt_Other 是多行 buff 区——由 FillInfo 填充（2026-08-11）
        }

        static TMP_Text GetTmp(GameObject root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

        public void ShowPieceInfo(int pieceId)
        {
            EnsureInfoRefs();
            var piece = _state.GetPiece(pieceId);
            if (piece == null) return;
            var def = piece.def;
            if (def == null) return;

            FillInfo(def, piece);
            if (_pieceInfoRoot != null) _pieceInfoRoot.gameObject.SetActive(true);
            RefreshGrpPlayVisibility(); // 选中棋子 → 隐藏关卡玩法面板（同位置切换显隐）
        }

        void FillInfo(PieceDef def, PieceInstance piece)
        {
            // 名称带阵营：士兵(友) / 士兵(敌)（2026-08-11：阵营并入名称）
            string sideTag = piece != null ? (piece.side == Side.Player ? "(友)" : "(敌)") : "";
            Set(_infoName, $"{def.displayName}{sideTag}");
            var effectiveType = GetEffectiveType(def);
            Set(_infoType, effectiveType == PieceType.Initial ? "初始" : effectiveType == PieceType.Deployable ? "部署" : "升变");
            Set(_infoValue, GetEffectiveValue(def).ToString());
            Set(_infoDurability, piece != null ? $"{piece.durability}/{def.durability}" : def.durability.ToString());
            // Txt_Abilities 是整局道中能力栏，不随当前选中棋子变化。
            RefreshEventAbilities();
            // buff 区（Txt_Other 多行）：升变 → 护盾 → 免费行动 → 临时能力（BuffDisplay 聚合）
            Set(_infoOther, BuildBuffLines(def, piece));

            var program = piece != null ? piece.GetProgram(_state) : (def.programSet != null && def.programSet.Count > 0 ? def.programSet[0].slots : null);
            _infoProgram = program;
            int slotCount = program != null ? Mathf.Min(program.Count, 4) : 0;
            for (int i = 0; i < 4; i++)
            {
                // 行为逻辑块：只显示已配置的槽（未配置隐藏）；挂 hover 检测
                if (_infoProgramBlocks[i] != null)
                {
                    _infoProgramBlocks[i].gameObject.SetActive(i < slotCount);
                    if (i < slotCount)
                    {
                        var hover = _infoProgramBlocks[i].GetComponent<BehaviorSlotHover>();
                        if (hover == null) hover = _infoProgramBlocks[i].gameObject.AddComponent<BehaviorSlotHover>();
                        hover.Init(this, i);
                    }
                }
            }
        }

        /// <summary>buff key 内置中文兜底（配置表未命中时——防机器码泄漏）：shield/free_execute/ability_* → 中文。</summary>
        static string BuffFallback(string key)
        {
            if (key == "shield") return "护盾";
            if (key == "free_execute") return "免费行动";
            if (key != null && key.StartsWith("ability_")) return "临时能力";
            Debug.LogWarning($"[Battle] buff key 无中文兜底：{key}");
            return "未知";
        }

        /// <summary>
        /// buff 区文本（Txt_Other 多行拼接）：升变 → 护盾 → 免费行动 → 临时能力（BuffDisplay 聚合 + BuffDescTable 名称）。
        /// 无 buff → “无”；最多 6 行；升变：PromotionConfig.toDefId → 棋子名。
        /// </summary>
        string BuildBuffLines(PieceDef def, PieceInstance piece)
        {
            var lines = new List<string>();
            // 升变（buff 行：可升变为 xx）
            if (def.promotionConfigId != 0 && ConfigTable.Find<PromotionConfig>(def.promotionConfigId) is PromotionConfig promo)
            {
                var toDef = ConfigTable.Find<PieceDef>(promo.toDefId);
                lines.Add(toDef != null ? $"升变：{toDef.displayName}" : "升变：未知目标"); // 配置缺失时中文兜底（防数字泄漏）
            }
            // BuffDisplay 聚合（护盾/免费行动/临时能力——后端顺序）
            if (piece != null)
            {
                foreach (var buff in BuffDisplay.GetBuffs(piece, _state))
                {
                    string name = BuffDescTable.GetName(buff.key) ?? BuffFallback(buff.key); // 配置表优先，内置中文兜底（防机器码泄漏）
                    // count 格式：剩余≥2 → ×N；=1 → 只名称；plain → 只名称
                    string line = name;
                    if (BuffDescTable.IsCountFormat(buff.key) && buff.remaining >= 2)
                    {
                        line = $"{name}×{buff.remaining}";
                    }
                    lines.Add(line);
                }
            }
            // 无 buff → “无”（标题 Txt_Other_K 保持“其他”）
            if (lines.Count == 0) return "无";
            // 最多 6 行（区域支持——超出截断）
            if (lines.Count > 6) lines = lines.GetRange(0, 6);
            return string.Join("\n", lines);
        }

        public void ClearPieceInfo()
        {
            EnsureInfoRefs();
            if (_pieceInfoRoot != null) _pieceInfoRoot.gameObject.SetActive(false);
            RefreshGrpPlayVisibility(); // 取消选中 → 恢复关卡玩法面板
            Set(_infoName, "");
            Set(_infoType, "");
            Set(_infoValue, "");
            Set(_infoDurability, "");
            RefreshEventAbilities();
            Set(_infoOther, "");
            for (int i = 0; i < 4; i++)
            {
                if (_infoProgramBlocks[i] != null) _infoProgramBlocks[i].gameObject.SetActive(false);
            }
        }

        static void Set(TMP_Text tmp, string text)
        {
            if (tmp != null) tmp.text = text;
        }

        /// <summary>程序槽详细描述（信息面板/浮窗用，自然语言）——描述表优先，未命中回退代码生成。</summary>
        static string SlotDetailDesc(Template slot)
        {
            var mapped = SlotDescTable.Get(slot);
            if (mapped != null) return mapped;
            switch (slot)
            {
                case MoveTemplate m:
                    return MoveDescNatural(m);
                case AttackTemplate a:
                    return AttackDescNatural(a);
                case EffectTemplate e:
                    return EffectDescNatural(e);
                case SkipTemplate:
                    return "跳：跳过本回合行动";
                default:
                    return "跳：跳过本回合行动";
            }
        }

        /// <summary>效果描述：效果模块装配即被动生效；描述表未命中时按能力配置生成。</summary>
        static string EffectDescNatural(EffectTemplate effect)
        {
            if (effect == null || string.IsNullOrEmpty(effect.abilityKey)) return "效：装配后被动生效";
            var ability = ConfigTable.FindByName<SpecialAbilityDef>(effect.abilityKey);
            if (ability == null) return "效：装配后被动生效";
            switch (ability.type)
            {
                case SpecialAbilityType.Passive:
                    string sign = ability.passiveValue >= 0 ? "+" : "";
                    return $"效：被动，{DisplayNames.OfPassiveTarget(ability.passiveTarget)}{sign}{ability.passiveValue}";
                case SpecialAbilityType.Trigger:
                    return $"效：被动，{DisplayNames.OfTriggerPoint(ability.triggerPoint)}时触发{DisplayNames.OfTriggerEffect(ability.triggerEffect)}";
                case SpecialAbilityType.Attach:
                    return $"效：被动，{DisplayNames.OfAttachPoint(ability.attachPoint)}附加效果";
                default:
                    return "效：装配后被动生效";
            }
        }

        /// <summary>移动描述：移动 = 在可达地块选一个前往（无"再移"概念）——按可达范围分级描述（描述表未收录结构的回退）。</summary>
        static string MoveDescNatural(MoveTemplate m)
        {
            Direction dirs = 0;
            int maxStep = 0;
            if (m.paths != null)
            {
                foreach (var path in m.paths)
                {
                    foreach (var seg in path.segments)
                    {
                        foreach (var step in seg.moves)
                        {
                            dirs |= step.direction;
                            if (step.steps != null)
                            {
                                foreach (var s in step.steps) maxStep = Mathf.Max(maxStep, s);
                            }
                        }
                    }
                }
            }
            if (dirs == 0) return "移：原地不动";

            int count = 0;
            for (int d = 1; d <= (int)Direction.DownRight; d <<= 1)
            {
                if ((dirs & (Direction)d) != 0) count++;
            }
            bool hasDiag = (dirs & (Direction.UpLeft | Direction.UpRight | Direction.DownLeft | Direction.DownRight)) != 0;

            if (count >= 8) return maxStep >= 2 ? "移：大范围内选定一格进行移动" : "移：周围范围内选定一格进行移动";
            if (maxStep >= 2) return "移：较大范围内选定一格进行移动";
            if (count == 4 && !hasDiag) return "移：上下左右范围内选定一格进行移动";
            if (count >= 3) return "移：前方范围内选定一格进行移动";
            if ((dirs & Direction.Left) != 0 && (dirs & Direction.Right) != 0) return "移：左右范围内选定一格进行移动";
            if (hasDiag) return "移：斜向范围内选定一格进行移动";
            return "移：前方一格范围内选定进行移动";
        }

        static string AttackDescNatural(AttackTemplate a)
        {
            string prefix = a.mode switch
            {
                AttackMode.Melee => "近战",
                AttackMode.MeleeAOE => "近战群攻",
                AttackMode.DirectFire => "直射",
                AttackMode.Arcing => "抛射",
                AttackMode.Spell => "法术",
                _ => "未知", // 中文兜底（防新增枚举泄漏）
            };
            string dirs = DirsNatural(a.directions);
            string target = a.mode == AttackMode.Melee || a.mode == AttackMode.MeleeAOE
                ? $"{dirs}相邻"
                : a.mode == AttackMode.DirectFire
                    ? $"{dirs}直线{GetRange(a)}格"
                    : "目标点";
            return $"攻：{prefix}，对{target}造成{a.damage}伤害";
        }

        static int GetRange(AttackTemplate a)
        {
            return a.range;
        }

        /// <summary>方向组合（位标志 → 中文，如 Up|Left → "左上"）。</summary>
        static string DirsNatural(Direction dirs)
        {
            var parts = new List<string>();
            foreach (Direction d in System.Enum.GetValues(typeof(Direction)))
            {
                if (d != 0 && (dirs & d) != 0) parts.Add(DirName(d));
            }
            if (parts.Count == 0) return "前方";
            // 上下左右 → 四方向合并描述
            string joined = string.Join("", parts);
            if (joined == "上下左右") return "上下左右";
            if (joined == "左上右上") return "左前右前";
            return joined;
        }

        /// <summary>步数描述：[1]=“1”，[1,2]=“1或2”。</summary>
        static string StepsNatural(List<int> steps)
        {
            if (steps == null || steps.Count == 0) return "";
            if (steps.Count == 1) return steps[0].ToString();
            return string.Join("或", steps);
        }

        static string DirName(Direction d)
        {
            switch (d)
            {
                case Direction.Up: return "上";
                case Direction.Down: return "下";
                case Direction.Left: return "左";
                case Direction.Right: return "右";
                case Direction.UpLeft: return "左上";
                case Direction.UpRight: return "右上";
                case Direction.DownLeft: return "左下";
                case Direction.DownRight: return "右下";
                default: return "未知"; // 中文兜底（防新增枚举泄漏）
            }
        }

        // ========== 手牌 ==========
        // 手牌区下移 tween（防面板销毁后访问失效 target）
        DG.Tweening.Tween _handPosTween;
        DG.Tweening.Tween _handSizeTween;
        int _handBuildSeq; // 手牌重建版本号（防异步协程竞态）
        string _lastHandKey = ""; // 上次重建的手牌指纹（无变化跳过重建——防闪烁）

        bool _handRebuildPending; // 2026-08-26：手牌节点未就绪时的重建等待标记（防重复排队）

        void RebuildHand()
        {
            if (_panel == null) return;
            if (_panel.HandRoot == null)
            {
                // 2026-08-26 防御：面板节点未解析（ResolveNodes 在 OnShow——Init 先于解析的时序窗口）
                // → 等待节点就绪后重建，防"手牌有数据但 UI 空"的静默丢失（按钮接线同理）。
                if (!_handRebuildPending)
                {
                    _handRebuildPending = true;
                    StartCoroutine(WaitForHandRootThenRebuild());
                }
                return;
            }

            // 无变化保护：手牌内容没变就不重建（消除外部 HandChanged/阶段切换的无意义闪烁）
            // 牌实例 id 是手牌 UI 身份；同 defId / 属性的重复牌也必须分别刷新与复用。
            string key = string.Join("|", _state.Hand.ConvertAll(c => $"{c.instanceId}-{c.defId}-{c.value}-{c.element}"));
            int expectedPieceCards = 0;
            foreach (var card in _state.Hand)
            {
                if (card.IsPiece && ConfigTable.Find<PieceDef>(card.defId) != null) expectedPieceCards++;
            }
            if (key == _lastHandKey && _panel.HandRoot.childCount == expectedPieceCards)
            {
                return;
            }
            _lastHandKey = key;

            // 拖拽中重建：先清理拖拽状态（防 _draggingCard 卡死/预览泄漏）
            if (_draggingCard)
            {
                _draggingCard = false;
                _draggingPromotionCard = false;
                SetHandLayoutDragging(false); // 重建即结束拖拽，恢复 hover
                _dragCardInstanceId = -1;
                _dragCard = null;
                if (_previewPiece != null) Destroy(_previewPiece);
                _previewPiece = null;
                _previewCell = new Vector2Int(-1, -1);
            }

            // 手牌卡为独立 prefab（Piece_Handcard）——Addressables 按需加载
            // 注意：清空旧卡放在协程内（加载完成后同帧清+建）——避免重建中间空白帧（闪一下）
            _handBuildSeq++;
            var snapshot = new List<Card>(_state.Hand);
            StartCoroutine(LoadAndBuildHand(_handBuildSeq, snapshot));
        }

        /// <summary>等待战斗面板节点解析（HandRoot 就绪）后重建手牌；超时放弃（面板无该节点时防死等）。</summary>
        System.Collections.IEnumerator WaitForHandRootThenRebuild()
        {
            float t = 0f;
            while (_panel != null && _panel.HandRoot == null && t < 3f)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            _handRebuildPending = false;
            if (_panel != null && _panel.HandRoot != null)
            {
                RebuildHand();
            }
        }

        IEnumerator LoadAndBuildHand(int seq, List<Card> snapshot)
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Piece_Handcard");
            yield return handle;
            if (seq != _handBuildSeq)
            {
                UnityEngine.AddressableAssets.Addressables.Release(handle);
                yield break; // 过期重建请求：放弃（防双份卡片）
            }
            if (handle.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                Debug.LogWarning("[Battle] 手牌卡 prefab 加载失败（address=Piece_Handcard）");
                UnityEngine.AddressableAssets.Addressables.Release(handle);
                yield break;
            }
            var template = handle.Result;
            // 先收集旧卡，再按当前 Hand 快照复用/增删；不改变 HandLayoutController 的动画。
            var oldCards = new List<(GameObject go, int instanceId)>();
            foreach (Transform child in _panel.HandRoot)
            {
                var drag = child.GetComponent<HandCardDrag>();
                if (drag != null) oldCards.Add((child.gameObject, drag.CardInstanceId));
                else
                {
                    child.gameObject.SetActive(false);
                    Destroy(child.gameObject); // 非手牌卡对象不应留在 HandRoot
                }
            }
            bool fromEmpty = oldCards.Count == 0;
            var reused = new bool[oldCards.Count];
            var layout = _panel.HandRoot.GetComponent<HandLayoutController>();
            if (layout == null) layout = _panel.HandRoot.gameObject.AddComponent<HandLayoutController>();
            // 手牌显示排序：类型优先（初始→部署→升变）+ 同类型价值升序；排序只影响视觉，Card 实例身份必须保留。
            // 2026-08-27 麻将玩法：手牌含麻将牌（棋子牌排序后追加，按点数升序）
            var hand = new List<(Card card, PieceDef def)>();
            var mahjongHand = new List<Card>();
            foreach (var handCard in snapshot)
            {
                if (handCard.IsMahjong) { mahjongHand.Add(handCard); continue; }
                if (!handCard.IsPiece) continue;
                var def = ConfigTable.Find<PieceDef>(handCard.defId);
                if (def != null) hand.Add((handCard, def));
            }
            hand.Sort((a, b) =>
            {
                int type = CardTypeColors.TypeOrder(GetEffectiveType(a.def)).CompareTo(CardTypeColors.TypeOrder(GetEffectiveType(b.def)));
                if (type != 0) return type;
                int value = GetEffectiveValue(a.def).CompareTo(GetEffectiveValue(b.def));
                return value != 0 ? value : a.card.instanceId.CompareTo(b.card.instanceId);
            });
            mahjongHand.Sort((a, b) => a.value != b.value ? a.value.CompareTo(b.value) : a.instanceId.CompareTo(b.instanceId));
            foreach (var mc in mahjongHand) hand.Add((mc, null));
            for (int i = 0; i < hand.Count; i++)
            {
                var handCard = hand[i].card;
                var def = hand[i].def;
                var data = def != null
                    ? PiecePresentationMapper.ToHandCard(
                        def,
                        GetEffectiveType(def),
                        GetEffectiveValue(def),
                        GetDisplayProgram(def),
                        handCard.element) // 2026-08-27：五行 → 类型位背景+字
                    : MahjongCardData(handCard.value); // 2026-08-27 麻将卡：复用卡模板（名字=麻将/价值=点数）
                GameObject card = null;
                // 复用必须按实例 id 匹配：同 defId、同属性的重复牌也不能互换身份。
                for (int j = 0; j < oldCards.Count; j++)
                {
                    if (!reused[j] && oldCards[j].instanceId == handCard.instanceId)
                    {
                        card = oldCards[j].go;
                        reused[j] = true;
                        break;
                    }
                }
                if (card == null)
                {
                    var view = UIComponentFactory.CreateHandCard(template, _panel.HandRoot, data);
                    card = view.gameObject;
                    card.name = $"Card_{i}_{(def != null ? def.displayName : "麻将")}";
                    card.SetActive(true);
                    if (handCard.IsMahjong) AddMahjongCardDrag(card, handCard.value, handCard.instanceId);
                    else AddCardDrag(card, handCard, i);
                    var newCanvasGroup = card.GetComponent<CanvasGroup>();
                    if (newCanvasGroup != null) newCanvasGroup.alpha = 1f;
                    // 全量重建后直接可见；不使用 alpha=0 的异步淡入，避免 tween 被打断后层级对象存在但不可见。
                }
                else
                {
                    card.name = $"Card_{i}_{(def != null ? def.displayName : "麻将")}";
                    card.SetActive(true);
                    var drag = card.GetComponent<HandCardDrag>();
                    if (drag != null) drag.CancelVisualTween();
                    // 复用卡视觉重置（防拖出动画残留：alpha=0/scale=0.3 时隐形）
                    var cg = card.GetComponent<CanvasGroup>();
                    if (cg != null) cg.alpha = 1f;
                    card.transform.localScale = Vector3.one * 0.35f;
                    var view = card.GetComponent<HandCardView>();
                    if (view == null) view = card.AddComponent<HandCardView>();
                    view.Bind(data);
                }
                // 五行（2026-08-25）：手牌外描边——element != None → 对应颜色（复用/新建统一处理）
                var cardOutline = card.GetComponent<UnityEngine.UI.Outline>();
                if (handCard.element != Element.None)
                {
                    if (cardOutline == null) cardOutline = card.AddComponent<UnityEngine.UI.Outline>();
                    cardOutline.effectColor = ElementColors.ColorOf(handCard.element);
                    cardOutline.effectDistance = new Vector2(2.5f, -2.5f);
                }
                else if (cardOutline != null)
                {
                    Destroy(cardOutline);
                }
            }
            // 销毁未复用的旧卡（已移除的）
            for (int j = 0; j < oldCards.Count; j++)
            {
                if (!reused[j] && oldCards[j].go != null)
                {
                    DestroyImmediate(oldCards[j].go);
                }
            }
            // 有复用卡 → 不 instant（布局插值产生滑动过渡）；从无到有 → instant 落位
            layout.RefreshCards(fromEmpty);
            // 已实例化卡片不依赖 prefab handle；每次重建释放本次加载引用，避免 refcount 累积。
            UnityEngine.AddressableAssets.Addressables.Release(handle);
            RefreshQualifyHighlight(); // 2026-08-23：手牌重建后回填 E5 资格高亮（真值在 GameState.EditedCardQualifyId）
        }

        /// <summary>新卡淡入（alpha 0→1，按索引错峰）——重建后重排有过渡而非瞬间出现。</summary>
        void FadeInCard(GameObject card, int index)
        {
            var cg = card.GetComponent<CanvasGroup>();
            if (cg == null) cg = card.AddComponent<CanvasGroup>();
            cg.alpha = 0f;
            DG.Tweening.DOTween.To(() => cg.alpha, a => cg.alpha = a, 1f, 0.2f)
                .SetDelay(index * 0.04f)
                .SetTarget(cg);
        }

        void AddCardDrag(GameObject card, Card handCard, int index)
        {
            var drag = card.AddComponent<HandCardDrag>();
            drag.Init(this, handCard.defId, handCard.instanceId, index);
        }

        public bool CanDragCard(int defId)
        {
            var def = ConfigTable.Find<PieceDef>(defId);
            if (def == null) return false;
            var effectiveType = GetEffectiveType(def);
            // 升变牌只可在玩家回合、至少有 1 AP 时拖到己方未升变棋子；不进入部署流程。
            if (effectiveType == PieceType.Promoted)
            {
                return _state.Phase == BattlePhase.PlayerTurn && _state.PlayerAP >= 1
                    && !_executing && !_presentationPlaying;
            }
            // 普通牌仍沿用原部署规则：Placement=初始 / PlayerTurn=部署。
            bool typeOk = _state.Phase == BattlePhase.Placement
                ? effectiveType == PieceType.Initial
                : effectiveType == PieceType.Deployable;
            return typeOk && !_executing && !_presentationPlaying
                && (_state.Phase == BattlePhase.Placement || _state.Phase == BattlePhase.PlayerTurn);
        }

        // ========== 拖拽部署 ==========
        void SetHandLayoutDragging(bool dragging)
        {
            if (_panel != null && _panel.HandRoot != null)
            {
                var layout = _panel.HandRoot.GetComponent<HandLayoutController>();
                if (layout != null) layout.SetDragging(dragging);
            }
        }

        public void OnCardDragStart(int defId, int cardInstanceId, GameObject card)
        {
            if (!CanDragCard(defId)) return;
            if (_previewPiece != null) Destroy(_previewPiece); // 防旧预览泄漏
            _draggingCard = true;
            var def = ConfigTable.Find<PieceDef>(defId);
            _draggingPromotionCard = def != null && GetEffectiveType(def) == PieceType.Promoted;
            SetHandLayoutDragging(true); // 拖拽期间冻结手牌 hover/让位（后端排查记录）
            _dragDefId = defId;
            _dragCardInstanceId = cardInstanceId;
            _dragCard = card;
            // 升变牌也创建跟随鼠标的立绘预览；释放时仍走场上目标检测，不进入空格部署流程。
            PieceViewFactory.EnsureSprites();
            _previewPiece = PieceViewFactory.CreatePieceView(-1, defId, Side.Player, new Vector2Int(-9, -9),
                PieceViewFactory.TintFor(defId));
            SetPreviewAlpha(0.6f);
            // 预览无阴影：单位尚未真正在场
            var shadow = _previewPiece.transform.Find("Shadow");
            if (shadow != null) shadow.gameObject.SetActive(false);
            if (_previewPiece != null) _previewPiece.transform.position = new Vector3(0f, -50f, 0f); // 隐藏待命
        }
        public void OnCardDrag(Vector2 screenPos)
        {
            if (!_draggingCard || _previewPiece == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            var ray = cam.ScreenPointToRay(screenPos);
            // 部署格与自由跟随统一以 y=0 棋盘平面换算：不依赖隐形 Tile Collider，
            // 避免棋子、预览物或其他 Collider 抢先命中后得到错误格子。
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (!plane.Raycast(ray, out float enter))
            {
                _previewCell = new Vector2Int(-1, -1);
                return;
            }
            Vector3 boardPoint = ray.GetPoint(enter);

            var cell = PieceViewFactory.CellFromWorld(boardPoint);
            bool canSnap = _draggingPromotionCard
                ? IsPromotionTargetCell(cell)
                : IsDeployableCell(cell);
            if (canSnap)
            {
                _previewPiece.transform.position = PieceViewFactory.CellToWorld(cell);
                _previewCell = cell;
                RefacePreview();
                return;
            }

            // 未吸附时仍按棋盘平面跟随光标，保持原有预览表现。
            boardPoint.y = Mathf.Clamp(boardPoint.y, 0.05f, 5f);
            _previewPiece.transform.position = boardPoint;
            RefacePreview();
            _previewCell = new Vector2Int(-1, -1);
        }

        /// <summary>预览朝向跟随相机（位置变化后重算）。</summary>
        void RefacePreview()
        {
            var portraitT = _previewPiece.transform.Find("Portrait");
            if (portraitT == null || Camera.main == null) return;
            Vector3 toCam = Camera.main.transform.position - portraitT.position;
            float horiz = new Vector2(toCam.x, toCam.z).magnitude;
            float angle = Mathf.Atan2(toCam.y, horiz) * Mathf.Rad2Deg;
            portraitT.rotation = Quaternion.Euler(angle, 0f, 0f);
        }

        public void OnCardDragEnd(PointerEventData eventData)
        {
            if (!_draggingCard) return;
            _draggingCard = false;
            var defId = _dragDefId;
            var cardInstanceId = _dragCardInstanceId;
            var card = _dragCard;
            if (_draggingPromotionCard)
            {
                int pieceId = FindPromotionTarget(eventData);
                if (pieceId >= 0)
                {
                    _flow.OnPlayerRequestPromote(new PromoteRequest(pieceId, defId)
                    {
                        cardInstanceId = cardInstanceId
                    });
                    StartCoroutine(RecoverCardIfFailed(cardInstanceId, card));
                }
                else
                {
                    RestoreDragCard(card); // 非法目标、AP 不足或非玩家回合：原卡回手。
                }
            }
            else if (_previewCell.x >= 0)
            {
                bool free = _state.Phase == BattlePhase.Placement;
                _flow.OnPlayerRequestDeploy(new DeployRequest(defId, _previewCell)
                {
                    free = free,
                    cardInstanceId = cardInstanceId
                });
                // 成功：PieceDeployed → 规则层精确移除该实例 → OnPieceDeployed 重建手牌。
                // 失败兜底：0.5s 后 Hand 仍含该实例才恢复卡片（引用提前捕获——_dragCard 本方法末尾置空）。
                StartCoroutine(RecoverCardIfFailed(cardInstanceId, card));
            }
            else
            {
                RestoreDragCard(card); // 非法格：只恢复拖出的卡片（不整体重建）
            }
            if (_previewPiece != null) Destroy(_previewPiece);
            _previewPiece = null;
            _previewCell = new Vector2Int(-1, -1);
            _draggingPromotionCard = false;
            _dragDefId = -1;
            _dragCardInstanceId = -1;
            _dragCard = null; // 统一清理（防野引用）
            SetHandLayoutDragging(false); // 拖拽结束恢复 hover（后端排查记录）
        }

        bool IsPromotionTargetCell(Vector2Int cell)
        {
            if (_state == null || _state.Phase != BattlePhase.PlayerTurn || _state.PlayerAP < 1) return false;
            var piece = _state.Pieces.TryGetValue(cell, out var target) ? target : null;
            return piece != null && piece.side == Side.Player
                && _state.GetEffectiveType(piece.DefId) != PieceType.Promoted;
        }

        int FindPromotionTarget(PointerEventData eventData)
        {
            if (_state.Phase != BattlePhase.PlayerTurn || _state.PlayerAP < 1 || Camera.main == null) return -1;
            var ray = Camera.main.ScreenPointToRay(eventData.position);
            if (!Physics.Raycast(ray, out var hit, 200f)) return -1;
            var cell = PieceViewFactory.CellFromWorld(hit.point);
            if (!IsPromotionTargetCell(cell)) return -1;
            return _state.Pieces[cell].Id;
        }

        IEnumerator RecoverCardIfFailed(int cardInstanceId, GameObject card)
        {
            yield return new WaitForSeconds(0.5f);
            // 只检查拖出的那张实例：同 defId 的另一张牌仍在手牌时不能误判恢复。
            bool held = false;
            foreach (var c in _state.Hand)
            {
                if (c.IsPiece && c.instanceId == cardInstanceId) { held = true; break; }
            }
            if (held)
            {
                RestoreDragCard(card); // 规则层未移除 → 部署失败 → 恢复卡片（幂等）
            }
        }

        /// <summary>恢复拖出的卡片（淡出动画倒放），不重建手牌——避免无变化闪烁。</summary>
        void RestoreDragCard(GameObject card = null)
        {
            if (card == null) card = _dragCard;
            if (card == null) return; // 卡片已销毁（Unity 伪 null）或拖拽清理后无引用
            var drag = card.GetComponent<HandCardDrag>();
            if (drag != null) drag.CancelVisualTween();
            var cg = card.GetComponent<CanvasGroup>();
            if (cg != null)
            {
                cg.alpha = 1f;
                DG.Tweening.DOTween.To(() => cg.alpha, a => cg.alpha = a, 1f, 0.15f)
                    .SetTarget(cg); // 绑定 target：卡片销毁时可被 Kill
            }
            card.transform.DOScale(0.35f, 0.15f);
            if (card == _dragCard) _dragCard = null;
        }

        bool IsDeployableCell(Vector2Int cell)
        {
            // 玩家部署区：y=0~1 + 空格（与规则层 IsValidDeployCell 一致）
            if (cell.x < 0 || cell.x >= 8 || cell.y < 0 || cell.y > 1) return false;
            if (_state.Pieces.ContainsKey(cell)) return false;
            return !_state.IsBlocked(cell); // 普通障碍与麻将墙体均不可部署
        }

        void SetPreviewAlpha(float a)
        {
            if (_previewPiece == null) return;
            var sr = _previewPiece.transform.Find("Portrait")?.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                var c = sr.color;
                c.a = a;
                sr.color = c;
            }
        }
    }

    /// <summary>
    /// 手牌卡拖拽（hover 表现已由 HandLayoutController 槽位判定统一管理）。
    /// 拖拽部署仅准备阶段；拖出时淡出，部署失败由 RebuildHand 恢复。
    /// </summary>
    public class HandCardDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        BattleController _controller;
        int _defId;
        int _cardInstanceId;
        CanvasGroup _cg;
        DG.Tweening.Tween _fadeTween; // 拖出淡出 tween（显式管理，防销毁后访问）

        public int DefId => _defId;
        public int CardInstanceId => _cardInstanceId; // 差异重建与请求按 Card 实例精确识别

        public void CancelVisualTween()
        {
            if (_fadeTween != null)
            {
                _fadeTween.Kill();
                _fadeTween = null;
            }
            if (_cg != null)
            {
                DG.Tweening.DOTween.Kill(_cg);
                _cg.alpha = 1f;
            }
            DG.Tweening.DOTween.Kill(transform);
        }

        public void Init(BattleController controller, int defId, int cardInstanceId, int cardIndex)
        {
            _controller = controller;
            _defId = defId;
            _cardInstanceId = cardInstanceId;
            _cg = GetComponent<CanvasGroup>();
            if (_cg == null) _cg = gameObject.AddComponent<CanvasGroup>();
        }

        void OnDestroy()
        {
            if (_fadeTween != null)
            {
                _fadeTween.Kill();
                _fadeTween = null;
            }
            // ⚠️ 2026-08-16：CanvasGroup 上的 tween（FadeInCard/RestoreDragCard 等 SetTarget(cg)）
            // 也要杀——只 Kill(transform) 杀不到组件 target，卡销毁后 DOTween 会报 missing target/field
            if (_cg != null) DG.Tweening.DOTween.Kill(_cg);
            DG.Tweening.DOTween.Kill(transform); // 拖出缩小 tween（有 target，也要杀）
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_controller.CanDragCard(_defId)) return;
            _controller.OnCardDragStart(_defId, _cardInstanceId, gameObject);
            // 拖出动画：淡出 + 缩小（失败时 RestoreDragCard 恢复）
            _fadeTween = DOTween.To(() => _cg.alpha, a => _cg.alpha = a, 0f, 0.15f);
            DOTween.Kill(transform);
            transform.DOScale(0.3f, 0.15f);
        }

        public void OnDrag(PointerEventData eventData)
        {
            _controller.OnCardDrag(eventData.position);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _controller.OnCardDragEnd(eventData);
        }
    }

    /// <summary>卡 hover 放大（仅缩放不上浮——2026-08-27 麻将牌山卡；参考手牌区 2 倍比例）。</summary>
    public class CardHoverScale : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        const float HoverFactor = 2f;
        Vector3 _baseScale = Vector3.zero;
        DG.Tweening.Tween _tween;

        void EnsureBase()
        {
            if (_baseScale == Vector3.zero) _baseScale = transform.localScale;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            EnsureBase();
            if (_tween != null) _tween.Kill();
            _tween = transform.DOScale(_baseScale * HoverFactor, 0.15f);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            EnsureBase();
            if (_tween != null) _tween.Kill();
            _tween = transform.DOScale(_baseScale, 0.15f);
        }

        void OnDestroy()
        {
            if (_tween != null) { _tween.Kill(); _tween = null; }
        }
    }

    /// <summary>麻将手牌卡拖拽（2026-08-27：拖到棋盘=打墙（1×2 竖两格）；点击=摸切（填牌山+抽牌））。</summary>
    public class MahjongCardDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        BattleController _controller;
        int _value;

        public void Init(BattleController controller, int value)
        {
            _controller = controller;
            _value = value;
        }

        public void OnBeginDrag(PointerEventData eventData) => _controller?.OnMahjongDragStart(_value, gameObject);
        public void OnDrag(PointerEventData eventData) => _controller?.OnMahjongDrag(eventData.position);
        public void OnEndDrag(PointerEventData eventData) => _controller?.OnMahjongDragEnd();
        public void OnPointerClick(PointerEventData eventData) => _controller?.OnMahjongCardClicked(_value);
    }

    /// <summary>围棋棋子牌拖拽（2026-08-24：手牌式拖到任意空格部署——牌不消耗；整套流程同手牌部署）。</summary>
    public class GoCardDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        BattleController _controller;

        public void Init(BattleController controller)
        {
            _controller = controller;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _controller.OnGoDragStart();
        }

        public void OnDrag(PointerEventData eventData)
        {
            _controller.OnGoDrag(eventData.position);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _controller.OnGoDragEnd();
        }
    }

    /// <summary>买子按钮 hover 提示（2026-08-26：未激活代币玩法/代币不足时说明原因——防玩家困惑）。</summary>
    public class GoBuyButtonTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        BattleController _controller;
        Button _button;

        public void Init(BattleController controller, Button button)
        {
            _controller = controller;
            _button = button;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_controller == null || _button == null) return;
            var canvas = _button.GetComponentInParent<Canvas>();
            Vector2 screen = canvas != null && canvas.worldCamera != null
                ? RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, _button.transform.position)
                : eventData.position;
            string msg = _controller.BuildGoBuyTip();
            if (msg != null) TooltipManager.Instance.ShowAtScreen(msg, screen);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            TooltipManager.Instance.Hide();
        }
    }
}
