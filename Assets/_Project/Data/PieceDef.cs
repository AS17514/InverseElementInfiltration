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
        /// <summary>
        /// 价值（配置值）。⚠️ 2026-08-15 策划新案：运行时价值一律走 PieceValue 推导（生效程序槽位总和——
        /// 编辑跨档即变种类）；本字段语义降为"默认价值"（默认程序槽位总和的配置镜像，供导入校验/旧数据兼容）；
        /// 积分/构筑/AI 选目标已改走推导（GameState.GetEffectiveValue）。
        /// </summary>
        public int value;
        public int durability;                         // 承伤次数（无 HP——被攻击扣次数，归 0 死亡）
        public Footprint footprint = Footprint.Size1x1;// 占格（1×1 先实现）
        public Facing defaultFacing = Facing.Up;       // 初始朝向（默认 Up=向前）
        public List<SpecialAbilityDef> specialAbilities = new List<SpecialAbilityDef>(); // 固有特殊能力
        public List<ProgramDef> programSet = new List<ProgramDef>(); // 程序集：默认模组[0] + 备用模组（战斗中模板变化用）
        public int promotionConfigId;                  // 升变映射（0 = 不可升变）
    }
}
