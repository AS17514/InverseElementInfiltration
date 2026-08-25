using System.Collections.Generic;
using TheLaw.Data;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>集中将业务数据映射为纯展示数据。View 与 Factory 不读取业务状态。</summary>
    public static class PiecePresentationMapper
    {
        public static PieceCardViewData ToPieceCard(PieceDef def, PieceType effectiveType, int effectiveValue,
            IReadOnlyList<Template> program)
        {
            return new PieceCardViewData(
                CardTypeColors.For(effectiveType),
                effectiveValue.ToString(),
                PieceTypeLabel(effectiveType),
                def != null ? def.name : string.Empty,
                ToProgramIcons(program));
        }

        public static HandCardViewData ToHandCard(PieceDef def, PieceType effectiveType, int effectiveValue,
            IReadOnlyList<Template> program)
        {
            return new HandCardViewData(
                CardTypeColors.For(effectiveType),
                def != null ? def.name : string.Empty,
                ToVerticalName(def != null ? def.displayName : string.Empty),
                effectiveValue.ToString(),
                PieceTypeLabel(effectiveType),
                ToProgramSlots(program));
        }

        public static ProgramCardViewData ToProgramCard(Template template)
        {
            return new ProgramCardViewData(
                ProgramTypeLabel(template),
                PieceValue.GetValue(template).ToString(),
                ProgramDescription(template),
                ProgramIconSprite(template));
        }

        /// <summary>程序块图标键（Addressables 地址）。移动按 4 族、攻击按 5 方式；效果不画（2026-08-26 定案）。</summary>
        public static string ProgramIconKey(Template template)
        {
            switch (template)
            {
                case AttackTemplate atk:
                    switch (atk.mode)
                    {
                        case AttackMode.MeleeAOE: return "attack_melee_aoe";
                        case AttackMode.DirectFire: return "attack_direct";
                        case AttackMode.Arcing: return "attack_arcing";
                        case AttackMode.Spell: return "attack_spell";
                        default: return "attack_melee";
                    }
                case MoveTemplate mv:
                    if (mv.jumpOffsets != null && mv.jumpOffsets.Count > 0) return "move_jump";
                    switch (mv.id)
                    {
                        case 3: case 5: case 6: case 7: case 8: case 13: case 20: return "move_area";
                        case 14: case 19: return "move_combo";
                        case 15: case 16: case 17: case 18: return "move_jump";
                        default: return "move_step";
                    }
                default:
                    return null; // 效果/跳过：无图标（效果不画）
            }
        }

        public static Sprite ProgramIconSprite(Template template)
        {
            var key = ProgramIconKey(template);
            return key != null && IconLibrary.TryGet(key, out var sprite) ? sprite : null;
        }

        public static string PieceTypeLabel(PieceType type)
        {
            switch (type)
            {
                case PieceType.Initial: return "始";
                case PieceType.Deployable: return "部";
                default: return "升";
            }
        }

        public static string ProgramTypeLabel(Template template)
        {
            switch (template)
            {
                case MoveTemplate: return "移";
                case AttackTemplate: return "攻";
                case EffectTemplate: return "效";
                default: return "跳";
            }
        }

        public static string ProgramDescription(Template template)
        {
            if (template == null) return string.Empty;
            var mapped = SlotDescTable.Get(template);
            if (!string.IsNullOrEmpty(mapped)) return mapped;
            switch (template)
            {
                case MoveTemplate: return "移：移动";
                case AttackTemplate: return "攻：攻击";
                case EffectTemplate: return "效：被动效果";
                default: return "跳：跳过";
            }
        }

        public static string ToVerticalName(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : string.Join("\n", value.ToCharArray());
        }

        public static IReadOnlyList<ProgramIconViewData> ToProgramIcons(IReadOnlyList<Template> program)
        {
            var result = new List<ProgramIconViewData>();
            if (program == null) return result;
            var count = Mathf.Min(program.Count, 4);
            for (var i = 0; i < count; i++)
            {
                var template = program[i];
                result.Add(new ProgramIconViewData(ProgramTypeLabel(template), template != null, ProgramIconSprite(template)));
            }
            return result;
        }

        public static IReadOnlyList<ProgramSlotViewData> ToProgramSlots(IReadOnlyList<Template> program)
        {
            var result = new List<ProgramSlotViewData>();
            if (program == null) return result;
            var count = Mathf.Min(program.Count, 4);
            for (var i = 0; i < count; i++)
            {
                var template = program[i];
                result.Add(new ProgramSlotViewData(
                    ProgramTypeLabel(template),
                    ProgramDescription(template),
                    template != null,
                    ProgramIconSprite(template)));
            }
            return result;
        }
    }
}
