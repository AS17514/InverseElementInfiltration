using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>Piece_Handcard 的纯展示绑定。</summary>
    public sealed class HandCardView : MonoBehaviour
    {
        private Image _background;
        private Image _nameBackground;
        private Image _portraitBackground;
        private Image _portrait;
        private Sprite _fallbackPortrait;
        private TMP_Text _nameText;
        private TMP_Text _valueText;
        private TMP_Text _typeText;
        private readonly Transform[] _slotIcons = new Transform[4];
        private readonly Image[] _slotIconImages = new Image[4];
        private readonly TMP_Text[] _slotIconTexts = new TMP_Text[4];
        private readonly Transform[] _slotDescriptions = new Transform[4];
        private readonly TMP_Text[] _slotDescriptionTexts = new TMP_Text[4];
        private const float EmptyProgramSlotAlpha = 0.2f;

        private void Awake()
        {
            CacheNodes();
        }

        public void Bind(HandCardViewData data)
        {
            CacheNodes();
            var color = data != null ? data.BackgroundColor : Color.white;
            ApplyChromeColor(_background, color);
            ApplyChromeColor(_nameBackground, color);
            ApplyChromeColor(_portraitBackground, color);
            if (_portrait != null)
            {
                _portrait.color = Color.white;
                _portrait.sprite = data != null
                    && PieceViewFactory.TryGetPreloadedPortrait(data.PortraitKey, out var portrait)
                    ? portrait
                    : _fallbackPortrait;
            }
            if (_nameText != null) _nameText.text = data?.VerticalName ?? string.Empty;
            if (_valueText != null) _valueText.text = data?.ValueText ?? string.Empty;
            if (_typeText != null) _typeText.text = data?.TypeLabel ?? string.Empty;

            for (var i = 0; i < 4; i++)
            {
                var slot = data != null && i < data.ProgramSlots.Count ? data.ProgramSlots[i] : null;
                var visible = slot != null && slot.Visible;
                if (_slotIcons[i] != null)
                {
                    _slotIcons[i].gameObject.SetActive(true);
                    if (_slotIconImages[i] != null)
                    {
                        if (visible && slot.IconSprite != null) _slotIconImages[i].sprite = slot.IconSprite;
                        var slotColor = visible && slot.IconColor.HasValue ? slot.IconColor.Value : Color.white;
                        _slotIconImages[i].color = new Color(slotColor.r, slotColor.g, slotColor.b, visible ? 1f : EmptyProgramSlotAlpha);
                    }
                    if (_slotIconTexts[i] != null) _slotIconTexts[i].text = visible ? slot.TypeLabel : string.Empty;
                }
                if (_slotDescriptions[i] != null)
                {
                    _slotDescriptions[i].gameObject.SetActive(true);
                    if (_slotDescriptionTexts[i] != null)
                    {
                        _slotDescriptionTexts[i].text = visible ? slot.Description : string.Empty;
                        var descriptionColor = _slotDescriptionTexts[i].color;
                        _slotDescriptionTexts[i].color = new Color(descriptionColor.r, descriptionColor.g, descriptionColor.b, visible ? 1f : EmptyProgramSlotAlpha);
                    }
                }
            }
        }

        private void CacheNodes()
        {
            if (_background == null) _background = GetComponent<Image>();
            if (_nameBackground == null) _nameBackground = FindNode("Img_InfoNameBg")?.GetComponent<Image>();
            if (_portraitBackground == null) _portraitBackground = FindNode("Grp_InfoPortrait")?.GetComponent<Image>();
            if (_portrait == null) _portrait = FindNode("Img_InfoPortrait")?.GetComponent<Image>();
            if (_fallbackPortrait == null && _portrait != null) _fallbackPortrait = _portrait.sprite;
            if (_nameText == null) _nameText = FindNode("Txt_InfoName")?.GetComponent<TMP_Text>();
            if (_valueText == null) _valueText = FindText("Img_InfoValue");
            if (_typeText == null) _typeText = FindText("Img_InfoType");

            for (var i = 0; i < 4; i++)
            {
                if (_slotIcons[i] == null)
                {
                    _slotIcons[i] = FindNode($"Img_InfoProgram{i + 1}");
                    _slotIconImages[i] = _slotIcons[i] != null ? _slotIcons[i].GetComponent<Image>() : null;
                    _slotIconTexts[i] = _slotIcons[i] != null ? _slotIcons[i].GetComponentInChildren<TMP_Text>(true) : null;
                }
                if (_slotDescriptions[i] == null)
                {
                    _slotDescriptions[i] = FindNode($"Txt_InfoProgram{i + 1}Desc");
                    _slotDescriptionTexts[i] = _slotDescriptions[i] != null
                        ? _slotDescriptions[i].GetComponent<TMP_Text>() : null;
                }
            }
        }

        private static void ApplyChromeColor(Image image, Color color)
        {
            if (image == null) return;
            var existing = image.color;
            image.color = new Color(color.r, color.g, color.b, existing.a);
        }

        private Transform FindNode(string nodeName)
        {
            foreach (var transform in GetComponentsInChildren<Transform>(true))
            {
                if (transform.name == nodeName || transform.name.StartsWith(nodeName)) return transform;
            }
            return null;
        }

        private TMP_Text FindText(string nodeName)
        {
            var node = FindNode(nodeName);
            return node != null ? node.GetComponentInChildren<TMP_Text>(true) : null;
        }
    }
}
