using TheLaw.Data;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 枚举 → 玩家可读中文显示名（集中映射——UI 直出枚举名 = 英文泄漏，统一走这里）。
    /// 所有映射 default 分支返回"未知" + 警告（防新增枚举值泄漏英文）。
    /// 注：方法与枚举类型同名在 C# 非法——方法统一 Of 前缀。
    /// </summary>
    public static class DisplayNames
    {
        // ====== 特殊能力类型 ======

        public static string OfAbilityType(SpecialAbilityType type)
        {
            switch (type)
            {
                case SpecialAbilityType.Passive: return "被动";
                case SpecialAbilityType.Trigger: return "触发";
                case SpecialAbilityType.Attach: return "附着";
                default:
                    Debug.LogWarning($"[DisplayNames] 未知特殊能力类型 {type}");
                    return "未知";
            }
        }

        public static string OfTriggerPoint(TriggerPoint point)
        {
            switch (point)
            {
                case TriggerPoint.OnBattleStart: return "战斗开始";
                case TriggerPoint.OnTurnStart: return "回合开始";
                case TriggerPoint.OnTurnEnd: return "回合结束";
                case TriggerPoint.OnKill: return "击杀";
                case TriggerPoint.OnPieceLanded: return "落子";
                case TriggerPoint.OnDamaged: return "受击";
                default:
                    Debug.LogWarning($"[DisplayNames] 未知触发点 {point}");
                    return "未知";
            }
        }

        public static string OfTriggerEffect(TriggerEffect effect)
        {
            switch (effect)
            {
                case TriggerEffect.ExtraAction: return "免费行动";
                case TriggerEffect.HealDurability: return "恢复承伤";
                case TriggerEffect.ShieldBlock: return "护盾";
                default:
                    Debug.LogWarning($"[DisplayNames] 未知触发效果 {effect}");
                    return "未知";
            }
        }

        public static string OfPassiveTarget(PassiveTarget target)
        {
            switch (target)
            {
                case PassiveTarget.MoveStep: return "移动步数";
                case PassiveTarget.AttackDamage: return "攻击伤害";
                case PassiveTarget.AttackRange: return "攻击射程";
                case PassiveTarget.Durability: return "承伤";
                default:
                    Debug.LogWarning($"[DisplayNames] 未知被动目标 {target}");
                    return "未知";
            }
        }

        public static string OfAttachPoint(AttachPoint point)
        {
            switch (point)
            {
                case AttachPoint.OnAttack: return "攻击时";
                case AttachPoint.OnMove: return "移动时";
                default:
                    Debug.LogWarning($"[DisplayNames] 未知附着点 {point}");
                    return "未知";
            }
        }

        // ====== 攻击模式 ======

        public static string OfAttackMode(AttackMode mode)
        {
            switch (mode)
            {
                case AttackMode.Melee: return "近战";
                case AttackMode.MeleeAOE: return "近战群攻";
                case AttackMode.DirectFire: return "直射";
                case AttackMode.Arcing: return "抛射";
                case AttackMode.Spell: return "法术";
                default:
                    Debug.LogWarning($"[DisplayNames] 未知攻击模式 {mode}");
                    return "未知";
            }
        }

        // ====== 方向 ======

        public static string OfDirection(Direction d)
        {
            switch (d)
            {
                case Direction.Up: return "上";
                case Direction.Down: return "下";
                case Direction.Left: return "左";
                case Direction.Right: return "右";
                case Direction.UpLeft: return "左上";
                case Direction.UpRight: return "右上";
                case Direction.DownLeft: return "左下";
                case Direction.DownRight: return "右下";
                default:
                    Debug.LogWarning($"[DisplayNames] 未知方向 {d}");
                    return "未知";
            }
        }

        // ====== 棋子类型 ======

        public static string OfPieceType(PieceType type)
        {
            switch (type)
            {
                case PieceType.Initial: return "初始";
                case PieceType.Deployable: return "部署";
                case PieceType.Promoted: return "升变";
                default:
                    Debug.LogWarning($"[DisplayNames] 未知棋子类型 {type}");
                    return "未知";
            }
        }
    }
}
