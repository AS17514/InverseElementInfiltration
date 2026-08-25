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
        private Sprite _bgSprite; // 程序槽默认背景（Bg.png——2026-08-26）

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
                // 有模块图标显图标；无图标（效果/空槽）显默认背景 Bg——2026-08-26 槽位背景
                if (data != null && data.IconSprite != null) _iconImage.sprite = data.IconSprite;
                else if (_bgSprite != null) _iconImage.sprite = _bgSprite;
                if (data != null && data.IconColor.HasValue) _iconImage.color = data.IconColor.Value;
            }
            if (_typeText != null)
                _typeText.text = visible && (data == null || data.IconSprite == null) ? data?.TypeLabel ?? string.Empty : string.Empty;
        }

        private void CacheNodes()
        {
            if (_iconImage == null) _iconImage = GetComponent<Image>();
            if (_typeText == null) _typeText = GetComponentInChildren<TMP_Text>(true);
            if (_bgSprite == null && IconLibrary.TryGet("Bg", out var bg)) _bgSprite = bg;
        }
    }
}
