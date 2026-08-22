using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>Btn_EventOption 的纯展示与点击转发绑定。</summary>
    public sealed class EventOptionView : MonoBehaviour
    {
        private Button _button;
        private TMP_Text _label;
        private UnityAction _boundClick;

        private void Awake()
        {
            CacheNodes();
        }

        public void Bind(EventOptionViewData data, Action onClick)
        {
            CacheNodes();
            if (_boundClick != null && _button != null) _button.onClick.RemoveListener(_boundClick);
            _boundClick = null;

            var interactable = data != null && data.Interactable;
            if (_label != null) _label.text = data?.Label ?? string.Empty;
            if (_button == null) return;

            _button.interactable = interactable;
            if (!interactable || onClick == null) return;
            _boundClick = () => onClick();
            _button.onClick.AddListener(_boundClick);
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
