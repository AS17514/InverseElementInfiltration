using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 撤销按钮输入处理器（编辑面板 Btn_Undo）：
    /// 单击（&lt;0.5s）→ OnClick；长按（≥0.5s）→ OnLongPress（松手不再触发单击——防双触发）；
    /// 悬停进/出 → OnHoverEnter/OnHoverExit（提示浮窗）。
    /// 用 unscaledTime（防 timeScale 暂停影响计时）。
    /// </summary>
    public class UndoButtonHandler : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public const float LongPressThreshold = 0.5f;

        public event Action OnClick;
        public event Action OnLongPress;
        public event Action OnHoverEnter;
        public event Action OnHoverExit;

        private float _pressTime;
        private bool _pressing;
        private bool _longFired;

        /// <summary>
        /// 按钮是否可交互。未激活（interactable=false）时不响应按下/悬停/长按，
        /// 避免置灰后长按仍触发 OnLongPress（2026-08-16 修复：编辑面板 Btn_Undo 未激活时长按仍弹全部撤回）。
        /// </summary>
        bool IsInteractable()
        {
            var btn = GetComponent<Button>();
            return btn == null || btn.interactable;
        }

        void Update()
        {
            if (_pressing && !_longFired && Time.unscaledTime - _pressTime >= LongPressThreshold)
            {
                _longFired = true;
                OnLongPress?.Invoke();
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!IsInteractable()) return;
            _pressing = true;
            _longFired = false;
            _pressTime = Time.unscaledTime;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (_pressing && !_longFired && IsInteractable() && Time.unscaledTime - _pressTime < LongPressThreshold)
            {
                OnClick?.Invoke(); // 单击（未达长按阈值且未长按触发）
            }
            _pressing = false;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!IsInteractable()) return;
            OnHoverEnter?.Invoke();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            OnHoverExit?.Invoke();
        }
    }
}
