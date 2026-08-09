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
            _title = transform.Find("Txt_EventName")?.GetComponent<TMP_Text>();
            _desc = transform.Find("Txt_EventDesc")?.GetComponent<TMP_Text>();
            _optionsRoot = transform.Find("Grp_EventOptions");
            _exitBtn = transform.Find("Btn_Exit")?.GetComponent<Button>();
            if (_exitBtn != null)
            {
                _exitBtn.onClick.RemoveAllListeners();
                _exitBtn.onClick.AddListener(() => Exit());
            }
            EventCenter.Instance.AddEventListener(GameEvent.EventOpened, OnEventOpened);
        }

        void OnDestroy()
        {
            EventCenter.Instance.RemoveEventListener(GameEvent.EventOpened, OnEventOpened);
        }

        void OnEventOpened(object data)
        {
            if (!(data is string eventId)) return;
            _currentEventId = eventId;
            _currentEvent = ConfigTable.FindByName<EventDefinition>(eventId);
            if (_currentEvent == null)
            {
                Debug.LogWarning($"[EventPanel] 事件定义未找到：{eventId}");
                Complete(); // 找不到定义直接推进（防卡关）
                return;
            }
            if (_title != null) _title.text = _currentEvent.name.Replace("Event_", ""); // 资产名兜底
            if (_desc != null) _desc.text = Describe(_currentEvent);
            BuildOptions();
            gameObject.SetActive(true);
        }

        /// <summary>描述：优先资产内字段（无描述字段时用资产名——测试数据描述在 JSON 未导入描述字段时的兜底）。</summary>
        static string Describe(EventDefinition ev)
        {
            // EventDefinition 无 description 字段——测试数据 JSON 有但未导入；先用名称
            return ev.name;
        }

        void BuildOptions()
        {
            if (_optionsRoot == null || _currentEvent == null) return;
            foreach (Transform child in _optionsRoot) Destroy(child.gameObject);
            for (int i = 0; i < _currentEvent.options.Count; i++)
            {
                var option = _currentEvent.options[i];
                var go = new GameObject($"Option_{i}", typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(_optionsRoot, false);
                var rt = (RectTransform)go.transform;
                rt.sizeDelta = new Vector2(600, 60);
                go.GetComponent<Image>().color = new Color(0.25f, 0.4f, 0.65f, 1f);
                var btn = go.GetComponent<Button>();
                btn.targetGraphic = go.GetComponent<Image>();

                var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                labelGo.transform.SetParent(go.transform, false);
                var label = labelGo.GetComponent<TextMeshProUGUI>();
                label.text = option.label;
                label.fontSize = 26;
                label.alignment = TextAlignmentOptions.Left;
                ((RectTransform)labelGo.transform).sizeDelta = new Vector2(560, 50);
                ((RectTransform)labelGo.transform).anchoredPosition = Vector2.zero;

                if (!option.available)
                {
                    btn.interactable = false; // 灰显
                    go.GetComponent<Image>().color = new Color(0.4f, 0.4f, 0.4f, 1f);
                }
                else
                {
                    int index = i;
                    btn.onClick.AddListener(() => OnOptionClicked(index));
                }
            }
        }

        void OnOptionClicked(int optionIndex)
        {
            if (_eventNode == null || _currentEventId == null) return;
            _eventNode.OnOptionSelected(_currentEventId, optionIndex); // 规则层校验 + 效果落账
            // 一版：无交互效果（遗物/婉拒）→ 直接完成推进；edit/deck 专用界面后续细化
            Complete();
        }

        /// <summary>事件交互完成：隐藏面板 + 通知 TowerFlow 推进下一节点。</summary>
        void Complete()
        {
            EventCenter.Instance.EventTrigger(GameEvent.EventCompleted);
            gameObject.SetActive(false);
        }

        void Exit()
        {
            // 退出事件关（紧急逃生）——直接推进
            Complete();
        }
    }
}
