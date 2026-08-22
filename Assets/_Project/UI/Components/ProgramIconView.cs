using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>Piece_ProgramInfo 的纯展示绑定。</summary>
    public sealed class ProgramIconView : MonoBehaviour
    {
        private Image _iconImage;
        private TMP_Text _typeText;

        private void Awake()
        {
            CacheNodes();
        }

        public void Bind(ProgramIconViewData data)
        {
            CacheNodes();
            var visible = data != null && data.Visible;
            if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
            if (_iconImage != null && data != null)
            {
                if (data.IconSprite != null) _iconImage.sprite = data.IconSprite;
                if (data.IconColor.HasValue) _iconImage.color = data.IconColor.Value;
            }
            if (_typeText != null) _typeText.text = visible ? data.TypeLabel : string.Empty;
        }

        private void CacheNodes()
        {
            if (_iconImage == null) _iconImage = GetComponent<Image>();
            if (_typeText == null) _typeText = GetComponentInChildren<TMP_Text>(true);
        }
    }
}
