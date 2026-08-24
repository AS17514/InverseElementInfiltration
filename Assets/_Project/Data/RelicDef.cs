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
