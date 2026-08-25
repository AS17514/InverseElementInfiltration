using System.Collections.Generic;
using TheLaw.Data;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>Piece_Card 的纯展示绑定。程序图标由 Factory/调用方创建后统一写入。</summary>
    public sealed class PieceCardView : MonoBehaviour
    {
        private Image _typeBackground;
        private TMP_Text _valueText;
        private TMP_Text _typeText;
        private Image _typeIcon;       // Img_PieceType（类型位：默认 None 图标——2026-08-27）
        private Image _footprintImage; // Img_PieceFootprint（占地标识）
        private Image _portrait;
        private Sprite _fallbackPortrait;
        private Transform _programRoot;

        private void Awake()
        {
            CacheNodes();
        }

        public void BindBase(PieceCardViewData data)
        {
            CacheNodes();
            if (_typeBackground != null) _typeBackground.color = data != null ? data.BackgroundColor : Color.white;
            if (_portrait != null)
            {
                _portrait.color = Color.white;
                _portrait.sprite = data != null
                    && PieceViewFactory.TryGetPreloadedPortrait(data.PortraitKey, out var portrait)
                    ? portrait
                    : _fallbackPortrait;
            }
            if (_valueText != null) _valueText.text = data?.ValueText ?? string.Empty;
            // 类型位：默认 None 图标（卡色已表达种类；Element 不在构筑卡——2026-08-27）
            if (_typeIcon != null && IconLibrary.TryGet("Info_None", out var none))
            {
                _typeIcon.sprite = none;
                _typeIcon.color = Color.white;
            }
            if (_typeText != null) _typeText.text = string.Empty;
            // 占地标识：1x1/1x2；1x3 无图标不兜底
            if (_footprintImage != null)
            {
                var fp = data != null ? data.Footprint : Footprint.Size1x1;
                Sprite f = null;
                if (fp == Footprint.Size1x2) IconLibrary.TryGet("InfoFootprint_1x2", out f);
                else if (fp == Footprint.Size1x1) IconLibrary.TryGet("InfoFootprint_1x1", out f);
                _footprintImage.sprite = f;
                _footprintImage.gameObject.SetActive(f != null);
            }
        }

        public void BindProgramIcons(IReadOnlyList<ProgramIconViewData> icons, GameObject iconTemplate)
        {
            CacheNodes();
            if (_programRoot == null) return;
            var count = icons != null ? icons.Count : 0;
            while (_programRoot.childCount < count && iconTemplate != null)
            {
                UIComponentFactory.CreateProgramIcon(iconTemplate, _programRoot, null);
            }

            for (var i = 0; i < _programRoot.childCount; i++)
            {
                var icon = _programRoot.GetChild(i).GetComponent<ProgramIconView>();
                if (icon == null) icon = _programRoot.GetChild(i).gameObject.AddComponent<ProgramIconView>();
                icon.Bind(i < count ? icons[i] : null);
            }
        }

        public void Bind(PieceCardViewData data, GameObject iconTemplate)
        {
            BindBase(data);
            BindProgramIcons(data?.ProgramIcons, iconTemplate);
        }

        private void CacheNodes()
        {
            if (_typeBackground == null) _typeBackground = FindNode("Image")?.GetComponent<Image>();
            if (_valueText == null) _valueText = FindText("Img_PieceValue");
            if (_typeText == null) _typeText = FindText("Img_PieceType");
            if (_typeIcon == null)
            {
                var typeNode = FindNode("Img_PieceType");
                _typeIcon = typeNode != null ? typeNode.GetComponent<Image>() : null;
            }
            if (_footprintImage == null)
            {
                var fpNode = FindNode("Img_PieceFootprint");
                _footprintImage = fpNode != null ? fpNode.GetComponent<Image>() : null;
            }
            if (_portrait == null) _portrait = FindNode("Img_PiecePortrait")?.GetComponent<Image>();
            if (_fallbackPortrait == null && _portrait != null) _fallbackPortrait = _portrait.sprite;
            if (_programRoot == null) _programRoot = FindNode("Grp_PieceProgramInfo");
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
            var node = FindNode(nodeName);
            return node != null ? node.GetComponentInChildren<TMP_Text>(true) : null;
        }
    }
}
