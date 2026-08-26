using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TheLaw.Core;
using TheLaw.Data;

namespace TheLaw.UI
{
    /// <summary>
    /// 新手教程管理器（Bootstrap 创建）：观察既有事件（只读）→ 按策划顺序展示教程序列。
    /// - 序列/步骤：TutorialContent（代码配置，文案源策划 docx）
    /// - 展示：TutorialPanel（角色说话，任意键下一步）+ TutorialMask（shader 挖孔高亮）
    /// - 去重：会话内 HashSet；持久化由后端 TutorialSystem.TryShow 发 TutorialRequested 事件承担（跨局去重）
    /// - 收口（2026-08-26）：deck_intro 已单源到后端（deck_standard→TryShow）；event_intro/floor_rule_intro/battle_intro 为过渡观察路径，待后端补 TryShow 后移除
    /// - 等待步：waitEvent 未触发时忽略推进；8s 超时跳过该步
    /// </summary>
    public class TutorialManager : MonoBehaviour
    {
        const float HighlightRetryInterval = 0.1f;
        const float WaitEventTimeout = 8f;

        UIManager _ui;
        TutorialPanel _panel;
        TutorialMask _mask;
        bool _panelPushed;

        readonly HashSet<string> _shown = new HashSet<string>();
        readonly HashSet<string> _waitEventsFired = new HashSet<string>();

        TutorialSequence _current;
        System.Func<bool> _contextValid; // 场景上下文校验：教程所教状态失效 → 自动取消本阶段（用户定案：思路2）
        int _stepIndex;
        bool _active;
        bool _runStarted;
        string _pendingId;
        string _pendingStart;
        Coroutine _waitRoutine;
        Coroutine _panelWaitRoutine;

        readonly List<string> _pendingHighlights = new List<string>();

        // ====== 生命周期 ======

        public void Init(UIManager ui)
        {
            _ui = ui;
            PanelBase.CreateAsync<TutorialPanel>(p =>
            {
                _panel = p;
                if (_ui != null) _ui.RegisterPanel(p);
                p.OnAdvanceRequested += RequestNext;
                p.OnBackRequested += GoBack;
                p.OnSkipRequested += Finish;
                if (!string.IsNullOrEmpty(_pendingStart))
                {
                    string id = _pendingStart;
                    _pendingStart = null;
                    ShowSequence(id);
                }
            });
            WireEvents();
        }

        /// <summary>新局开始（Bootstrap.StartNewGame 调用）：标记开局、清等待事件。</summary>
        public void OnNewRun()
        {
            _runStarted = true;
            _waitEventsFired.Clear();
        }

        void OnDestroy()
        {
            UnwireEvents();
            if (_panel != null)
            {
                _panel.OnAdvanceRequested -= RequestNext;
                _panel.OnBackRequested -= GoBack;
                _panel.OnSkipRequested -= Finish;
            }
        }

        // ====== 事件订阅（只读观察，不改状态） ======

        void WireEvents()
        {
            var ec = EventCenter.Instance;
            ec.AddEventListener(GameEvent.EventOpened, OnEventOpened);
            ec.AddEventListener(GameEvent.AbilityCandidatesDrawn, OnAbilityCandidates);
            ec.AddEventListener(GameEvent.RuleCandidatesDrawn, OnRuleCandidates);
            ec.AddEventListener(GameEvent.RelicObtained, OnRelicObtained);
            ec.AddEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
            ec.AddEventListener(GameEvent.TutorialRequested, OnTutorialRequested);
        }

        void UnwireEvents()
        {
            var ec = EventCenter.Instance;
            if (ec == null) return;
            ec.RemoveEventListener(GameEvent.EventOpened, OnEventOpened);
            ec.RemoveEventListener(GameEvent.AbilityCandidatesDrawn, OnAbilityCandidates);
            ec.RemoveEventListener(GameEvent.RuleCandidatesDrawn, OnRuleCandidates);
            ec.RemoveEventListener(GameEvent.RelicObtained, OnRelicObtained);
            ec.RemoveEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
            ec.RemoveEventListener(GameEvent.TutorialRequested, OnTutorialRequested);
        }

