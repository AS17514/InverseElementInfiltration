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
        private bool _isRulePick;              // 玩法事件模式（EventDefinition.isRulePick == true）
        private bool _ruleSelectionLocked;     // 玩法选择防重复点击锁（选择后同步推进——下一事件重建时复位）

        private Image _artImage;               // 事件 CG 位（Grp_EventContent/Img_EventArt——按事件类型切换图+位置/尺寸）
        private Sprite _defaultArt;            // prefab 默认 CG（无专属 CG 事件回退）
        private Vector2 _defaultArtPos, _defaultArtSize; // prefab 默认 CG 的 anchoredPosition/sizeDelta（恢复用）
        private Sprite _artAbility, _artEdit, _artMode; // 事件 CG（Addressables 懒加载缓存）
        private string _pendingArtKey;         // CG 未加载完时的待应用类型 key（加载完成后补应用）
        private Sprite _circleSmallEmpty, _circleSmallFilled, _circleBigEmpty, _circleBigFilled; // 进度节点素材（小/大 × 空/满）
        private int _pendingBuilds;            // 未完成的内容构建计数（选项区重建——普通/玩法/能力；内容就绪检查点）
        private bool _artPending;              // 事件 CG 未加载完成（待应用——内容就绪检查点等待其加载）

        // ====== 内容就绪检查点（2026-08-25：过渡 loading 等此信号再淡出——防止事件内容未加载完就揭盖）======
        void BeginContent() { _pendingBuilds++; IsContentReady = false; }
        void EndContent() { if (_pendingBuilds > 0) _pendingBuilds--; TryNotifyReady(); }
        void TryNotifyReady() { if (_pendingBuilds == 0 && !_artPending) NotifyContentReady(); }

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
            RefreshProgress(); // 2026-08-25：每次显示/重建顺带刷左侧进度节点（素材未就绪时跳过，加载完成后补刷）
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
            // 2026-08-25：Img_EventArt 已套 Grp_EventArt 遮罩容器——硬路径 + FindDeep 兜底（层级再变不失效）
            _artImage = (transform.Find("Grp_EventContent/Grp_EventArt/Img_EventArt") ?? FindDeep(transform, "Img_EventArt"))?.GetComponent<Image>();
            if (_artImage != null)
            {
                _defaultArt = _artImage.sprite; // 缓存 prefab 默认 CG（恢复用）
                var artRt = _artImage.rectTransform;
                if (artRt != null) { _defaultArtPos = artRt.anchoredPosition; _defaultArtSize = artRt.sizeDelta; }
            }
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
            // 玩法事件（2026-08-25）：不发 EventOpened——候选广播驱动二选一（无刷新）
            EventCenter.Instance.AddEventListener(GameEvent.RuleCandidatesDrawn, OnRuleCandidatesDrawn);
            // UI 架构重构 §六：跨局残留由"新实例"保证（局结束销毁面板）——不再需要 RunEnded 重置
            // 预加载选项按钮模板（Btn_EventOption）
            StartCoroutine(LoadOptionTemplate());
            // 预加载事件 CG（event_ability/event_edit/event_mode——Addressables 地址 = 文件名）
            StartCoroutine(LoadEventArtSprites());
            // 预加载进度节点素材（小/大 × 空/满——Addressables 地址 = 文件名）
            StartCoroutine(LoadProgressSprites());
        }

        /// <summary>按名深度查找（面板层级布局变化时兜底——与 BattleController/PieceEditPanel 同模式）。</summary>
        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
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
            btn.onClick.AddListener(() =>
            {
                UiSfx.Play(); // 事件面板设置按钮碰撞音（2026-08-24 音频挂点方案）
                OnSettingsClicked?.Invoke();
            });
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
            EventCenter.Instance.RemoveEventListener(GameEvent.RuleCandidatesDrawn, OnRuleCandidatesDrawn);
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
            _isRulePick = false;               // 新事件复位玩法锁
            _ruleSelectionLocked = false;
            if (_currentEvent.isAbilityPick)
            {
                // 能力事件兜底（正常能力事件不发 EventOpened——由 AbilityCandidatesDrawn 驱动；此分支防误入）
                EnterAbilityMode(_currentEventId, _currentEvent);
                return;
            }
            BeginContent(); // 内容就绪检查点：普通事件开始构建
            _isAbilityPick = false;
            if (_exitBtn != null) _exitBtn.interactable = true; // 普通事件恢复退出按钮（能力模式已禁用——不能直接完成绕过选择）
            // 2026-08-25：事件小文本覆盖（EventTexts——docx 来源；未登记事件回退定义原文本）
            if (_title != null) _title.text = EventTexts.TitleFor(_currentEventId, _currentEvent);
            if (_desc != null) _desc.text = EventTexts.DescFor(_currentEventId, _currentEvent);
            ApplyEventArt(ArtForEvent(_currentEvent)); // CG 按事件类型切换（编辑→event_edit；其他→默认）
            BuildOptions(); // 选项就位后内部刷新布局（时序正确——模板未就绪时由 BuildOptionsWhenReady 补刷）
            bool wasVisible = gameObject.activeSelf;
            gameObject.SetActive(true);
            // 事件面板"隐藏→显示"时播碰撞音（2026-08-24 音频挂点方案；已显示时换事件不重复播）
            if (!wasVisible) UiSfx.Play();
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
            EndContent(); // 内容就绪检查点：普通事件选项构建完成（同步路径）
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


        // ========== 事件 CG 切换（2026-08-25：Img_EventArt 按事件类型换图——能力/编辑/玩法）==========

        /// <summary>
        /// 事件 → CG 类型 key：玩法→rule、能力→ability、含构筑选项→deck、含编辑选项→edit；其他→null（默认图）。
        /// 构筑复用编辑图（event_edit）但位置/尺寸不同（2026-08-25 用户定案：4 事件 3 图）。
        /// </summary>
        static string ArtForEvent(EventDefinition ev)
        {
            if (ev == null) return null;
            if (ev.isRulePick) return "rule";
            if (ev.isAbilityPick) return "ability";
            if (ev.options != null)
            {
                bool hasEdit = false;
                foreach (var o in ev.options)
                {
                    if (o.effects == null) continue;
                    foreach (var e in o.effects)
                    {
                        if (e.effectType == EffectType.DeckBuild) return "deck";   // 构筑优先（编辑图大尺寸形态）
                        if (e.effectType == EffectType.EditProgram) hasEdit = true;
                    }
                }
                if (hasEdit) return "edit";
            }
            return null;
        }

        /// <summary>CG 配置：图地址 + anchoredPosition(x,y) + sizeDelta(w,h)——锚点居中（prefab 实测 anchorMin/Max=(0.5,0.5)）。</summary>
        private struct EventArtConfig
        {
            public string Address;
            public float X, Y, W, H;
        }

        static readonly Dictionary<string, EventArtConfig> ArtConfigs = new Dictionary<string, EventArtConfig>
        {
            ["ability"] = new EventArtConfig { Address = "event_ability", X = 67f,   Y = 0f,    W = 1226f, H = 868f },
            ["edit"]    = new EventArtConfig { Address = "event_edit",    X = 315f,  Y = 0f,    W = 1376f, H = 974f },
            ["rule"]    = new EventArtConfig { Address = "event_mode",    X = -38f,  Y = 0f,    W = 1228f, H = 868f },
            ["deck"]    = new EventArtConfig { Address = "event_edit",    X = -433f, Y = -187f, W = 1757f, H = 1242f },
        };

        /// <summary>应用事件 CG：换图 + 按类型设置位置/尺寸；未加载记 pending（加载完成协程补应用）；null = 恢复 prefab 默认图与默认位置。</summary>
        void ApplyEventArt(string key)
        {
            if (_artImage == null) return;
            var rt = _artImage.rectTransform;
            if (string.IsNullOrEmpty(key) || !ArtConfigs.TryGetValue(key, out var cfg))
            {
                _artImage.sprite = _defaultArt;
                if (rt != null) { rt.anchoredPosition = _defaultArtPos; rt.sizeDelta = _defaultArtSize; }
                _pendingArtKey = null;
                _artPending = false; // 默认 CG：无待应用
                TryNotifyReady();
                return;
            }
            _pendingArtKey = key;
            Sprite s = ResolveArtSprite(cfg.Address);
            if (s != null)
            {
                _artImage.sprite = s;
                if (rt != null)
                {
                    rt.anchoredPosition = new Vector2(cfg.X, cfg.Y);
                    rt.sizeDelta = new Vector2(cfg.W, cfg.H);
                }
                _pendingArtKey = null;
                _artPending = false; // CG 已应用（同步路径）
                TryNotifyReady();
            }
            else
            {
                _artPending = true; // CG 未加载完——内容就绪检查点等加载完成补应用
            }
        }

        Sprite ResolveArtSprite(string address)
        {
            return address == "event_ability" ? _artAbility
                : address == "event_edit" ? _artEdit
                : address == "event_mode" ? _artMode : null;
        }

        /// <summary>预加载 3 张事件 CG；完成后补应用 pending 地址（首发事件早于加载完成时）。</summary>
        System.Collections.IEnumerator LoadEventArtSprites()
        {
            var a = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>("event_ability");
            yield return a;
            if (a.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) _artAbility = a.Result;
            else Debug.LogWarning("[EventPanel] 事件 CG 加载失败：event_ability");

            var e = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>("event_edit");
            yield return e;
            if (e.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) _artEdit = e.Result;
            else Debug.LogWarning("[EventPanel] 事件 CG 加载失败：event_edit");

            var m = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>("event_mode");
            yield return m;
            if (m.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) _artMode = m.Result;
            else Debug.LogWarning("[EventPanel] 事件 CG 加载失败：event_mode");

            if (!string.IsNullOrEmpty(_pendingArtKey)) ApplyEventArt(_pendingArtKey);
        }

        /// <summary>预加载进度节点素材（event_circle_{small,big}_{empty,filled}）；就绪后按当前状态补刷。</summary>
        System.Collections.IEnumerator LoadProgressSprites()
        {
            var se = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>("event_circle_small_empty");
            yield return se;
            if (se.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) _circleSmallEmpty = se.Result;

            var sf = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>("event_circle_small_filled");
            yield return sf;
            if (sf.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) _circleSmallFilled = sf.Result;

            var be = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>("event_circle_big_empty");
            yield return be;
            if (be.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) _circleBigEmpty = be.Result;

            var bf = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>("event_circle_big_filled");
            yield return bf;
            if (bf.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) _circleBigFilled = bf.Result;

            RefreshProgress();
        }

        /// <summary>
        /// 左侧进度节点刷新（2026-08-25）：按 GameState 当前层 NodeStates 换图——Completed = 对应尺寸 filled、
        /// 其余 = 对应尺寸 empty（明暗由素材本身表达，不再颜色叠加）；非当前层节点不动。
        /// 节点命名 Img_Node_{层}_{序号}（1 起）；大节点（战斗关）判定 sizeDelta.y ≥ 50。
        /// </summary>
        void RefreshProgress()
        {
            if (_gameState == null) return;
            var states = _gameState.NodeStates;
            if (states == null || states.Count == 0) return;
            for (int i = 0; i < states.Count; i++)
            {
                var img = FindDeep(transform, $"Img_Node_{_gameState.CurrentFloor + 1}_{i + 1}")?.GetComponent<Image>();
                if (img == null) continue; // 节点缺失容错（命名不符/未摆放）
                bool isBig = img.rectTransform != null && img.rectTransform.sizeDelta.y >= 50f;
                Sprite target = states[i] == NodeState.Completed
                    ? (isBig ? _circleBigFilled : _circleSmallFilled)
                    : (isBig ? _circleBigEmpty : _circleSmallEmpty);
                if (target != null) img.sprite = target;
            }
        }

        // ========== 玩法事件二选一（2026-08-25：isRulePick 专用分支——复用事件面板/选项模板）==========

        /// <summary>玩法事件候选广播（面板已加载——二选一无刷新，重建整个选项区）。</summary>
        void OnRuleCandidatesDrawn(object data)
        {
            if (_gameState == null || _resolver == null) return; // 未 Init（首发由 Bootstrap 懒加载回填）
            var evId = _gameState.CurrentEventId;
            if (string.IsNullOrEmpty(evId)) return;
            var ev = ConfigTable.FindByName<EventDefinition>(evId);
            if (ev == null || !ev.isRulePick) return; // 非玩法事件（防御——广播只应由玩法事件发出）
            EnterRuleMode(evId, ev);
        }

        /// <summary>玩法模式主动回填（公开——Bootstrap 懒加载/读档路径：不能依赖历史事件广播）。成功返回 true。</summary>
        public bool ShowRuleEventFromState()
        {
            if (_gameState == null || _resolver == null) return false;
            var evId = _gameState.CurrentEventId;
            if (string.IsNullOrEmpty(evId)) return false;
            if (_gameState.RuleCandidates == null || _gameState.RuleCandidates.Count == 0) return false;
            var ev = ConfigTable.FindByName<EventDefinition>(evId);
            if (ev == null || !ev.isRulePick) return false;
            EnterRuleMode(evId, ev);
            return true;
        }

        /// <summary>进入玩法模式：标题/描述 → 重建二选一选项 → 显示（退出按钮置灰——不能"直接完成"绕过选择）。</summary>
        void EnterRuleMode(string eventId, EventDefinition ev)
        {
            _currentEventId = eventId;
            _currentEvent = ev;
            _isAbilityPick = false;
            _abilitySelectionLocked = false;
            _isRulePick = true;
            _ruleSelectionLocked = false;
            BeginContent(); // 内容就绪检查点：玩法事件开始构建
            if (_title != null) _title.text = string.IsNullOrEmpty(ev.title) ? "未知事件" : ev.title; // 中文兜底（防资产名泄漏）
            if (_desc != null) _desc.text = EventTexts.DescFor(_currentEventId, _currentEvent);
            if (_exitBtn != null) _exitBtn.interactable = false; // 玩法模式禁用退出（不能"直接完成"绕过玩法选择）
            ApplyEventArt(ArtForEvent(ev)); // 玩法事件 CG（event_mode）
            BuildRuleOptions(); // 候选就位后内部刷新布局（时序正确）
            bool wasVisible = gameObject.activeSelf;
            gameObject.SetActive(true);
            // 玩法事件二选一"隐藏→显示"时播碰撞音（2026-08-25；已显示时重建选项不重复播）
            if (!wasVisible) UiSfx.Play();
        }

        /// <summary>玩法选项区重建：每个候选 = 1 个玩法选项（玩法名；二选一不可刷新——不挂长按）。</summary>
        void BuildRuleOptions()
        {
            if (_optionsRoot == null || _currentEvent == null || _gameState == null) return;
            if (_optionTemplate == null)
            {
                StartCoroutine(BuildRuleOptionsWhenReady());
                return;
            }
            var candidates = _gameState.RuleCandidates;
            if (candidates == null || candidates.Count == 0) return; // 后端已清空（选择后）——不重建空区
            // 2026-08-23 时序修复：同步清空旧候选（DestroyImmediate）——延迟 Destroy 会与新建候选同帧并存
            while (_optionsRoot.childCount > 0)
            {
                DestroyImmediate(_optionsRoot.GetChild(0).gameObject);
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                var index = i;
                string name = DisplayNames.OfStyle(candidates[i]);
                UIComponentFactory.CreateEventOption(
                    _optionTemplate,
                    _optionsRoot,
                    new EventOptionViewData(name, true, string.Empty), // 双文本——仅标题无描述（玩法简述待文案源）
                    () => SelectRule(index));
            }
            RefreshLayout(); // 2026-08-23 时序修复：候选就位后再刷新布局（Grp_EventOptions 已准备好）
            EndContent(); // 内容就绪检查点：玩法候选构建完成（同步路径）
        }

        System.Collections.IEnumerator BuildRuleOptionsWhenReady()
        {
            int guard = 0;
            while (_optionTemplate == null && guard++ < 300) yield return null; // 防死等（同普通选项模板）
            if (_optionTemplate == null)
            {
                Debug.LogWarning("[EventPanel] 玩法选项模板加载超时——跳过本次构建");
                yield break;
            }
            BuildRuleOptions();
        }

        /// <summary>选择玩法候选：先上锁并隐藏面板（EventCompleted 同步推进——防残留/重复点击）→ Resolver 落账推进。</summary>
        void SelectRule(int index)
        {
            if (!_isRulePick || _ruleSelectionLocked || _resolver == null) return;
            _ruleSelectionLocked = true; // 防重复点击锁（同步推进后不释放——下一事件重建时复位）
            gameObject.SetActive(false);  // 先隐藏：避免同步推进（EventCompleted→下一事件再激活）造成残留或重复点击
            _resolver.SelectRule(index);  // 后端落账（激活玩法） + 清候选 + EventCompleted 推进（不走普通 Complete）
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
            _isRulePick = false;
            _ruleSelectionLocked = false;
            BeginContent(); // 内容就绪检查点：能力事件开始构建
            // 2026-08-25：事件小文本覆盖 + 刷新次数动态注入（docx 斜体 <i> 由 EventTexts 提供）
            if (_title != null) _title.text = EventTexts.TitleFor(eventId, ev);
            if (_desc != null) _desc.text = EventTexts.DescFor(eventId, ev, AbilityRefreshTotal());
            ApplyEventArt(ArtForEvent(ev)); // 能力事件 CG（event_ability）
            if (_exitBtn != null) _exitBtn.interactable = false; // 能力模式禁用退出（不能"直接完成"绕过能力选择）
            BuildAbilityOptions(); // 候选就位后内部刷新布局（时序正确）
            bool wasVisible = gameObject.activeSelf;
            gameObject.SetActive(true);
            // 能力事件三选一"隐藏→显示"时播碰撞音（2026-08-24 音频挂点方案；已显示时刷新候选不重复播）
            if (!wasVisible) UiSfx.Play();
        }

        /// <summary>能力候选剩余刷新总数（描述"刷新次数：N"动态注入——N = 全部候选剩余刷新之和）。</summary>
        int AbilityRefreshTotal()
        {
            if (_gameState == null || _gameState.AbilityRefreshLeft == null) return 0;
            int sum = 0;
            foreach (var n in _gameState.AbilityRefreshLeft) sum += n;
            return sum;
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
            EndContent(); // 内容就绪检查点：能力候选构建完成（同步路径）
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
            UiSfx.Play(); // 事件面板退出按钮碰撞音（2026-08-24 音频挂点方案）
            // 退出事件关（2026-08-23）：转 Bootstrap 确认弹窗（确认=保存进度并返回主菜单；取消=无改动）——不再直接推进
            OnExitClicked?.Invoke();
        }
    }
}
