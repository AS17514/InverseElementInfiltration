using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>
    /// 特殊能力定义（数据层一等公民，SO 资产）——三类执行路径的统一载体。
    /// 消费者：棋子固有（Def.specialAbilities）/ 遗物（RelicDef）/ 关卡效果（GrantAbility）。
    /// </summary>
    public class SpecialAbilityDef : GameConfigBase
    {
        public SpecialAbilityType type;

        // ---- Passive（被动修正：解析前/结算时修正数值，可叠加）----
        public PassiveTarget passiveTarget;
        public int passiveValue;
        public bool applyBeforeResolve = true; // true=模板解析前（能力修正）；false=伤害结算时（伤害修正）

        // ---- Trigger（事件触发：触发点 + 待执行队列；第一版作用于自身、暂无限次）----
        public TriggerPoint triggerPoint;
        public TriggerEffect triggerEffect;
        public int amount; // HealDurability 用（+amount 承伤）

        // ---- Attach（附着：随攻击/移动附加结算）----
        public AttachPoint attachPoint;
        public AttackShape attachShape = AttackShape.Cross; // 默认十字
        public int attachDamage; // 0 = 沿用主伤害
    }
}
