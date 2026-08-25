using System.Collections.Generic;
using UnityEngine;

namespace TheLaw.UI
{
    public sealed class ProgramSlotViewData
    {
        public string TypeLabel { get; }
        public string Description { get; }
        public bool Visible { get; }
        public Sprite IconSprite { get; }
        public Color? IconColor { get; }

        public ProgramSlotViewData(string typeLabel, string description, bool visible = true,
            Sprite iconSprite = null, Color? iconColor = null)
        {
            TypeLabel = typeLabel ?? string.Empty;
            Description = description ?? string.Empty;
            Visible = visible;
            IconSprite = iconSprite;
            IconColor = iconColor;
        }
    }

    public sealed class ProgramIconViewData
    {
        public string TypeLabel { get; }
        public bool Visible { get; }
        public Sprite IconSprite { get; }
        public Color? IconColor { get; }

        public ProgramIconViewData(string typeLabel, bool visible = true, Sprite iconSprite = null, Color? iconColor = null)
        {
            TypeLabel = typeLabel ?? string.Empty;
            Visible = visible;
            IconSprite = iconSprite;
            IconColor = iconColor;
        }
    }

    public sealed class ProgramCardViewData
    {
        public string TypeLabel { get; }
        public string ValueText { get; }
        public string Description { get; }
        public Sprite IconSprite { get; }

        public ProgramCardViewData(string typeLabel, string valueText, string description, Sprite iconSprite = null)
        {
            TypeLabel = typeLabel ?? string.Empty;
            ValueText = valueText ?? string.Empty;
            Description = description ?? string.Empty;
            IconSprite = iconSprite;
        }
    }

    public class PieceCardViewData
    {
        public Color BackgroundColor { get; }
        public string ValueText { get; }
        public string TypeLabel { get; }
        public string PortraitKey { get; }
        public IReadOnlyList<ProgramIconViewData> ProgramIcons { get; }

        public PieceCardViewData(Color backgroundColor, string valueText, string typeLabel, string portraitKey,
            IReadOnlyList<ProgramIconViewData> programIcons)
        {
            BackgroundColor = backgroundColor;
            ValueText = valueText ?? string.Empty;
            TypeLabel = typeLabel ?? string.Empty;
            PortraitKey = portraitKey ?? string.Empty;
            ProgramIcons = programIcons ?? new List<ProgramIconViewData>();
        }
    }

    public sealed class HandCardViewData : PieceCardViewData
    {
        public new string PortraitKey { get; }
        public string VerticalName { get; }
        public IReadOnlyList<ProgramSlotViewData> ProgramSlots { get; }

        public HandCardViewData(Color backgroundColor, string portraitKey, string verticalName, string valueText,
            string typeLabel, IReadOnlyList<ProgramSlotViewData> programSlots)
            : base(backgroundColor, valueText, typeLabel, portraitKey, ToIcons(programSlots))
        {
            PortraitKey = portraitKey ?? string.Empty;
            VerticalName = verticalName ?? string.Empty;
            ProgramSlots = programSlots ?? new List<ProgramSlotViewData>();
        }

        private static IReadOnlyList<ProgramIconViewData> ToIcons(IReadOnlyList<ProgramSlotViewData> slots)
        {
            var icons = new List<ProgramIconViewData>();
            if (slots == null) return icons;
            foreach (var slot in slots)
            {
                icons.Add(new ProgramIconViewData(
                    slot?.TypeLabel,
                    slot != null && slot.Visible,
                    slot?.IconSprite,
                    slot?.IconColor));
            }
            return icons;
        }
    }

    public sealed class EventOptionViewData
    {
        /// <summary>选项标题（Txt_OptionTitle——displayName/名称）。</summary>
        public string Title { get; }
        /// <summary>选项描述（Txt_Content——description；2026-08-23 新增双文本结构）。</summary>
        public string Content { get; }
        public bool Interactable { get; }

        /// <summary>兼容旧语义：Label = 标题（新结构请用 Title/Content）。</summary>
        public string Label => Title;

        public EventOptionViewData(string title, bool interactable, string content = "")
        {
            Title = title ?? string.Empty;
            Content = content ?? string.Empty;
            Interactable = interactable;
        }
    }

    public sealed class ConfirmViewData
    {
        public string Message { get; }

        public ConfirmViewData(string message)
        {
            Message = message ?? string.Empty;
        }
    }

    public sealed class ItemGettingViewData
    {
        public string Name { get; }
        public string Description { get; }
        public Color IconColor { get; }
        public bool ShowIcon { get; }

        public ItemGettingViewData(string name, string description, Color iconColor, bool showIcon = true)
        {
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
            IconColor = iconColor;
            ShowIcon = showIcon;
        }
    }

    public sealed class BattleResultViewData
    {
        public string ResultText { get; }
        public Color ResultColor { get; }
        public string StatsText { get; }
        public string TipText { get; }

        public BattleResultViewData(string resultText, Color resultColor, string statsText, string tipText)
        {
            ResultText = resultText ?? string.Empty;
            ResultColor = resultColor;
            StatsText = statsText ?? string.Empty;
            TipText = tipText ?? string.Empty;
        }
    }

    public sealed class TooltipViewData
    {
        public string Text { get; }

        public TooltipViewData(string text)
        {
            Text = text ?? string.Empty;
        }
    }
}
