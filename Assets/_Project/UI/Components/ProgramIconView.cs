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
            if (_iconImage != null)
            {
                // 有图标只显图标；无图标才用文字（2026-08-26 图标接入防重叠；null 清残留）
                _iconImage.sprite = data != null ? data.IconSprite : null;
                if (data != null && data.IconColor.HasValue) _iconImage.color = data.IconColor.Value;
            }
            if (_typeText != null)
                _typeText.text = visible && (data == null || data.IconSprite == null) ? data?.TypeLabel ?? string.Empty : string.Empty;
        }

        private void CacheNodes()
        {
            if (_iconImage == null) _iconImage = GetComponent<Image>();
            if (_typeText == null) _typeText = GetComponentInChildren<TMP_Text>(true);
        }
    }
}
