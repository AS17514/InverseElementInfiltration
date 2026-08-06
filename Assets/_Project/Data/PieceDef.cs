using System.Collections.Generic;
using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>
    /// 棋子定义（一张牌的数据，SO 资产，纯配置——启动加载全程不变）。
    /// 瘦身版：移动/攻击参数移入模板；只留固有属性 + 特殊能力 + 程序集。
    /// </summary>
    public class PieceDef : GameConfigBase
    {
        public string displayName;                     // 中文显示名（资产名用英文——UI 显示用这个）
        public PieceType pieceType;                    // 种类：初始/部署/升变
        public int value;                              // 价值（1~9，击杀得积分；构筑总价值限制依据）
        public int durability;                         // 承伤次数（无 HP——被攻击扣次数，归 0 死亡）
        public Footprint footprint = Footprint.Size1x1;// 占格（1×1 先实现）
        public Facing defaultFacing = Facing.Up;       // 初始朝向（默认 Up=向前）
        public List<SpecialAbilityDef> specialAbilities = new List<SpecialAbilityDef>(); // 固有特殊能力
        public List<ProgramDef> programSet = new List<ProgramDef>(); // 程序集：默认模组[0] + 备用模组（战斗中模板变化用）
        public int promotionConfigId;                  // 升变映射（0 = 不可升变）
    }
}
