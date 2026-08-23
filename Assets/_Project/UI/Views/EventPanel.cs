using System;
using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 事件关面板：监听 EventOpened → 查事件定义（标题/描述/选项）→ 点选项调 EventNodeSystem.OnOptionSelected → 发 EventCompleted（TowerFlow 推进）。
    /// 交互约定（契约）：available=false 选项灰显；效果落账后无交互效果 → 直接 EventCompleted；edit/deck 效果 → 打开对应界面（后续细化）。
    /// 能力事件（2026-08-23）：isAbilityPick 事件不发 EventOpened——走 AbilityCandidatesDrawn → BuildAbilityOptions → SelectAbility / RefreshAbilityCandidate 专用分支；普通事件原流程不变。
    /// </summary>
    public class EventPanel : PanelBase
    {
        public override string Key => "EventPanel";

        // 设置按钮（Bootstrap 订阅 → PushOverlay("Settings")——面板只转发输入）
        public event Action OnSettingsClicked;
        // 退出按钮（2026-08-23：Bootstrap 弹确认窗——确认保存并返回主菜单；不再直接推进）
        public event Action OnExitClicked;

        private EventNodeSystem _eventNode;
        private EventDefinition _currentEvent;
        private string _currentEventId;
        private GameObject _optionTemplate; // Btn_EventOption prefab（Addressables 缓存）

        private GameState _gameState;          // 能力模式：读取候选/刷新次数（UI 只读——状态修改必须走 Resolver）
        private Resolver _resolver;            // 能力模式：SelectAbility / RefreshAbilityCandidate
        private bool _isAbilityPick;           // 能力事件模式（EventDefinition.isAbilityPick == true）
        private bool _abilitySelectionLocked;  // 能力选择防重复点击锁（选择后同步推进——下一事件重建时复位）

        private TMP_Text _title;
        private TMP_Text _desc;
        private Transform _optionsRoot;
        private Button _exitBtn;

        public void Init(EventNodeSystem eventNode, GameState gameState, Resolver resolver)
        {
            _eventNode = eventNode;
            _gameState = gameState;
            _resolver = resolver;
        }

        // UI 架构重构 §三.2：事件面板数据 = 事件广播驱动（ShowEvent），非 Show 驱动——
        // OnShow 无需刷新（幂等在数据层 _currentEventId：重复广播无新数据不重建）
        protected override void OnShow() { }

        /// <summary>
        /// 事件面板布局刷新：基类全量刷新后，再对 Grp_EventDesc（描述+选项容器）单独强制重排一遍——
        /// 动态文案/选项重建后偶发错乱，二次刷新兜底（2026-08-23 人工确认）。
        /// </summary>
        protected override void RefreshLayout()
        {
            base.RefreshLayout();
            var desc = _optionsRoot != null ? _optionsRoot.parent as RectTransform : null;
            if (desc == null)
            {
                var d = transform.Find("Grp_EventContent/Grp_EventDesc");
                if (d != null) desc = d as RectTransform;
            }
            if (desc == null) return;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(desc);
            foreach (var lg in desc.GetComponentsInChildren<LayoutGroup>(true))
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(lg.transform as RectTransform);
            }
            foreach (var csf in desc.GetComponentsInChildren<ContentSizeFitter>(true))
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(csf.transform as RectTransform);
            }
        }

        private void Awake()
        {
            _title = transform.Find("Grp_TopBar/Txt_EventName")?.GetComponent<TMP_Text>();
            _desc = transform.Find("Grp_EventContent/Grp_EventDesc/Txt_EventDesc")?.GetComponent<TMP_Text>();
            _optionsRoot = transform.Find("Grp_EventContent/Grp_EventDesc/Grp_EventOptions");
            // Btn_Exit 在 Grp_TopBar/Grp_Functions/ 下（prefab 布局）
            _exitBtn = transform.Find("Grp_TopBar/Grp_Functions/Btn_Exit")?.GetComponent<Button>();
            if (_exitBtn != null)
            {
                _exitBtn.onClick.RemoveAllListeners();
                _exitBtn.onClick.AddListener(() => Exit());
            }
            BindSettingsButton();
            EventCenter.Instance.AddEventListener(GameEvent.EventOpened, OnEventOpened);
            // 能力事件（2026-08-23）：不发 EventOpened——候选广播驱动三选一（刷新后同广播重建选项区）
            EventCenter.Instance.AddEventListener(GameEvent.AbilityCandidatesDrawn, OnAbilityCandidatesDrawn);
            // UI 架构重构 §六：跨局残留由"新实例"保证（局结束销毁面板）——不再需要 RunEnded 重置
            // 预加载选项按钮模板（Btn_EventOption）
            StartCoroutine(LoadOptionTemplate());
        }

        /// <summary>设置按钮按名搜全层级绑定（Bootstrap 订阅事件打开 Settings overlay）。</summary>
        void BindSettingsButton()
        {
            Button btn = null;
            foreach (var b in GetComponentsInChildren<Button>(true))
            {
                if (b.name == "Btn_Settings") { btn = b; break; }
            }
            if (btn == null)
            {
                Debug.LogWarning("[EventPanel] 未找到设置按钮 Btn_Settings");
                return;
            }
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => OnSettingsClicked?.Invoke());
        }

        System.Collections.IEnumerator LoadOptionTemplate()
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Btn_EventOption");
            yield return handle;
            if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded)
            {
                _optionTemplate = handle.Result;
            }
        }

        void OnDestroy()
        {
            EventCenter.Instance.RemoveEventListener(GameEvent.EventOpened, OnEventOpened);
            EventCenter.Instance.RemoveEventListener(GameEvent.AbilityCandidatesDrawn, OnAbilityCandidatesDrawn);
        }

        void OnEventOpened(object data)
        {
            if (data is string eventId)
            {
                ShowEvent(eventId);
            }
        }

        /// <summary>展示事件（公开——Bootstrap 懒加载完成后主动推数据，防首次事件丢失）。普通事件原流程不变；能力事件走专用分支。</summary>
        public void ShowEvent(string eventId)
        {
            if (string.IsNullOrEmpty(eventId)) return;
            if (eventId == _currentEventId) return; // 幂等：同一事件重复推送跳过（防双消费双推进）
            _currentEventId = eventId;
            _currentEvent = ConfigTable.FindByName<EventDefinition>(eventId);
            if (_currentEvent == null)
            {
                Debug.LogWarning($"[EventPanel] 事件定义未找到：{eventId}");
                Complete(); // 找不到定义直接推进（防卡关）
                return;
            }
            _abilitySelectionLocked = false; // 新事件复位能力锁（普通/能力共用）
            if (_currentEvent.isAbilityPick)
            {
                // 能力事件兜底（正常能力事件不发 EventOpened——由 AbilityCandidatesDrawn 驱动；此分支防误入）
                EnterAbilityMode(_currentEventId, _currentEvent);
                return;
            }
            _isAbilityPick = false;
            if (_exitBtn != null) _exitBtn.interactable = true; // 普通事件恢复退出按钮（能力模式已禁用——不能直接完成绕过选择）
            if (_title != null) _title.text = string.IsNullOrEmpty(_currentEvent.title) ? "未知事件" : _currentEvent.title; // 中文兜底（防资产名泄漏）
            if (_desc != null) _desc.text = Describe(_currentEvent);
            BuildOptions(); // 选项就位后内部刷新布局（时序正确——模板未就绪时由 BuildOptionsWhenReady 补刷）
            gameObject.SetActive(true);
        }

        /// <summary>描述：优先资产内 description 字段（JSON 导入）；空则回退标题/资产名（历史资产未重导入时兜底）。</summary>
        static string Describe(EventDefinition ev)
        {
            if (!string.IsNullOrEmpty(ev.description)) return ev.description;
            return ev.title;
        }

        void BuildOptions()
        {
            if (_optionsRoot == null || _currentEvent == null) return;
            if (_optionTemplate == null)
            {
                // 模板未就绪：等加载完成再生成（prefab 视觉为准——不用硬编码兜底）
                StartCoroutine(BuildOptionsWhenReady());
                return;
            }
            // 2026-08-23 时序修复：同步清空旧选项（DestroyImmediate）——延迟 Destroy 会与新建选项同帧并存，布局计算错乱
            while (_optionsRoot.childCount > 0)
            {
                DestroyImmediate(_optionsRoot.GetChild(0).gameObject);
            }
            for (int i = 0; i < _currentEvent.options.Count; i++)
            {
                var option = _currentEvent.options[i];
                var index = i;
                // 2026-08-23：双文本结构——label 含 "名称\n描述" 时按首个换行拆分（标题/描述都视为必填）
                string title = option.label;
                string content = string.Empty;
                int nl = option.label.IndexOf('\n');
                if (nl >= 0)
                {
                    title = option.label.Substring(0, nl);
                    content = option.label.Substring(nl + 1);
                }
                UIComponentFactory.CreateEventOption(
                    _optionTemplate,
                    _optionsRoot,
                    new EventOptionViewData(title, option.available, content),
                    option.available ? () => OnOptionClicked(index) : null);
            }
            RefreshLayout(); // 2026-08-23 时序修复：选项就位后再刷新布局（Grp_EventOptions 已准备好）
        }

        System.Collections.IEnumerator BuildOptionsWhenReady()
        {
            int guard = 0;
            while (_optionTemplate == null && guard++ < 300) yield return null; // 防死等（大审查 H2）
            if (_optionTemplate == null)
            {
                Debug.LogWarning("[EventPanel] 选项模板加载超时——跳过本次构建");
                yield break;
            }
            BuildOptions();
        }

        // ========== 能力事件三选一（2026-08-23：isAbilityPick 专用分支——普通事件原流程不变）==========

        /// <summary>能力事件候选广播（面板已加载——刷新后也走这里重建整个选项区）。</summary>
        void OnAbilityCandidatesDrawn(object data)
        {
            if (_gameState == null || _resolver == null) return; // 未 Init（首发由 Bootstrap 懒加载回填）
            var evId = _gameState.CurrentEventId;
            if (string.IsNullOrEmpty(evId)) return;
            var ev = ConfigTable.FindByName<EventDefinition>(evId);
            if (ev == null || !ev.isAbilityPick) return; // 非能力事件（防御——广播只应由能力事件发出）
            EnterAbilityMode(evId, ev);
        }

        /// <summary>能力模式主动回填（公开——Bootstrap 懒加载/读档路径：不能依赖历史事件广播）。成功返回 true。</summary>
        public bool ShowAbilityEventFromState()
        {
            if (_gameState == null || _resolver == null) return false;
            var evId = _gameState.CurrentEventId;
            if (string.IsNullOrEmpty(evId)) return false;
            if (_gameState.AbilityCandidates == null || _gameState.AbilityCandidates.Count == 0) return false;
            var ev = ConfigTable.FindByName<EventDefinition>(evId);
            if (ev == null || !ev.isAbilityPick) return false;
            EnterAbilityMode(evId, ev);
            return true;
        }

        /// <summary>进入能力模式：标题/描述（含刷新提示 + 候选清单）→ 重建能力选项 → 显示。</summary>
        void EnterAbilityMode(string eventId, EventDefinition ev)
        {
            _currentEventId = eventId;
            _currentEvent = ev;
            _isAbilityPick = true;
            _abilitySelectionLocked = false;
            if (_title != null) _title.text = string.IsNullOrEmpty(ev.title) ? "未知事件" : ev.title; // 中文兜底（防资产名泄漏）
            if (_desc != null) _desc.text = DescribeAbility();
            if (_exitBtn != null) _exitBtn.interactable = false; // 能力模式禁用退出（不能"直接完成"绕过能力选择）
            BuildAbilityOptions(); // 候选就位后内部刷新布局（时序正确）
            gameObject.SetActive(true);
        }

        /// <summary>能力描述：事件描述 + 操作提示；候选清单不移入文本区（2026-08-23：与按钮区重复渲染导致叠加——见 docs/能力事件显示-修复参考_20260823.md）。</summary>
        string DescribeAbility()
        {
            var sb = new System.Text.StringBuilder(Describe(_currentEvent));
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("长按选项按钮刷新事件（刷新次数）");
            return sb.ToString();
        }

        /// <summary>
        /// 能力选项区重建：每个候选 = 1 个选择选项（displayName + 描述）；刷新并入长按（长按选项按钮 = 刷新该项）。
        /// 2026-08-23：刷新不再占用独立选项按钮——避免事件界面选项过多溢出。
        /// </summary>
        void BuildAbilityOptions()
        {
            if (_optionsRoot == null || _currentEvent == null || _gameState == null) return;
            if (_optionTemplate == null)
            {
                StartCoroutine(BuildAbilityOptionsWhenReady());
                return;
            }
            var candidates = _gameState.AbilityCandidates;
            var refreshLeft = _gameState.AbilityRefreshLeft;
            if (candidates == null || candidates.Count == 0) return; // 后端已清空（选择后）——不重建空区
            // 2026-08-23 时序修复：同步清空旧候选（DestroyImmediate）——延迟 Destroy 会与新建候选同帧并存，布局计算错乱
            while (_optionsRoot.childCount > 0)
            {
                DestroyImmediate(_optionsRoot.GetChild(0).gameObject);
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                var index = i;
                var relic = candidates[i];
                string name = relic != null ? relic.displayName : $"能力候选 {index + 1}";
                string desc = relic != null ? relic.description : string.Empty;
                int left = refreshLeft != null && index < refreshLeft.Count ? refreshLeft[index] : 0;
                UIComponentFactory.CreateEventOption(
                    _optionTemplate,
                    _optionsRoot,
                    new EventOptionViewData(name, true, desc), // 2026-08-23：双文本——Title=displayName，Content=description
                    () => SelectAbility(index),
                    left > 0 ? () => RefreshAbility(index) : null); // 剩余次数用尽：不挂长按（长按无效）
            }
            RefreshLayout(); // 2026-08-23 时序修复：候选就位后再刷新布局（Grp_EventOptions 已准备好）
        }

        System.Collections.IEnumerator BuildAbilityOptionsWhenReady()
        {
            int guard = 0;
            while (_optionTemplate == null && guard++ < 300) yield return null; // 防死等（同普通选项模板）
            if (_optionTemplate == null)
            {
                Debug.LogWarning("[EventPanel] 能力选项模板加载超时——跳过本次构建");
                yield break;
            }
            BuildAbilityOptions();
        }

        /// <summary>选择能力候选：先上锁并隐藏面板（EventCompleted 同步推进——防残留/重复点击）→ Resolver 落账推进。</summary>
        void SelectAbility(int index)
        {
            if (!_isAbilityPick || _abilitySelectionLocked || _resolver == null) return;
            _abilitySelectionLocked = true; // 防重复点击锁（同步推进后不释放——下一事件重建时复位）
            gameObject.SetActive(false);    // 先隐藏：避免同步推进（EventCompleted→下一事件 EventOpened 再激活）造成残留或重复点击
            _resolver.SelectAbility(index); // 后端落账 + 清候选 + EventCompleted 推进（不走普通 Complete）
        }

        /// <summary>刷新单项候选：直接调 Resolver（后端校验次数并替换 → 再发 AbilityCandidatesDrawn → 整区重建）。</summary>
        void RefreshAbility(int index)
        {
            if (!_isAbilityPick || _abilitySelectionLocked || _resolver == null) return;
            _resolver.RefreshAbilityCandidate(index);
        }

        void OnOptionClicked(int optionIndex)
        {
            if (_eventNode == null || _currentEventId == null) return;
            _eventNode.OnOptionSelected(_currentEventId, optionIndex); // 规则层校验 + 效果落账
            // 交互效果（编辑/构筑）：隐藏事件面板等专用界面（StateChanged("edit"/"deck") 由 Bootstrap 处理）
            bool interactive = false;
            if (_currentEvent != null && optionIndex >= 0 && optionIndex < _currentEvent.options.Count)
            {
                foreach (var e in _currentEvent.options[optionIndex].effects)
                {
                    if (e.effectType == EffectType.EditProgram || e.effectType == EffectType.DeckBuild)
                    {
                        interactive = true;
                        break;
                    }
                }
            }
            if (interactive)
            {
                gameObject.SetActive(false); // 等专用面板完成（EventCompleted 推进）——下一节点 EventOpened 再激活
                return;
            }
            // 非交互效果（遗物/婉拒）：推进只能由玩家显式操作——选项区重建为"继续"按钮（禁止自动跳过）
            ShowContinue();
        }

        /// <summary>结果展示后：清空选项区 → 生成"继续"按钮（玩家点击才推进——2026-08-13 需求）。</summary>
        void ShowContinue()
        {
            if (_optionsRoot == null) { Complete(); return; }
            if (_optionTemplate == null)
            {
                StartCoroutine(ShowContinueWhenReady());
                return;
            }
            // 2026-08-23 时序修复：同步清空旧选项（DestroyImmediate）——延迟 Destroy 会与新建"继续"按钮同帧并存
            while (_optionsRoot.childCount > 0)
            {
                DestroyImmediate(_optionsRoot.GetChild(0).gameObject);
            }
            UIComponentFactory.CreateEventOption(
                _optionTemplate,
                _optionsRoot,
                new EventOptionViewData("继续", true, string.Empty), // 2026-08-23：双文本——仅标题无描述
                Complete);
            RefreshLayout(); // 2026-08-23 时序修复：按钮就位后再刷新布局
        }

        System.Collections.IEnumerator ShowContinueWhenReady()
        {
            int guard = 0;
            while (_optionTemplate == null && guard++ < 300) yield return null; // 防死等（大审查 H2）
            if (_optionTemplate == null)
            {
                Debug.LogWarning("[EventPanel] 选项模板加载超时——直接推进");
                Complete();
                yield break;
            }
            ShowContinue();
        }

/// <summary>事件交互完成：先关自己再通知 TowerFlow 推进（防同步推进重新激活面板后被 SetActive(false) 关闭——时序反转）。
        /// ⚠️ 2026-08-12：携带当前事件 id——TowerFlow 校验匹配才推进（防重复信号跳节点）。</summary>
        void Complete()
        {
            gameObject.SetActive(false);
            EventCenter.Instance.EventTrigger(GameEvent.EventCompleted, _currentEventId);
        }

        void Exit()
        {
            // 退出事件关（2026-08-23）：转 Bootstrap 确认弹窗（确认=保存进度并返回主菜单；取消=无改动）——不再直接推进
            OnExitClicked?.Invoke();
        }
    }
}
