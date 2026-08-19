using System.Collections.Generic;
using TheLaw.Data;
using UnityEngine;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 棋子实例（战斗中的棋子，运行时状态）。
    /// 程序不存实例上——通过 def 查询（程序三层查找：实例覆盖① > 种类级表② > Def 默认③）。
    /// </summary>
    public class PieceInstance
    {
        public int Id;                          // 战斗中唯一 id（GameState 分配）
        public PieceDef def;                    // 属于哪种棋子（引用）
        public int DefId => def != null ? def.Id : _defId;
        private int _defId;

        public Side side;
        public int durability;                  // 当前承伤（被攻击扣次数，归 0 死亡）
        public Vector2Int position;
        public Facing facing;
        public List<Template> programOverride;  // ① 实例程序覆盖（战斗中临时变化，随战斗销毁；null=无）
        public List<SpecialAbilityDef> tempAbilities = new List<SpecialAbilityDef>(); // 临时获得能力（随战斗销毁）
        public bool isDeployed;                 // 是否已部署上场
        public int waveIndex = -1;              // 所属波次（-1=非波次棋子；每波得分按此累计）
        public int shieldCount;                 // 剩余护盾（抵挡伤害用，一次性不恢复；入快照）

        public PieceInstance(PieceDef def, Side side, Vector2Int position)
        {
            this.def = def;
            _defId = def.Id;
            this.side = side;
            this.position = position;
            ApplyDefProperties(); // 承伤/护盾按 def 初始化（与升变共用同一初始化路径——防属性只初始化一条路径再漏）
            // 敌方与我方对向——初始朝向翻转（我方朝上/向前，敌方朝下/向后；朝向只在创建时定，升变保留原朝向）
            facing = side == Side.Enemy ? OppositeFacing(def.defaultFacing) : def.defaultFacing;
            isDeployed = true;
        }

        /// <summary>
        /// 按当前 def 重算"def 决定的可变实例属性"（创建与升变共用）。
        /// ⚠️ 2026-08-12：升变此前只更新 durability、漏了 shieldCount（新身体护盾丢失）——提炼统一方法根治；
        /// 以后新增"def 决定的可变属性"只需在此添加，创建/升变两条路径自动覆盖。
        /// </summary>
        public void ApplyDefProperties()
        {
            durability = def.durability;
            shieldCount = GetShieldAmount(); // 固有 + 临时护盾能力之和（临时能力保留——实例状态不因升变消失）
        }

        /// <summary>朝向翻转（上下互换/左右互换）——敌方与我方对向。</summary>
        public static Facing OppositeFacing(Facing f)
        {
            switch (f)
            {
                case Facing.Up: return Facing.Down;
                case Facing.Down: return Facing.Up;
                case Facing.Left: return Facing.Right;
                case Facing.Right: return Facing.Left;
                default: return f;
            }
        }

        /// <summary>护盾量 = 该棋子全部 ShieldBlock 能力（固有 + 临时）amount 之和。</summary>
        public int GetShieldAmount()
        {
            int total = 0;
            foreach (var ability in GetAllAbilities())
            {
                if (ability.type == SpecialAbilityType.Trigger && ability.triggerPoint == TriggerPoint.OnDamaged
                    && ability.triggerEffect == TriggerEffect.ShieldBlock)
                {
                    total += ability.amount;
                }
            }
            return total;
        }

        /// <summary>
        /// 程序三层查找：实例覆盖① > 种类级表② > Def 默认③。
        /// 执行开始即定稿（变化只影响下一次执行——由 BattleFlow 保证）。
        /// </summary>
        public List<Template> GetProgram(GameState state)
        {
            if (programOverride != null && programOverride.Count > 0)
            {
                return programOverride; // ①
            }
            if (state.TryGetCurrentProgram(DefId, out var edited))
            {
                return edited; // ② 编辑事件（敌我共享）
            }
            if (def != null && def.programSet.Count > 0)
            {
                return def.programSet[0].slots; // ③ Def 默认模组
            }
            return null;
        }

        /// <summary>
        /// 该棋子全部特殊能力（固有 + 效果模块装配 + 临时）——被动修正/触发/附着的查询源。
        /// ⚠️ 2026-08-19 效果模块"装配即生效"（策划确认：不耗 AP、被动、有模块即生效）：
        /// 程序（实例覆盖① > 编辑差异② > Def 默认③）中的 EffectTemplate 能力动态并入——
        /// 编辑程序后自动生效（无需重新物化）；能力引用经 ConfigTable.FindByName（abilityKey）。
        /// ⚠️ 2026-08-19 叠加语义（用户确认"护盾可叠加"）：**不去重**——同能力资产的多个来源（内部模块 +
        /// 外部模块——如盾兵 Effect-1 + 外部 Effect-4 护盾）按实例各计一次（护盾 1+1=2）；
        /// 棋子固有能力已迁移为程序效果模块（abilities 字段移除——"特殊能力=行动槽"）。
        /// </summary>
        public List<SpecialAbilityDef> GetAllAbilities()
        {
            var result = new List<SpecialAbilityDef>();
            if (def != null)
            {
                result.AddRange(def.specialAbilities);
            }
            // 效果模块（装配即生效）：程序中的 EffectTemplate → 能力并入（按实例叠加——不去重）
            var state = GameState.Instance;
            if (state != null)
            {
                var program = GetProgram(state);
                if (program != null)
                {
                    foreach (var slot in program)
                    {
                        if (slot is EffectTemplate effect && !string.IsNullOrEmpty(effect.abilityKey))
                        {
                            var ability = ConfigTable.FindByName<SpecialAbilityDef>(effect.abilityKey);
                            if (ability != null)
                            {
                                result.Add(ability);
                            }
                        }
                    }
                }
            }
            result.AddRange(tempAbilities);
            return result;
        }
    }
}
