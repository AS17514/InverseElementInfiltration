using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>Program_Card 的纯展示绑定。</summary>
    public sealed class ProgramCardView : MonoBehaviour
    {
        private Image _typeIcon;
        private TMP_Text _typeText;
        private TMP_Text _valueText;
        private TMP_Text _descriptionText;

        private void Awake()
        {
            CacheNodes();
        }

        public void Bind(ProgramCardViewData data)
        {
            CacheNodes();
            if (_typeIcon != null && data != null && data.IconSprite != null) _typeIcon.sprite = data.IconSprite;
            if (_typeText != null) _typeText.text = data?.TypeLabel ?? string.Empty;
            if (_valueText != null) _valueText.text = data?.ValueText ?? string.Empty;
            if (_descriptionText != null) _descriptionText.text = data?.Description ?? string.Empty;
        }

        private void CacheNodes()
        {
            if (_typeIcon == null)
            {
                var iconNode = FindNode("Img_ProgramType");
                _typeIcon = iconNode != null ? iconNode.GetComponent<Image>() : null;
            }
            if (_typeText == null) _typeText = FindText("Img_ProgramType");
            if (_valueText == null) _valueText = FindText("Txt_ProgramCount");
            if (_descriptionText == null) _descriptionText = FindText("Txt_ProgramDesc");
        }

        private Transform FindNode(string nodeName)
        {
            foreach (var transform in GetComponentsInChildren<Transform>(true))
            {
                if (transform.name == nodeName) return transform;
            }
            return null;
        }

        private TMP_Text FindText(string nodeName)
        {
            foreach (var transform in GetComponentsInChildren<Transform>(true))
            {
                if (transform.name == nodeName) return transform.GetComponentInChildren<TMP_Text>(true);
            }
            return null;
        }
    }
}
