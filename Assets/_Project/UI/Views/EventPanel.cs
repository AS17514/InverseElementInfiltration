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
    /// </summary>
    public class EventPanel : PanelBase
    {
        public override string Key => "EventPanel";

        private EventNodeSystem _eventNode;
        private EventDefinition _currentEvent;
        private string _currentEventId;
        private GameObject _optionTemplate; // Btn_EventOption prefab（Addressables 缓存）

        private TMP_Text _title;
        private TMP_Text _desc;
        private Transform _optionsRoot;
        private Button _exitBtn;

        public void Init(EventNodeSystem eventNode)
        {
            _eventNode = eventNode;
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
            EventCenter.Instance.AddEventListener(GameEvent.EventOpened, OnEventOpened);
            // 预加载选项按钮模板（Btn_EventOption）
            StartCoroutine(LoadOptionTemplate());
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
        }

        void OnEventOpened(object data)
        {
            if (data is string eventId)
            {
                ShowEvent(eventId);
            }
        }

        /// <summary>展示事件（公开——Bootstrap 懒加载完成后主动推数据，防首次事件丢失）。</summary>
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
            if (_title != null) _title.text = string.IsNullOrEmpty(_currentEvent.title) ? _currentEvent.name.Replace("Event_", "") : _currentEvent.title;
            if (_desc != null) _desc.text = Describe(_currentEvent);
            BuildOptions();
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
            foreach (Transform child in _optionsRoot) Destroy(child.gameObject);
            for (int i = 0; i < _currentEvent.options.Count; i++)
            {
                var option = _currentEvent.options[i];
                var go = Instantiate(_optionTemplate, _optionsRoot);
                var btn = go.GetComponent<Button>();
                if (btn == null) btn = go.AddComponent<Button>();
                var label = go.GetComponentInChildren<TMP_Text>();
                if (label != null) label.text = option.label;
                if (!option.available)
                {
                    // 灰显交给 Button.interactable=false（disabledColor 自动）——不改预制体视觉
                    btn.interactable = false;
                }
                else
                {
                    int index = i;
                    btn.onClick.AddListener(() => OnOptionClicked(index));
                }
            }
        }

        System.Collections.IEnumerator BuildOptionsWhenReady()
        {
            while (_optionTemplate == null) yield return null;
            BuildOptions();
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
            }
            else
            {
                Complete(); // 无交互效果（遗物/婉拒）→ 直接推进
            }
        }

        /// <summary>事件交互完成：先关自己再通知 TowerFlow 推进（防同步推进重新激活面板后被 SetActive(false) 关闭——时序反转）。</summary>
        void Complete()
        {
            gameObject.SetActive(false);
            EventCenter.Instance.EventTrigger(GameEvent.EventCompleted);
        }

        void Exit()
        {
            // 退出事件关（紧急逃生）——直接推进
            Complete();
        }
    }
}