        void OnEventOpened(object data)
        {
            TryFirstEvent(); // 过渡保留：后端暂未对普通事件发 TutorialRequested（事件打开仅覆盖 edit/deck 两序列）——后端补 TryShow 后移除
        }

        /// <summary>教程契约（2026-08-25 后端 TutorialSystem.TryShow 审核通过后发）：携带教程序列 id → 播放。</summary>
        void OnTutorialRequested(object data)
        {
            if (data is string id && !string.IsNullOrEmpty(id)) ShowSequence(id);
        }

        void OnAbilityCandidates(object data)
        {
            TryFirstEvent(); // 首事件 = 能力事件（选遗物）→ 事件界面教程
        }

        // ⚠️ 过渡保留：floor_rule_intro / battle_intro / event_intro 后端暂未发 TutorialRequested——观察路径为唯一来源；deck_intro 已收口到后端（见 OnStateChanged 移除记录）
        void OnRuleCandidates(object data)
        {
            if (_shown.Contains("event_intro"))
            {
                // 事件界面教程已播过 → 本玩法事件 = 下一层起始 → 玩法选择教程（docx 最终版新增段）
                ShowSequence("floor_rule_intro");
            }
            else
            {
                TryFirstEvent();
            }
        }

        /// <summary>本局第一个事件界面（普通/能力/玩法任一）→ 事件界面教程。</summary>
        void TryFirstEvent()
        {
            if (!_runStarted) return;
            if (_shown.Contains("event_intro")) return;
            ShowSequence("event_intro");
        }

        void OnRelicObtained(object data)
        {
            _waitEventsFired.Add("RelicObtained");
            if (_active && _current != null)
            {
                int next = _stepIndex + 1;
                if (next < _current.steps.Count && _current.steps[next].waitEvent == "RelicObtained")
                {
                    CancelWaitTimeout();
                    _stepIndex = next;
                    ShowCurrent();
                }
            }
        }

        void OnPhaseChanged(object data) // ⚠️ 过渡保留：battle_intro 后端未覆盖（见上方注释）
        {
            if (!(data is BattlePhase phase)) return;
            if (phase == BattlePhase.Placement && _runStarted && !_shown.Contains("battle_intro"))
            {
                ShowSequence("battle_intro");
            }
        }

        // ====== 序列控制 ======

        public void ShowSequence(string id)
        {
            if (_shown.Contains(id)) return;
            if (_active)
            {
                _pendingId = id; // 当前序列未结束，排队
                return;
            }
            var seq = TutorialContent.Find(id);
            if (seq == null)
            {
                Debug.LogWarning("[TutorialManager] 未知教程序列：" + id);
                return;
            }
            if (_panel == null)
            {
                _pendingStart = id; // 面板异步加载中，就绪后自动开始
                return;
            }
            _shown.Add(id);
            _current = seq;
            _contextValid = ContextFor(id); // 绑定上下文校验（所教状态退出 → 取消）
            _stepIndex = 0;
            _active = true;
            EnsurePanelPushed();
            EnsureMask();
            if (_mask != null) _mask.SetBlocking(true); // 教程期间阻挡下层面板交互
            ShowCurrent();
        }

        void ShowCurrent()
        {
            if (_current == null) return;
            var step = _current.steps[_stepIndex];
            if (_panel != null) _panel.ShowStep(step, _stepIndex > 0);

            // 高亮解析（未就绪目标每帧重试）
            _pendingHighlights.Clear();
            if (step.highlightTargets != null && step.highlightTargets.Count > 0)
            {
                foreach (var t in step.highlightTargets)
                {
                    _pendingHighlights.Add(t);
                }
            }
            ApplyResolvedHighlights();

            // 等待步：若下一步有 waitEvent 且未触发，忽略推进（等事件或超时跳过）
            int next = _stepIndex + 1;
            if (next < _current.steps.Count)
            {
                var nextStep = _current.steps[next];
                if (!string.IsNullOrEmpty(nextStep.waitEvent) && !_waitEventsFired.Contains(nextStep.waitEvent))
                {
                    StartWaitTimeout();
                }
            }
        }

