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
        int _selectedPieceId = -1;
        // 敌方升变预告可能早于 Piece View 创建：按 pieceId 缓存，视觉出现后补应用。
        readonly Dictionary<int, PromoteAnnouncement> _pendingPromotionWarnings = new Dictionary<int, PromoteAnnouncement>();
        readonly BattleViewRegistry _pieceViews = new BattleViewRegistry();

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
            _pendingForcedExecs.Clear();
            _pendingPromotionWarnings.Clear();
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
            }
            if (_state.PromoteAnnouncements != null)
            {
                foreach (var announcement in _state.PromoteAnnouncements)
                {
                    if (announcement != null) CacheOrApplyPromotionWarning(announcement);
                }
            }
            ApplyPendingPromotionWarnings();
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

        /// <summary>buff 变化（护盾/免费行动/临时能力）：目标是当前选中棋子 → 刷新信息面板（Txt_Other buff 区实时更新）。</summary>
        void OnBuffsChanged(object data)
        {
            if (data is int pieceId && pieceId == _selectedPieceId && _selectedPieceId >= 0)
            {
                var piece = _state.GetPiece(_selectedPieceId);
                if (piece != null && _infoName != null)
                {
                    FillInfo(piece.def, piece);
                }
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
            if (_state.Phase == BattlePhase.PlayerTurn) TryStartForcedExec(); // 插入执行：回合切换后（后端同守卫）触发

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

        void RebuildHand()
        {
            if (_panel == null || _panel.HandRoot == null) return;

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
            // ⚠️ 2026-08-20 牌结构：仅棋子牌显示（麻将牌表现留待玩法实现/前端后续）
            var hand = new List<(Card card, PieceDef def)>();
            foreach (var handCard in snapshot)
            {
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
            for (int i = 0; i < hand.Count; i++)
            {
                var handCard = hand[i].card;
                var def = hand[i].def;
                var data = PiecePresentationMapper.ToHandCard(
                    def,
                    GetEffectiveType(def),
                    GetEffectiveValue(def),
                    GetDisplayProgram(def));
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
                    card.name = $"Card_{i}_{def.displayName}";
                    card.SetActive(true);
                    AddCardDrag(card, handCard, i);
                    var newCanvasGroup = card.GetComponent<CanvasGroup>();
                    if (newCanvasGroup != null) newCanvasGroup.alpha = 1f;
                    // 全量重建后直接可见；不使用 alpha=0 的异步淡入，避免 tween 被打断后层级对象存在但不可见。
                }
                else
                {
                    card.name = $"Card_{i}_{def.displayName}";
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
}
