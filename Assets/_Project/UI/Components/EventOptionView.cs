using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// Btn_EventOption 的纯展示与点击转发绑定。
    /// 2026-08-23 扩展：可选长按回调（onLongPress）——长按触发后吞掉本次抬起点击（防误选）；
    /// 普通事件不传 onLongPress，行为与原先完全一致。
    /// </summary>
    public sealed class EventOptionView : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        private const float LongPressSeconds = 0.6f; // 长按判定时长

        private Button _button;
        private TMP_Text _title;    // Txt_OptionTitle（displayName/名称）
        private TMP_Text _content;  // Txt_Content（description）
        private UnityAction _boundClick;

        private Action _onLongPress;   // 长按回调（null = 普通选项，无长按语义）
        private float _pressStart;     // 按下时刻（unscaledTime——暂停/UI 时间缩放不影响判定）
        private bool _pressing;        // 指针按住中
        private bool _longPressFired;  // 本次按住已触发过长按（只触发一次）
        private bool _suppressClick;   // 长按触发后抬起不再触发点击选择

        private void Awake()
        {
            CacheNodes();
        }

        public void Bind(EventOptionViewData data, Action onClick, Action onLongPress = null)
        {
            CacheNodes();
            if (_boundClick != null && _button != null) _button.onClick.RemoveListener(_boundClick);
            _boundClick = null;

            _onLongPress = onLongPress;
            _pressing = false;
            _longPressFired = false;
            _suppressClick = false;

            var interactable = data != null && data.Interactable;
            if (_title != null) _title.text = data?.Title ?? string.Empty;
            if (_content != null) _content.text = data?.Content ?? string.Empty;
            ApplyHeightByContent(data != null ? data.Content : string.Empty); // 2026-08-23：按描述行数定高（90/120）
            if (_button == null) return;

            _button.interactable = interactable;
            if (!interactable || onClick == null) return;
            _boundClick = () =>
            {
                if (_suppressClick)
                {
                    _suppressClick = false; // 长按已触发刷新——吞掉本次抬起点击（防误选）
                    return;
                }
                onClick();
            };
            _button.onClick.AddListener(_boundClick);
        }

        private void Update()
        {
            if (!_pressing || _onLongPress == null || _longPressFired) return;
            if (Time.unscaledTime - _pressStart < LongPressSeconds) return;
            _longPressFired = true;
            _suppressClick = true; // 长按触发 → 本次抬起不再当点击
            var handler = _onLongPress;
            _onLongPress = null;   // 只触发一次（刷新后选项区整体重建，防止残留重复触发）
            handler?.Invoke();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_onLongPress == null) return;
            _pressing = true;
            _longPressFired = false;
            _suppressClick = false;
            _pressStart = Time.unscaledTime;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _pressing = false;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _pressing = false; // 移出按钮取消长按计时（不触发）
        }

        private void OnDestroy()
        {
            if (_boundClick != null && _button != null) _button.onClick.RemoveListener(_boundClick);
        }

        private void CacheNodes()
        {
            if (_button == null) _button = GetComponent<Button>();
            if (_button == null) _button = gameObject.AddComponent<Button>();
            if (_title == null) _title = FindText("Txt_OptionTitle");
            if (_content == null) _content = FindText("Txt_Content");
        }

        TMP_Text FindText(string nodeName)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == nodeName) return t.GetComponent<TMP_Text>();
            }
            return null;
        }

        // ====== 高度自适应（2026-08-23：按描述文本行数——1 行 90 / ≥2 行 120）======

        const float OptionHeightOneLine = 90f;
        const float OptionHeightTwoLines = 120f;
        const float CapacityUnitsPerLine = 24f; // 每行容量 = 24 个全角（半角按 0.5 计）

        void ApplyHeightByContent(string content)
        {
            var rt = transform as RectTransform;
            if (rt == null) return;
            rt.sizeDelta = new Vector2(rt.sizeDelta.x,
                EstimateLines(content) >= 2 ? OptionHeightTwoLines : OptionHeightOneLine);
        }

        /// <summary>估算文本行数：全角=1 单位、半角=0.5 单位，每 24 单位换行；换行符直接换行。</summary>
        static int EstimateLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            int lines = 1;
            float units = 0f;
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    lines++;
                    units = 0f;
                    continue;
                }
                units += IsFullWidth(c) ? 1f : 0.5f;
                if (units > CapacityUnitsPerLine)
                {
                    lines++;
                    units = 0f;
                }
            }
            return lines;
        }

        /// <summary>东亚全角字符判定（CJK 统一表意 + 全角标点/假名/谚文等——宽度近似口径）。</summary>
        static bool IsFullWidth(char c)
        {
            return c >= 0x1100 && (c <= 0x115F || c == 0x2329 || c == 0x232A
                || (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F)
                || (c >= 0xAC00 && c <= 0xD7A3)
                || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0xFE30 && c <= 0xFE4F)
                || (c >= 0xFF00 && c <= 0xFF60)
                || (c >= 0xFFE0 && c <= 0xFFE6));
        }
    }
}