        void RequestNext()
        {
            if (!_active || _current == null) return;
            int next = _stepIndex + 1;
            if (next >= _current.steps.Count)
            {
                Finish();
                return;
            }
            var step = _current.steps[next];
            if (!string.IsNullOrEmpty(step.waitEvent) && !_waitEventsFired.Contains(step.waitEvent))
            {
                return; // 等待事件中：本次推进忽略
            }
            CancelWaitTimeout();
            _stepIndex = next;
            ShowCurrent();
        }

        void GoBack()
        {
            if (!_active || _current == null) return;
            if (_stepIndex <= 0) return;
            CancelWaitTimeout();
            _stepIndex--;
            ShowCurrent();
        }

        void Finish()
        {
            CancelWaitTimeout();
            _active = false;
            _current = null;
            _contextValid = null;
            _pendingHighlights.Clear();
            if (_panel != null) _panel.HideAll();
            if (_panelPushed && _ui != null)
            {
                _ui.PopOverlay();
                _panelPushed = false;
            }
            if (_mask != null)
            {
                _mask.SetVisible(false);
                _mask.SetBlocking(false); // 教程结束恢复交互
            }
            if (!string.IsNullOrEmpty(_pendingId))
            {
                string id = _pendingId;
                _pendingId = null;
                ShowSequence(id);
            }
        }

        /// <summary>
        /// 场景上下文失效 → 取消当前阶段教程（用户定案：思路2）：
        /// 教程所教的面板/状态已退出（如教程没播完就选了能力、事件面板已关闭）→ 立即收尾，
        /// 不启动排队序列（避免在错误上下文续播造成"图层压栈上诡异显示"）。
        /// </summary>
        void CancelSequence()
        {
            CancelWaitTimeout();
            _active = false;
            _current = null;
            _contextValid = null;
            _pendingHighlights.Clear();
            if (_panel != null) _panel.HideAll();
            if (_panelPushed && _ui != null)
            {
                _ui.PopOverlay();
                _panelPushed = false;
            }
            if (_mask != null)
            {
                _mask.SetVisible(false);
                _mask.SetBlocking(false);
            }
            _pendingId = null; // 取消不续播排队序列
        }

        /// <summary>序列 → 上下文校验函数（null = 不校验）。</summary>
        System.Func<bool> ContextFor(string id)
        {
            switch (id)
            {
                case "event_intro":      return () => IsPanelVisible("EventPanel");
                case "edit_intro":       return () => IsPanelVisible("PieceEdit");
                case "deck_intro":       return () => IsPanelVisible("DeckBuild");
                case "battle_intro":     return () => IsPanelVisible("Battle");
                case "floor_rule_intro": return () => IsPanelVisible("EventPanel");
                default:                 return null;
            }
        }

        bool IsPanelVisible(string key)
        {
            var mb = _ui != null ? _ui.GetPanel(key) as MonoBehaviour : null;
            return mb != null && mb.gameObject.activeInHierarchy;
        }

        void StartWaitTimeout()
        {
            CancelWaitTimeout();
            _waitRoutine = StartCoroutine(WaitTimeoutRoutine());
        }

        void CancelWaitTimeout()
        {
            if (_waitRoutine != null) { StopCoroutine(_waitRoutine); _waitRoutine = null; }
        }

        IEnumerator WaitTimeoutRoutine()
        {
            yield return new WaitForSecondsRealtime(WaitEventTimeout);
            _waitRoutine = null;
            if (!_active || _current == null) yield break;
            // 事件仍未触发 → 跳过等待步
            int skipTo = _stepIndex + 2;
            if (skipTo >= _current.steps.Count)
            {
                Finish();
            }
            else
            {
                _stepIndex = skipTo;
                ShowCurrent();
            }
        }

