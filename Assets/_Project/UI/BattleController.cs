using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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

        // ========== 回合进度条（2026-08-12：进度条 + 波次节点）==========
        private FloorConfig _floor; // 当前层配置（波次数据源）
        private List<WaveDef> _waveDefs = new List<WaveDef>();
        private GameObject _waveNodeTemplate; // Tag_WaveNode prefab（Addressables）
        private readonly List<GameObject> _waveNodes = new List<GameObject>(); // 已创建的节点实例
        private int _lastWaveCount = -1; // Update 轮询：WaveScores 变化（波次部署完成）→ 刷新节点亮黄

        /// <summary>回合进度：总回合 = 末波 startTurn-1 + endCountdown；进度 = (TurnCount+1)/总回合（归零回合满条，末段等距体现倒计时）。</summary>
        void RefreshTurnProgress()
        {
            if (_panel == null) return;
            if (_floor == null)
            {
                // 从 GameState.CurrentFloor 拿层配置（无需 Init 注入）
                foreach (var map in ConfigTable.All<MapConfig>())
                {
                    if (map.floors != null && _state.CurrentFloor >= 0 && _state.CurrentFloor < map.floors.Count)
                    {
                        _floor = map.floors[_state.CurrentFloor];
                        break;
                    }
                }
                if (_floor != null)
                {
                    _waveDefs = _floor.waveDefs ?? new List<WaveDef>();
                    BuildWaveNodes();
                }
            }
            if (_floor == null) return;
            // 总回合 = 末波 startTurn - 1 + endCountdown（TurnCount 从 0 开始；倒计时设值当回合即减 1）
            //   → 归零回合 TurnCount = startTurn+endCountdown-2，(TurnCount+1)/total 恰为 1.0 满条
            int totalTurns = 0;
            if (_waveDefs.Count > 0)
            {
                var last = _waveDefs[_waveDefs.Count - 1];
                totalTurns = last.startTurn - 1 + Mathf.Max(0, last.endCountdown);
            }
            if (totalTurns > 0)
            {
                // 进度 = 已完成回合数 / 总回合数（末波倒计时自然体现在末段等距推进——无需额外文本）
                _panel.SetTurnProgress(Mathf.Clamp01((float)(_state.TurnCount + 1) / totalTurns));
            }
            RefreshWaveNodeStates();
        }

        /// <summary>波次节点：按 startTurn/总回合 比例定位（0~1 → Slider 范围）。</summary>
        void BuildWaveNodes()
        {
            if (_panel == null || _panel.WaveNodesRoot == null) return;
            foreach (var n in _waveNodes) if (n != null) Destroy(n);
            _waveNodes.Clear();
            if (_waveDefs.Count == 0 || _waveNodeTemplate == null) return;
            var last = _waveDefs[_waveDefs.Count - 1];
            int totalTurns = last.startTurn - 1 + Mathf.Max(0, last.endCountdown);
            foreach (var wave in _waveDefs)
            {
                var node = Instantiate(_waveNodeTemplate, _panel.WaveNodesRoot);
                node.name = $"WaveNode_{wave.startTurn}";
                // 初始波（startTurn=1，第一回合默认生成初始敌人）不显示节点
                if (wave.startTurn <= 1) node.SetActive(false);
                _waveNodes.Add(node);
                // 定位：startTurn/总回合（与进度公式 (TurnCount+1)/total 对齐——节点落在该波部署瞬间的进度位置）
                var rt = node.GetComponent<RectTransform>();
                if (rt != null && totalTurns > 0)
                {
                    float ratio = Mathf.Clamp01((float)wave.startTurn / totalTurns);
                    rt.anchorMin = new Vector2(ratio, 0.5f);
                    rt.anchorMax = new Vector2(ratio, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                }
                // 波次号文本（可选）
                var txt = node.GetComponentInChildren<TMP_Text>();
                if (txt != null) txt.text = wave.startTurn.ToString();
            }
        }

        /// <summary>节点状态：用 WaveScores.Count 精确绑定部署完成时刻（BattleFlow 部署完 Add(0)）——亮黄与敌方生成同刻。</summary>
        void RefreshWaveNodeStates()
        {
            int deployed = _state.WaveScores != null ? _state.WaveScores.Count : 0;
            for (int i = 0; i < _waveNodes.Count && i < _waveDefs.Count; i++)
            {
                var node = _waveNodes[i];
                if (node == null || !node.activeSelf) continue;
                var img = node.GetComponent<Image>();
                if (img == null) continue;
                if (i + 1 < deployed) img.color = new Color(1f, 1f, 1f, 1f);            // 已过（该波后又有新波部署）：亮白
                else if (i + 1 == deployed) img.color = new Color(1f, 0.84f, 0.2f, 1f); // 当前波（刚部署）：金
                else img.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);                     // 未来（未部署）：暗
            }
        }

        /// <summary>加载波次节点模板（Addressables——Tag_WaveNode；失败则跳过节点只显示进度条）。</summary>
        System.Collections.IEnumerator LoadWaveNodeTemplate()
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Tag_WaveNode");
            yield return handle;
            if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && handle.Result != null)
            {
                _waveNodeTemplate = handle.Result;
                BuildWaveNodes();
            }
        }

        // ========== 状态 ==========
        bool _executing;             // 执行镜像进行中
        int _execPieceId = -1;
        List<Template> _execProgram;
        int _execIndex;
        bool _awaitingCell;          // 等玩家选格
        bool _isMoveSelect;          // true=移动选格 false=攻击选格
        List<Vector2Int> _cellOptions = new List<Vector2Int>();
        GameObject _highlightRoot;   // 移动可选格高亮容器
        int _selectedPieceId = -1;

        // 表现队列（帧缓冲合并同槽事件）
        readonly List<System.Func<IEnumerator>> _presentations = new List<System.Func<IEnumerator>>();
        bool _presentationPlaying;
        bool _selectResultDirty;     // 选格后帧内是否有表现事件（判落账成败）

        // 部署预览
        GameObject _previewPiece;
        Vector2Int _previewCell = new Vector2Int(-1, -1);
        bool _draggingCard;
        int _dragDefId = -1;
        GameObject _dragCard; // 拖拽中的卡片（失败时恢复，避免整体重建闪烁）

        // 信息面板（Main 1 场景 UI 根下的 3D TMP 文本，用户已拼）
        TMPro.TextMeshPro _infoName, _infoType, _infoValue, _infoDurability, _infoAbilities;
        TMPro.TextMeshPro _infoOther; // 单节点多行 buff 区（Txt_Other——护盾/免费行动/临时能力/升变，\n 分隔）
        SpriteRenderer[] _infoProgramBlocks = new SpriteRenderer[4]; // 行为逻辑块（SpriteRenderer）
        List<Template> _infoProgram; // 当前信息面板显示的程序（浮窗内容源）
        RectTransform _tooltip; // 行为逻辑浮窗（UGUI）
        TMPro.TextMeshProUGUI _tooltipText;

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
            EventCenter.Instance.RemoveEventListener(GameEvent.StateChanged, OnStateChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.BuffsChanged, OnBuffsChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.ExtraActionGranted, OnBuffsChanged);
            if (_tooltip != null) _tooltip.gameObject.SetActive(false);
            if (_handPosTween != null) _handPosTween.Kill();
            if (_handSizeTween != null) _handSizeTween.Kill();
            // 随会话销毁战斗面板（重开/回主菜单时清理——防多局累积实例）
            // ⚠️ DestroyImmediate：Destroy 延迟会让旧面板在重开新局时短暂并存（滚动条/状态沿用旧实例）——同步销毁
            if (_panel != null) DestroyImmediate(_panel.gameObject);
            // 清理盘面视觉：高亮根 + 全部棋子视觉（重开会话时盘面必须清空）
            ClearHighlights();
            foreach (var go in FindObjectsOfType<GameObject>())
            {
                if (go.name.StartsWith("Piece_"))
                {
                    DestroyImmediate(go);
                }
            }
        }

        /// <summary>退出战斗请求（Bootstrap 订阅——回主菜单）。</summary>
        public event System.Action OnExitRequested;

        public void Init(BattleFlow flow, GameState state, UIManager uiManager)
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
            EventCenter.Instance.AddEventListener(GameEvent.StateChanged, OnStateChanged);
            EventCenter.Instance.AddEventListener(GameEvent.BuffsChanged, OnBuffsChanged); // buff 变化 → 刷新选中棋子信息面板
            EventCenter.Instance.AddEventListener(GameEvent.ExtraActionGranted, OnBuffsChanged); // 免费行动授予同刷新

            PanelBase.CreateAsync<BattlePanel>(p =>
            {
                _panel = p;
                // 2026-08-12 架构重构：BattlePanel 注册进 UIManager（切换型）——替代直接 _panel.Show()
                _uiManager.RegisterPanel(p);
                _uiManager.ShowPanel("Battle");
                if (_panel.PhaseButton != null)
                {
                    _panel.PhaseButton.onClick.AddListener(OnPhaseButtonClicked);
                }
                if (_panel.ExitButton != null)
                {
                    _panel.ExitButton.onClick.AddListener(() =>
                    {
                        _panel.ExitButton.interactable = false; // 立即反馈——收尾延后 1 帧期间按钮置灰（防"点了没反应"）
                        OnExitRequested?.Invoke();
                    });
                }
                RefreshAll();
                UpdateHandPositionByPhase(); // 初始阶段即应用手牌区状态（准备阶段高度 250）
                ClearPieceInfo(); // 初始：信息面板隐藏（无选中/无临时状态）
                // 补齐开局已有棋子视觉（首波部署早于控制器创建——PieceDeployed 事件已丢）
                SyncExistingPieces();
                // 回合进度条：加载波次节点模板 + 首次刷新（2026-08-12）
                StartCoroutine(LoadWaveNodeTemplate());
                RefreshTurnProgress();
                _lastWaveCount = _state.WaveScores != null ? _state.WaveScores.Count : -1; // 初始同步（开局首波已部署）
            });
        }

        /// <summary>同步棋盘已有棋子视觉（开局首波/后续会话补齐——事件早于监听时视觉不创建）。</summary>
        void SyncExistingPieces()
        {
            foreach (var kv in _state.Pieces)
            {
                var piece = kv.Value;
                if (GameObject.Find($"Piece_{piece.Id}") != null) continue;
                PieceViewFactory.CreatePieceView(piece.Id, piece.side, piece.position,
                    piece.side == Side.Player ? PieceViewFactory.TintFor(piece.DefId) : PieceViewFactory.TintFor(piece.DefId + 1));
            }
        }

        void Update()
        {
            // 波次部署完成轮询：WaveScores.Count 变化 → 节点亮黄与敌方生成同刻（部署后无事件通知，轮询最可靠）
            if (_state != null && _state.WaveScores != null && _state.WaveScores.Count != _lastWaveCount)
            {
                _lastWaveCount = _state.WaveScores.Count;
                RefreshWaveNodeStates();
            }

            if (!Input.GetMouseButtonDown(0)) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

            // 射线打 Tile（棋子无碰撞，Tile 有 BoxCollider）
            var ray = Camera.main != null ? Camera.main.ScreenPointToRay(Input.mousePosition) : default;
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

        /// <summary>选中后只显示首个逻辑块的范围（移动=绿块；攻击=红框）——多个范围杂糅不易读。</summary>
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
            switch (program[0])
            {
                case MoveTemplate move:
                    ShowHighlights(_intentResolver.GetMoveOptions(_state, piece, move), null);
                    break;
                case AttackTemplate atk:
                    ShowHighlights(null, _boardRules.GetAttackableCells(_state, piece, atk));
                    break;
                default:
                    ClearHighlights(); // Skip 等：无范围
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
                if (s is SkipTemplate) { execIndex++; continue; }
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
        }

        /// <summary>表现组播完后：镜像推进下一槽（规则层已 AdvanceSlot 到等待/结束）。</summary>
        void AdvanceAfterPresentation()
        {
            if (!_executing) return;
            _execIndex++;
            AdvanceExec();
        }

        // ========== 表现协议 ==========
        void EnqueuePresentation(System.Func<IEnumerator> play)
        {
            _presentations.Add(play);
            if (!_presentationPlaying) StartCoroutine(PresentationLoop());
        }

        IEnumerator PresentationLoop()
        {
            _presentationPlaying = true;
            yield return null; // 帧缓冲：合并同槽伴随事件（DamageDealt+PieceDied）
            while (_presentations.Count > 0)
            {
                var play = _presentations[0];
                _presentations.RemoveAt(0);
                yield return play();
            }
            _presentationPlaying = false;
            EventCenter.Instance.EventTrigger(GameEvent.PresentationFinished);
            if (_executing) AdvanceAfterPresentation();
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
        }

        void OnPieceDeployed(object data)
        {
            var info = (DeployInfo)data;
            EnqueuePresentation(() => PlayDeploy(info));
            if (info.Side == Side.Player)
            {
                RebuildHand(); // 规则层已 Hand.Remove——本地重建移除该卡
                RefreshPhaseButton(); // ResolveDeploy 不发 HandChanged——按钮摆放前置状态需手动刷新
            }
        }

        void OnPieceDied(object data)
        {
            var info = (DeathInfo)data;
            // 选中单位死亡：清选中 + 清高亮（防残留高亮指向死棋子）
            if (info.PieceId == _selectedPieceId)
            {
                ClearSelection();
                ClearHighlights();
            }
            EnqueuePresentation(() => PlayDeath(info));
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

        // ========== 表现动画（DOTween 优先，测试最小可用）==========
        IEnumerator PlayMove(MoveInfo info)
        {
            var go = GameObject.Find($"Piece_{info.PieceId}");
            if (go != null)
            {
                var to = PieceViewFactory.CellToWorld(info.To);
                go.transform.DOMove(to, 0.25f).SetEase(Ease.OutQuad);
                yield return new WaitForSeconds(0.3f);
            }
            yield return null;
        }

        IEnumerator PlayDamage(DamageInfo info)
        {
            // 闪烁目标（被攻击方）——原实现闪攻击者（敌方），玩家看不到自己被攻击
            var go = GameObject.Find($"Piece_{info.TargetId}");
            if (go != null)
            {
                var sr = go.transform.Find("Portrait")?.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    sr.color = Color.white;
                    yield return new WaitForSeconds(0.08f);
                    sr.color = PieceViewFactory.TintFor(_state.GetPiece(info.TargetId)?.DefId ?? 0);
                }
            }
            yield return new WaitForSeconds(0.15f);
        }

        IEnumerator PlayDeploy(DeployInfo info)
        {
            // 复用场景已有视觉（敌方 BoardBuilder 摆的）或新建
            if (GameObject.Find($"Piece_{info.PieceId}") == null)
            {
                var existing = FindEnemyVisualAt(info.Cell);
                if (existing != null)
                {
                    existing.name = $"Piece_{info.PieceId}";
                }
                else
                {
                    PieceViewFactory.CreatePieceView(info.PieceId, info.Side, info.Cell,
                        info.Side == Side.Player ? PieceViewFactory.TintFor(info.DefId) : PieceViewFactory.TintFor(info.DefId + 1));
                }
            }
            yield return new WaitForSeconds(0.1f);
        }

        IEnumerator PlayDeath(DeathInfo info)
        {
            var go = GameObject.Find($"Piece_{info.PieceId}");
            if (go != null)
            {
                var sr = go.transform.Find("Portrait")?.GetComponent<SpriteRenderer>();
                if (sr != null) sr.material.DOFade(0f, 0.2f); // material 版扩展（DOTween.dll 内）
                go.transform.DOScale(0f, 0.2f);
                yield return new WaitForSeconds(0.25f);
                Destroy(go);
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
            StartCoroutine(LoadTooltipAndShow(slotIndex, leftTopWorld));
        }

        bool _tooltipLoading; // 加载锁：防重入双实例（首帧加载慢时连续 hover 会并发创建两个浮窗）

        IEnumerator LoadTooltipAndShow(int slotIndex, Vector3 leftTopWorld)
        {
            if (_tooltipLoading)
            {
                yield break; // 加载中：忽略重复请求（加载完成后下次 hover 自然显示）
            }
            if (_tooltip == null)
            {
                _tooltipLoading = true;
                var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("BehaviorTooltip");
                yield return handle;
                _tooltipLoading = false;
                if (handle.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    Debug.LogWarning("[Battle] BehaviorTooltip 加载失败");
                    yield break;
                }
                if (_tooltip != null)
                {
                    yield break; // 双保险：加载期间已被其他路径创建（防双实例）
                }
                var canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
                if (canvas != null && canvas.transform.parent != null) canvas = null; // 只挂根 Canvas（跳过子 Canvas）
                if (canvas == null)
                {
                    // 兜底：遍历找根 Canvas（FindObjectOfType 顺序未定义，可能先命中子 Canvas）
                    var all = UnityEngine.Object.FindObjectsOfType<Canvas>();
                    foreach (var c in all)
                    {
                        if (c.transform.parent == null) { canvas = c; break; }
                    }
                }
                var go = Instantiate(handle.Result, canvas != null ? canvas.transform : null);
                _tooltip = go.GetComponent<RectTransform>();                _tooltipText = go.transform.Find("Txt_Desc")?.GetComponent<TMPro.TextMeshProUGUI>();
                _tooltip.pivot = new Vector2(1f, 1f); // 右上角为锚点
            }
            if (_tooltipText != null)
            {
                // 复查：异步加载期间玩家可能切换选中 → _infoProgram 已换/变短（防 NRE/越界）
                if (_infoProgram == null || slotIndex < 0 || slotIndex >= _infoProgram.Count)
                {
                    yield break;
                }
                _tooltipText.text = SlotDetailDesc(_infoProgram[slotIndex]);
            }
            // 用 Canvas 的 worldCamera（UICamera）算屏幕坐标——主相机斜俯视坐标系不一致会定位到屏幕外
            var tooltipCanvas = _tooltip.GetComponentInParent<Canvas>();
            var uiCam = tooltipCanvas != null ? tooltipCanvas.worldCamera : null;
            if (uiCam != null)
            {
                // 屏幕坐标用主相机（玩家视角——块是 3D 对象主相机渲染）；再转 UICamera 的 Canvas 平面
                // 用 UICamera 投影会与玩家看到的块位置偏差（两相机视角不同）
                var screenPt = Camera.main != null
                    ? Camera.main.WorldToScreenPoint(leftTopWorld)
                    : uiCam.WorldToScreenPoint(leftTopWorld);
                if (RectTransformUtility.ScreenPointToWorldPointInRectangle(
                        tooltipCanvas.transform as RectTransform, screenPt, uiCam, out var worldPt))
                {
                    _tooltip.position = worldPt; // 右上角 = sprite 左上角（玩家视角）
                }
            }
            _tooltip.gameObject.SetActive(true);
        }

        public void HideBehaviorTooltip()
        {
            if (_tooltip != null) _tooltip.gameObject.SetActive(false);
        }

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
            // 2026-08-12 架构重构（B 审查阻塞项）：BattleController 跨战斗复用（不重建）——CreateAsync 回调只跑一次，
            // 第 2 场起 BattlePanel 需在此重新 ShowPanel（首场回调已 Show，此处幂等）
            if (_state.Phase == BattlePhase.Placement && _panel != null && _uiManager != null)
            {
                _uiManager.ShowPanel("Battle");
            }
            RefreshAll();
            RefreshTurnProgress(); // 回合进度条刷新（阶段切换=回合推进）
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
            float targetY = expanded ? 50f : -60f;
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

        /// <summary>手牌是否还有初始棋子（摆放前置判断）。</summary>
        bool HasInitialInHand()
        {
            foreach (var defId in _state.Hand)
            {
                var def = ConfigTable.Find<PieceDef>(defId);
                if (def != null && def.pieceType == PieceType.Initial) return true;
            }
            return false;
        }

        void OnAPChanged(object data)
        {
            RefreshAP();
        }

        /// <summary>通用状态通知（规则层字符串信号——如 placement-incomplete：摆放未完成拒绝结束）。</summary>
        void OnStateChanged(object data)
        {
            if (data is string s && s == "placement-incomplete")
            {
                RefreshPhaseButton(); // 刷新按钮状态（提示继续摆放）
            }
        }

        void OnHandChanged(object data)
        {
            if (data == null) return; // AddToEnemyWavePool 也发 HandChanged(null)——敌方侧变化不重建玩家手牌
            RebuildHand();
            RefreshPhaseButton(); // 手牌变化 → 摆放前置状态可能变化（按钮可用性）
        }

        // ========== UI 刷新 ==========
        void RefreshAll()
        {
            RefreshPhaseButton();
            RefreshAP();
            RebuildHand();
        }

        void RefreshPhaseButton()
        {
            if (_panel == null || _panel.PhaseButton == null) return;
            Debug.Log($"[Battle] RefreshPhaseButton phase={_state.Phase} eventNameSet=true");
            var btn = _panel.PhaseButton;
            var txt = _panel.PhaseButtonText;
            switch (_state.Phase)
            {
                case BattlePhase.Placement:
                    // 摆放前置（规则层）：手牌还有初始棋子时禁用（文字恒为"结束准备"）
                    btn.interactable = !HasInitialInHand();
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
                default:
                    btn.gameObject.SetActive(false);
                    break;
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

        void EnsureInfoRefs()
        {
            if (_infoName != null) return;
            var ui = GameObject.Find("UI");
            if (ui == null) return;
            _infoRoot = ui;
            _infoName = GetTmp(ui, "Txt_Name");
            _infoType = GetTmp(ui, "Txt_Type");
            _infoValue = GetTmp(ui, "Txt_Value");
            _infoDurability = GetTmp(ui, "Txt_Durability");
            _infoAbilities = GetTmp(ui, "Txt_SpecialAbilities");
            _infoOther = GetTmp(ui, "Txt_Other"); // 单节点多行 buff 区（2026-08-11：场景无 Txt_Other1~3，改为单 Txt_Other）
            for (int i = 0; i < 4; i++)
            {
                var t = ui.transform.Find($"Txt_BehaviorLogic{i + 1}");
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

        static TMPro.TextMeshPro GetTmp(GameObject root, string name)
        {
            var t = root.transform.Find(name);
            return t != null ? t.GetComponent<TMPro.TextMeshPro>() : null;
        }

        public void ShowPieceInfo(int pieceId)
        {
            EnsureInfoRefs();
            var piece = _state.GetPiece(pieceId);
            if (piece == null) return;
            var def = piece.def;
            if (def == null) return;

            FillInfo(def, piece);
            if (_infoRoot != null) _infoRoot.SetActive(true);
        }

        void FillInfo(PieceDef def, PieceInstance piece)
        {
            // 名称带阵营：士兵(友) / 士兵(敌)（2026-08-11：阵营并入名称）
            string sideTag = piece != null ? (piece.side == Side.Player ? "(友)" : "(敌)") : "";
            Set(_infoName, $"{def.displayName}{sideTag}");
            Set(_infoType, def.pieceType == PieceType.Initial ? "初始" : def.pieceType == PieceType.Deployable ? "部署" : "升变");
            Set(_infoValue, def.value.ToString());
            Set(_infoDurability, piece != null ? $"{piece.durability}/{def.durability}" : def.durability.ToString());
            var abilities = new List<string>();
            foreach (var a in def.specialAbilities) abilities.Add(DisplayNames.OfAbilityType(a.type)); // 中文映射（2026-08-11：防英文枚举泄漏）
            if (piece != null)
            {
                foreach (var a in piece.GetAllAbilities())
                {
                    string cn = DisplayNames.OfAbilityType(a.type);
                    if (!abilities.Contains(cn)) abilities.Add(cn);
                }
            }
            Set(_infoAbilities, abilities.Count > 0 ? string.Join(", ", abilities) : "无");
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
            if (_infoRoot != null) _infoRoot.SetActive(false);
            Set(_infoName, "");
            Set(_infoType, "");
            Set(_infoValue, "");
            Set(_infoDurability, "");
            Set(_infoAbilities, "");
            Set(_infoOther, "");
            for (int i = 0; i < 4; i++)
            {
                if (_infoProgramBlocks[i] != null) _infoProgramBlocks[i].gameObject.SetActive(false);
            }
        }

        static void Set(TMPro.TextMeshPro tmp, string text)
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
                default:
                    return "跳：跳过本回合行动";
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
            string key = string.Join(",", _state.Hand);
            if (key == _lastHandKey && _panel.HandRoot.childCount > 0)
            {
                return;
            }
            _lastHandKey = key;

            // 拖拽中重建：先清理拖拽状态（防 _draggingCard 卡死/预览泄漏）
            if (_draggingCard)
            {
                _draggingCard = false;
                _dragCard = null;
                if (_previewPiece != null) Destroy(_previewPiece);
                _previewPiece = null;
                _previewCell = new Vector2Int(-1, -1);
            }

            // 手牌卡为独立 prefab（Piece_Handcard）——Addressables 按需加载
            // 注意：清空旧卡放在协程内（加载完成后同帧清+建）——避免重建中间空白帧（闪一下）
            _handBuildSeq++;
            StartCoroutine(LoadAndBuildHand(_handBuildSeq));
        }

        IEnumerator LoadAndBuildHand(int seq)
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Piece_Handcard");
            yield return handle;
            if (seq != _handBuildSeq) yield break; // 过期重建请求：放弃（防双份卡片）
            if (handle.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                Debug.LogWarning("[Battle] 手牌卡 prefab 加载失败（address=Piece_Handcard）");
                yield break;
            }
            var template = handle.Result;
            // 差异重建：同一 defId 复用旧卡（布局插值滑动到新位置）；只销毁移除/新建新增
            var oldCards = new List<(GameObject go, int defId)>();
            foreach (Transform child in _panel.HandRoot)
            {
                var drag = child.GetComponent<HandCardDrag>();
                if (drag != null) oldCards.Add((child.gameObject, drag.DefId));
            }
            bool fromEmpty = oldCards.Count == 0;
            var reused = new bool[oldCards.Count];
            var layout = _panel.HandRoot.GetComponent<HandLayoutController>();
            if (layout == null) layout = _panel.HandRoot.gameObject.AddComponent<HandLayoutController>();
            // 手牌显示排序：类型优先（初始→部署→升变）+ 同类型价值升序（全场景统一——CardTypeColors.SortPieces）
            var handDefs = new List<PieceDef>();
            foreach (var id in _state.Hand)
            {
                var d = ConfigTable.Find<PieceDef>(id);
                if (d != null) handDefs.Add(d);
            }
            CardTypeColors.SortPieces(handDefs);
            var hand = handDefs.ConvertAll(d => d.Id);
            for (int i = 0; i < hand.Count; i++)
            {
                var def = ConfigTable.Get<PieceDef>(hand[i]);
                if (def == null) continue; // 配置缺失防御（缺卡不建，避免 NRE 中止整个协程）
                GameObject card = null;
                // 复用：找第一个未复用且 defId 相同的旧卡（保留其位置 → 布局插值滑动）
                for (int j = 0; j < oldCards.Count; j++)
                {
                    if (!reused[j] && oldCards[j].defId == def.Id)
                    {
                        card = oldCards[j].go;
                        reused[j] = true;
                        break;
                    }
                }
                if (card == null)
                {
                    card = Instantiate(template, _panel.HandRoot);
                    card.SetActive(true);
                    FillCard(card, def, i);
                    AddCardDrag(card, def.Id, i);
                    if (fromEmpty) FadeInCard(card, i); // 仅从无到有时淡入
                }
                else
                {
                    card.name = $"Card_{i}_{def.displayName}";
                    card.SetActive(true);
                    // 复用卡视觉重置（防拖出动画残留：alpha=0/scale=0.3 时隐形）
                    var cg = card.GetComponent<CanvasGroup>();
                    if (cg != null) cg.alpha = 1f;
                    card.transform.localScale = Vector3.one * 0.35f;
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

        void FillCard(GameObject card, PieceDef def, int index)
        {
            // 卡背景色按种类标识（低饱和度：初始=绿 / 部署=蓝 / 升变=红）
            var bg = card.GetComponent<Image>();
            if (bg != null) bg.color = CardTypeColors.For(def.pieceType);
            var nameText = FindCardNode(card.transform, "Txt_InfoName")?.GetComponent<TMP_Text>();
            if (nameText != null) nameText.text = VerticalName(def.displayName); // 竖排（一字一行）
            var valueText = FindCardNode(card.transform, "Img_InfoValue")?.GetComponentInChildren<TMP_Text>();
            if (valueText != null) valueText.text = def.value.ToString();
            var typeText = FindCardNode(card.transform, "Img_InfoType")?.GetComponentInChildren<TMP_Text>();
            if (typeText != null) typeText.text = def.pieceType == PieceType.Initial ? "始" : def.pieceType == PieceType.Deployable ? "部" : "升";
            // 程序描述 + 槽位显隐（未配置的块/解释隐藏；每个槽填各自的单槽描述）
            // 程序 = 编辑差异优先（CurrentPrograms——编辑结果在此），回退 Def 默认模组（2026-08-11 数据链修复）
            int slotCount = 0;
            List<Template> slots = null;
            if (_state != null && _state.TryGetCurrentProgram(def.Id, out var edited)) slots = edited;
            else if (def.programSet != null && def.programSet.Count > 0 && def.programSet[0].slots != null) slots = def.programSet[0].slots;
            if (slots != null) slotCount = Mathf.Min(slots.Count, 4);
            for (int s = 0; s < 4; s++)
            {
                bool show = s < slotCount;
                var block = FindCardNode(card.transform, $"Img_InfoProgram{s + 1}");
                if (block != null)
                {
                    block.gameObject.SetActive(show);
                    // 槽位图标文字（移/攻/跳）
                    var blockText = block.GetComponentInChildren<TMP_Text>();
                    if (blockText != null && show) blockText.text = SlotTypeCharStatic(slots[s]);
                }
                var desc = FindCardNode(card.transform, $"Txt_InfoProgram{s + 1}Desc");
                if (desc != null)
                {
                    desc.gameObject.SetActive(show);
                    if (show)
                    {
                        var tmp = desc.GetComponent<TMP_Text>();
                        if (tmp != null) tmp.text = SlotDetailDesc(slots[s]); // 单槽自然语言描述
                    }
                }
            }
        }

        /// <summary>槽位图标字符（移/攻/跳）。</summary>
        static string SlotTypeCharStatic(Template t)
        {
            switch (t)
            {
                case MoveTemplate: return "移";
                case AttackTemplate: return "攻";
                default: return "跳";
            }
        }

        /// <summary>竖排名称：每个字符一行（卡片名称竖向显示）。</summary>
        /// <summary>卡片背景色（种类标识，低饱和度：初始=浅绿 / 部署=浅蓝 / 升变=浅红）。</summary>
        static Color CardTypeColor(PieceType type)
        {
            switch (type)
            {
                case PieceType.Initial: return new Color(0.58f, 0.78f, 0.58f, 1f);   // 浅绿
                case PieceType.Deployable: return new Color(0.58f, 0.70f, 0.85f, 1f); // 浅蓝
                default: return new Color(0.85f, 0.62f, 0.62f, 1f);                   // 浅红（升变）
            }
        }

        static string VerticalName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return string.Join("\n", name.ToCharArray());
        }

        /// <summary>卡片节点容错查找：精确匹配失败则递归前缀匹配（节点名可能有 "(1)" 复制后缀）。</summary>
        static Transform FindCardNode(Transform root, string name)
        {
            var exact = root.Find(name);
            if (exact != null) return exact;
            foreach (Transform child in root)
            {
                if (child.name.StartsWith(name)) return child;
                var deeper = FindCardNode(child, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        void AddCardDrag(GameObject card, int defId, int index)
        {
            var drag = card.AddComponent<HandCardDrag>();
            drag.Init(this, defId, index);
        }

        public bool CanDragCard(int defId)
        {
            // 阶段限定种类（与规则层 IsDeployAllowed 一致）：Placement=初始 / PlayerTurn=部署；升变牌靠升变操作上场不可部署
            var def = ConfigTable.Find<PieceDef>(defId);
            if (def == null) return false;
            bool typeOk = _state.Phase == BattlePhase.Placement
                ? def.pieceType == PieceType.Initial
                : def.pieceType == PieceType.Deployable;
            // 执行中/表现播放中禁止拖拽（防时序错乱）
            return typeOk && !_executing && !_presentationPlaying
                && (_state.Phase == BattlePhase.Placement || _state.Phase == BattlePhase.PlayerTurn);
        }

        // ========== 拖拽部署 ==========
        public void OnCardDragStart(int defId, GameObject card)
        {
            if (!CanDragCard(defId)) return;
            if (_previewPiece != null) Destroy(_previewPiece); // 防旧预览泄漏
            _draggingCard = true;
            _dragDefId = defId;
            _dragCard = card;
            PieceViewFactory.EnsureSprites();
            _previewPiece = PieceViewFactory.CreatePieceView(-1, Side.Player, new Vector2Int(-9, -9),
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
            // 命中合法格 → 吸附定位点
            if (Physics.Raycast(ray, out var hit, 200f))
            {
                var cell = PieceViewFactory.CellFromWorld(hit.point);
                if (IsDeployableCell(cell))
                {
                    _previewPiece.transform.position = PieceViewFactory.CellToWorld(cell);
                    _previewCell = cell;
                    RefacePreview();
                    return;
                }
            }
            // 未吸附：立绘跟随光标（射线与 y=0 棋盘平面交点，保持可见）
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(ray, out float enter))
            {
                var p = ray.GetPoint(enter);
                p.y = Mathf.Clamp(p.y, 0.05f, 5f); // 略高于棋盘，不穿地
                _previewPiece.transform.position = p;
                RefacePreview();
            }
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

        public void OnCardDragEnd()
        {
            if (!_draggingCard) return;
            _draggingCard = false;
            if (_previewCell.x >= 0)
            {
                bool free = _state.Phase == BattlePhase.Placement;
                _flow.OnPlayerRequestDeploy(new DeployRequest(_dragDefId, _previewCell) { free = free });
                // 成功：PieceDeployed → 规则层 Hand.Remove → OnPieceDeployed 重建手牌
                // 失败兜底：0.5s 后 Hand 仍含该 defId → 恢复卡片（引用提前捕获——_dragCard 本方法末尾置空）
                var card = _dragCard;
                StartCoroutine(RecoverCardIfFailed(_dragDefId, card));
            }
            else
            {
                RestoreDragCard(); // 非法格：只恢复拖出的卡片（不整体重建）
            }
            if (_previewPiece != null) Destroy(_previewPiece);
            _previewPiece = null;
            _previewCell = new Vector2Int(-1, -1);
            _dragDefId = -1;
            _dragCard = null; // 统一清理（防野引用）
        }

        IEnumerator RecoverCardIfFailed(int defId, GameObject card)
        {
            yield return new WaitForSeconds(0.5f);
            if (_state.Hand.Contains(defId))
            {
                RestoreDragCard(card); // 规则层未移除 → 部署失败 → 恢复卡片（幂等）
            }
        }

        /// <summary>恢复拖出的卡片（淡出动画倒放），不重建手牌——避免无变化闪烁。</summary>
        void RestoreDragCard(GameObject card = null)
        {
            if (card == null) card = _dragCard;
            if (card == null) return; // 卡片已销毁（Unity 伪 null）或拖拽清理后无引用
            var cg = card.GetComponent<CanvasGroup>();
            if (cg != null)
            {
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
            return true;
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
        CanvasGroup _cg;
        DG.Tweening.Tween _fadeTween; // 拖出淡出 tween（显式管理，防销毁后访问）

        public int DefId => _defId; // 差异重建时按 defId 复用卡片

        public void Init(BattleController controller, int defId, int cardIndex)
        {
            _controller = controller;
            _defId = defId;
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
            DG.Tweening.DOTween.Kill(transform); // 拖出缩小 tween（有 target，也要杀）
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_controller.CanDragCard(_defId)) return;
            _controller.OnCardDragStart(_defId, gameObject);
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
            _controller.OnCardDragEnd();
        }
    }
}
