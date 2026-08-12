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
            // ⚠️ 2026-08-12：跨局残留修复——新局首个事件 id 恒 "event-0-0"，与上局最后事件相同时
            // 幂等早退（ShowEvent 提前 return）→ 首事件沿用上局内容。整局结束（RunEnded）清 _currentEventId。
            EventCenter.Instance.AddEventListener(GameEvent.RunEnded, OnRunEnded);
            // 2026-08-12：遗物获得提示（大审查 B5 漏接修复——首事件必得遗物，玩家需看到"获得遗物 XX"）
            EventCenter.Instance.AddEventListener(GameEvent.RelicObtained, OnRelicObtained);
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
            EventCenter.Instance.RemoveEventListener(GameEvent.RunEnded, OnRunEnded);
            EventCenter.Instance.RemoveEventListener(GameEvent.RelicObtained, OnRelicObtained);
        }

        void OnRunEnded(object data)
        {
            _currentEventId = null; // 跨局重置（防新局首事件幂等早退）
            _relicPending = false;
        }

        bool _relicPending; // 本次选项获得遗物（描述区追加提示 + 延迟关闭展示）

        void OnRelicObtained(object data)
        {
            if (data is RelicDef relic && _desc != null && gameObject.activeSelf)
            {
                _relicPending = true;
                _desc.text += $"\n\n✨ 获得遗物：{relic.displayName}";
            }
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
            _relicPending = false; // 新事件重置遗物提示标记
            _currentEvent = ConfigTable.FindByName<EventDefinition>(eventId);
            if (_currentEvent == null)
            {
                Debug.LogWarning($"[EventPanel] 事件定义未找到：{eventId}");
                Complete(); // 找不到定义直接推进（防卡关）
                return;
            }
            if (_title != null) _title.text = string.IsNullOrEmpty(_currentEvent.title) ? "未知事件" : _currentEvent.title; // 中文兜底（防资产名泄漏）
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
            int guard = 0;
            while (_optionTemplate == null && guard++ < 300) yield return null; // 防死等（大审查 H2）
            if (_optionTemplate == null)
            {
                Debug.LogWarning("[EventPanel] 选项模板加载超时——跳过本次构建");
                yield break;
            }
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
            else if (_relicPending)
            {
                // 获得遗物：描述区已追加提示——延迟关闭让玩家看到（大审查 B5）
                _relicPending = false;
                StartCoroutine(DelayedComplete());
            }
            else
            {
                Complete(); // 无交互效果（遗物/婉拒）→ 直接推进
            }
        }

        System.Collections.IEnumerator DelayedComplete()
        {
            yield return new WaitForSeconds(0.9f); // 遗物提示展示时间
            Complete();
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
            // 退出事件关（紧急逃生）——直接推进
            Complete();
        }
    }
}