        // ====== 面板 / 遮罩 ======

        void EnsurePanelPushed()
        {
            if (_panelPushed || _ui == null || _panel == null) return;
            _ui.PushOverlay("Tutorial");
            _panelPushed = true;
        }

        void EnsureMask()
        {
            if (_mask == null)
            {
                // 层级由 TutorialMask.EnsureLayered 每帧自愈（挂教程面板 Canvas 首子节点，继承 21000 排序）
                _mask = TutorialMask.Create(FindUICamera());
            }
            if (_mask != null) _mask.SetVisible(true);
        }

        static Camera FindUICamera()
        {
            var vp = Object.FindObjectOfType<UICameraViewport>();
            if (vp != null)
            {
                var cam = vp.GetComponent<Camera>();
                if (cam != null) return cam;
            }
            return Camera.main;
        }

        // ====== 高亮解析 ======

        void Update()
        {
            TryTriggerEditIntro(); // 编辑教程触发点 = 进入棋子编辑面板后（轮询可见，非候选事件）
            if (!_active) return;
            // 场景上下文检查：所教状态已退出（如教程未播完就选定能力）→ 立即取消本阶段，防"图层压栈上诡异显示"
            if (_contextValid != null && !_contextValid())
            {
                CancelSequence();
                return;
            }
            if (_pendingHighlights.Count > 0)
            {
                _retryTimer -= Time.unscaledDeltaTime;
                if (_retryTimer <= 0f)
                {
                    _retryTimer = HighlightRetryInterval;
                    ApplyResolvedHighlights();
                }
            }
        }

        float _retryTimer;

        /// <summary>编辑教程：棋子编辑面板（PieceEdit）可见时触发（用户定案：进入面板后，而非三选一候选时）。</summary>
        void TryTriggerEditIntro()
        {
            if (!_runStarted || _shown.Contains("edit_intro")) return;
            var mb = _ui != null ? _ui.GetPanel("PieceEdit") as MonoBehaviour : null;
            if (mb != null && mb.gameObject.activeInHierarchy)
            {
                ShowSequence("edit_intro");
            }
        }

        void ApplyResolvedHighlights()
        {
            if (_pendingHighlights.Count == 0)
            {
                if (_mask != null) _mask.SetVisible(false);
                return;
            }
            var resolved = new List<Transform>();
            var unresolved = new List<string>();
            foreach (var s in _pendingHighlights)
            {
                var ts = ResolveHighlightMulti(s);
                if (ts != null && ts.Count > 0) resolved.AddRange(ts);
                else unresolved.Add(s);
            }
            _pendingHighlights.Clear();
            _pendingHighlights.AddRange(unresolved);

            if (resolved.Count == 0)
            {
                // 目标全部未解析 → 遮罩整层隐藏（避免"全屏压暗无挖孔"黑屏）
                if (_mask != null) _mask.SetVisible(false);
                return;
            }
            if (_mask != null)
            {
                _mask.SetVisible(true);
                _mask.SetTargets(resolved, _current != null ? _current.steps[_stepIndex].highlightPadding : 26f);
                // 挖孔覆盖 ≥97% 屏幕（如误选全屏面板根节点）→ 无高亮意义，整层隐藏
                if (_mask.CoversMostOfScreen())
                {
                    _mask.SetVisible(false);
                }
            }
        }

        /// <summary>解析高亮目标（支持一个 spec → 多个目标联合挖孔）。</summary>
        List<Transform> ResolveHighlightMulti(string spec)
        {
            var list = new List<Transform>();
            if (spec == "@leftInfoExAbilities")
            {
                // 左栏分数区：Grp_L 全部内容，排除 Txt_Abilities_K / Txt_Abilities（用户定案）
                var grpL = FindAnyScene("Grp_L");
                if (grpL != null)
                {
                    foreach (Transform ch in grpL)
                    {
                        if (!ch.gameObject.activeInHierarchy) continue;
                        if (ch.GetComponent<RectTransform>() == null) continue;
                        if (ch.name == "Txt_Abilities_K" || ch.name == "Txt_Abilities") continue;
                        list.Add(ch);
                    }
                }
                return list;
            }
            var single = ResolveHighlight(spec);
            if (single != null) list.Add(single);
            return list;
        }

