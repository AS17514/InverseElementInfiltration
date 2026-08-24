using System.Collections.Generic;
using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>
    /// 能力基础效果类型（2026-08-22 能力=基础效果组合模型——能力（遗物）由 effects 组合而成，可复用）。
    /// 承载：APBonus（行动点上限+N）/ DrawExtra（花费AP抽牌额外一张）/ PromoteCopyDeployable（升变部署棋子→复制牌入手）
    /// ActionEconomy（行动经济：执行不耗AP+每棋子每回合一次——合并buff）/ DeployRow（己方部署区+N行）
    /// DrawEditedImmediate（抽到被编辑牌→立即部署/升变+执行一次——E5，待插入骨架）。
    /// 2026-08-24 能力池 P1 扩展（单玩法 9 条）：见决策记录_能力池扩展_20260824。
    /// </summary>
    public enum RelicEffectType
    {
        APBonus,                  // 行动点上限 +value（获得时即时生效）
        DrawExtra,                // 花费 AP 抽牌时额外抽 value 张
        PromoteCopyDeployable,    // 升变"部署"棋子时复制牌入手
        ActionEconomy,            // 行动经济（执行不耗 AP + 每棋子每回合一次——语义见决策记录）
        DeployRow,                // 己方部署区 +value 行
        DrawEditedImmediate,      // 抽到被编辑过的棋子→立即部署/升变+执行一次（E5——高亮资格式）
        // ====== 2026-08-24 能力池 P1（单玩法 9 条）======
        MahjongFillOnDeployPromote, // 改良：己方部署/升变棋子时，棋子价值填入牌山
        HuBaseScoreBonus,           // 改良：和牌时基础得分 + 雀头价值（多个按最高）
        Baopai,                     // 宝牌：本局选 1-9 数字；对应价值牌进弃牌区+价值分；组成刻/顺番数额外+1
        ElementRefine,              // 提纯：手牌回合开始变相生属性；部署/升变全场棋子属性统一+变化计分
        GoDeployExtra,              // 速攻：每回合围棋可部署 2 次（限次 1→2）
        GoValueUp,                  // 升值：每次部署围棋→全场围棋价值+1（战斗级）
        GoPromote,                  // 假定：围棋也可被升变（用手牌升变牌，升变为该牌棋子）
        DiceRig,                    // 出千：投掷骰子点数可自选（1-6）
        TokenOnKill,                // 开源：每次击败对方棋子获得 1 代币
        TokenSpendMultiplier,       // 节流：消耗代币时倍率+1；购买非初始棋子获得 1 代币
        // ====== 2026-08-24 能力池 P2（复合 5 条——买子挂起；双玩法 tags 由配置保证）======
        DiceToMahjongScore,         // 骨骰骨牌：投掷时点数填牌山；点数参与组成刻/顺 → 当回合 AP+1（当前点数，不回上限不钳制）
        TokenOnWallBreak,           // 麻将筹码：部署的麻将牌被破坏 → +1 代币（购买弃牌区麻将牌已天然支持）
        ElementRerollOnRoll,        // 变换：投掷时场上及手牌所有棋子重刷属性（随机）
        ElementMatchMultiplier,     // 属性骰子：部署"骰子对应属性"的棋子 → 倍率+1（6=任意属性触发）
        TokenOnRoll,                // 老虎机·投掷获得骰子点数代币
        SlotDrawOnMatch,            // 老虎机·花费代币数 == 当前骰子点数 → 抽一张并丢弃一张到弃牌区
        // ====== 2026-08-24 能力池 P3（震击/吃子——"编辑"前缀能力，tags basic）======
        ShockWall,                  // 震击：开局非部署区随机生成 2 个不可破坏墙；我方攻击墙 → 周围 8 格所有棋子受固定 1 伤害
        Devour,                     // 吃子：我方执行跳过攻击槽；移动落点可踩敌方格并直接击败（无视承伤含护盾——仅玩家侧）
    }

    /// <summary>能力基础效果规格（组合中的一个原子效果——type + value）。</summary>
    [System.Serializable]
    public class RelicEffectSpec
    {
        public RelicEffectType type;
        public int value = 1;
    }

    /// <summary>
    /// 遗物定义（SO 资产，局内获得，整局持续、可叠加）。
    /// 遗物 = 能力载体：词条（tags——按玩法过滤能力池） + 效果组合（effects——基础效果解耦复用）；
    /// 另有 abilities（SpecialAbilityDef——旧式：Passive/Trigger/Attach）并存（旧遗物沿用）。
    /// </summary>
    public class RelicDef : GameConfigBase
    {
        public string displayName;
        public string description;
        public List<string> tags = new List<string>();                    // 词条（2026-08-22："basic"/"mahjong"/"element"——能力事件按玩法词条过滤）
        public List<RelicEffectSpec> effects = new List<RelicEffectSpec>(); // 基础效果组合（2026-08-22 能力模型）
        public List<SpecialAbilityDef> abilities = new List<SpecialAbilityDef>();
    }
}
