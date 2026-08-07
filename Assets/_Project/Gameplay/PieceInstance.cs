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
            durability = def.durability;
            facing = def.defaultFacing;
            isDeployed = true;
            shieldCount = GetShieldAmount(); // 初始护盾 = 固有 ShieldBlock 能力 amount 之和
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

        /// <summary>该棋子全部特殊能力（固有 + 临时）——被动修正/触发/附着的查询源。</summary>
        public List<SpecialAbilityDef> GetAllAbilities()
        {
            var result = new List<SpecialAbilityDef>();
            if (def != null)
            {
                result.AddRange(def.specialAbilities);
            }
            result.AddRange(tempAbilities);
            return result;
        }
    }
}