        /// <summary>解析高亮目标：普通名/路径在面板内 FindDeep、场景内 GameObject.Find；"@xxx" 走动态解析。</summary>
        Transform ResolveHighlight(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            if (spec[0] == '@')
            {
                return ResolveDynamic(spec.Substring(1));
            }
            // 面板内查找（常见面板 Key 全试）
            foreach (var key in new[] { "Battle", "EventPanel", "PieceEdit", "DeckBuild", "DeckLibrary" })
            {
                var mb = _ui != null ? _ui.GetPanel(key) as MonoBehaviour : null;
                if (mb != null)
                {
                    var found = TutorialPanel.FindDeep<Transform>(mb.transform, spec);
                    if (found != null) return found;
                }
            }
            var go = GameObject.Find(spec);
            return go != null ? go.transform : null;
        }

        /// <summary>动态目标（@key）：按候选节点名解析；未命中返回 null（高亮跳过，不阻塞教程）。</summary>
        Transform ResolveDynamic(string key)
        {
            switch (key)
            {
                case "handArea":
                    return FindDeepAnyPanel("Battle", "Grp_Hand", "HandArea", "Grp_HandArea", "HandLayout", "Grp_HandLayout", "Grp_HandCards");
                case "frontRows":
                    return FindAnyScene("Grp_Board", "Board", "Grid", "ChessBoard", "Grp_Grid", "BoardGrid");
                case "board":
                    return FindAnyScene("Grp_Board", "Board", "Grid", "ChessBoard", "Grp_Grid", "BoardGrid");
                case "firstPlayerPiece":
                    return FindFirstPiece();
                case "leftInfo":
                    // 左侧规则/得分栏是场景对象 Grp_L（不在 BattlePanel 内）
                    return FindAnyScene("Grp_L")
                        ?? FindDeepAnyPanel("Battle", "Grp_LeftInfo", "Grp_Info", "Grp_RuleInfo", "Grp_Status", "Grp_Rules", "Grp_Left");
                case "bottomLeft":
                    return FindDeepAnyPanel("Battle", "Grp_AP", "Grp_ActionPoint", "Grp_BottomLeft", "Grp_APArea")
                        ?? FindAnyScene("Grp_AP");
                case "bottomRight":
                    return FindDeepAnyPanel("Battle", "Grp_DrawPile", "Grp_Deck", "Grp_BottomRight", "Grp_Redraw", "Btn_Graveyard")
                        ?? FindAnyScene("Grp_DrawPile", "Btn_Graveyard");
                default:
                    return null;
            }
        }

        Transform FindDeepAnyPanel(string panelKey, params string[] names)
        {
            var mb = _ui != null ? _ui.GetPanel(panelKey) as MonoBehaviour : null;
            if (mb == null) return null;
            foreach (var n in names)
            {
                var found = TutorialPanel.FindDeep<Transform>(mb.transform, n);
                if (found != null) return found;
            }
            return null;
        }

        Transform FindAnyScene(params string[] names)
        {
            foreach (var n in names)
            {
                var go = GameObject.Find(n);
                if (go != null) return go.transform;
            }
            return null;
        }

        /// <summary>首个活跃且带 Renderer 的 "Piece*" 对象（我方已部署棋子；命名规则变化时由李毕确认）。</summary>
        Transform FindFirstPiece()
        {
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                if (t.name.StartsWith("Piece") && t.gameObject.activeInHierarchy)
                {
                    if (t.GetComponentInChildren<Renderer>() != null) return t;
                }
            }
            return null;
        }
    }
}