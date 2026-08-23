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
        private TMP_Text _label;
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
            if (_label != null) _label.text = data?.Label ?? string.Empty;
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
            if (_label == null) _label = GetComponentInChildren<TMP_Text>(true);
        }
    }
}
