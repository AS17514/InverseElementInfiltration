namespace TheLaw.Data
{
    /// <summary>
    /// 玩法注册表（2026-08-24 玩法选择机制）：玩法 id 单一来源——
    /// 候选抽取（玩法事件二选一）与激活判定（IsStyleActive）共用同一常量集；
    /// 候选池 = 全部玩法 − 已激活玩法（ActiveStyles 推导——禁止散落字符串字面量）。
    /// "basic" 是能力词条不是玩法（能力池过滤用——见 Resolver.AbilityPool）。
    /// </summary>
    public static class StyleRegistry
    {
        public const string Mahjong = "mahjong"; // 麻将（2026-08-20 玩法，Mahjong.StyleId 指向本常量）
        public const string Element = "element"; // 属性（五行相克相生）
        public const string Dice = "dice";       // 骰子（2026-08-24 新玩法）
        public const string Go = "go";           // 围棋（2026-08-24 新玩法）
        public const string Token = "token";     // 代币（2026-08-24 新玩法）

        /// <summary>全部玩法 id（候选抽取池——玩法事件从"未激活玩法"中随机抽 2，无放回）。</summary>
        public static readonly string[] All = { Mahjong, Element, Dice, Go, Token };
    }
}