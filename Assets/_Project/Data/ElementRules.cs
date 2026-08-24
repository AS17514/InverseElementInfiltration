using System;

namespace TheLaw.Data
{
    /// <summary>
    /// 属性玩法「五行」判定规则（2026-08-20 实现——源自属性玩法口述记录）：
    /// 相生：土→金→水→木→火→土（循环）——土生金，金生水，水生木，木生火，火生土
    /// 相克：金→木→土→水→火→金（循环）——金克木，木克土，土克水，水克火，火克金
    /// 静态判定表——只判段关系，不涉及状态（状态归属见决策记录_牌数据结构与玩法语义）。
    /// </summary>
    public static class ElementRules
    {
        /// <summary>a 是否克制 b（金克木…；None（无属性）不参与判定——返回 false）。</summary>
        public static bool IsCountering(Element a, Element b)
        {
            if (a == Element.None || b == Element.None) return false;
            switch (a)
            {
                case Element.Gold: return b == Element.Wood;   // 金克木
                case Element.Wood: return b == Element.Earth;  // 木克土
                case Element.Earth: return b == Element.Water; // 土克水
                case Element.Water: return b == Element.Fire;  // 水克火
                case Element.Fire: return b == Element.Gold;   // 火克金
                default: return false;
            }
        }

        /// <summary>a 是否相生 b（土生金…；None 不参与）。</summary>
        public static bool IsGenerating(Element a, Element b)
        {
            if (a == Element.None || b == Element.None) return false;
            switch (a)
            {
                case Element.Earth: return b == Element.Gold;  // 土生金
                case Element.Gold: return b == Element.Water;  // 金生水
                case Element.Water: return b == Element.Wood;  // 水生木
                case Element.Wood: return b == Element.Fire;   // 木生火
                case Element.Fire: return b == Element.Earth;  // 火生土
                default: return false;
            }
        }

        /// <summary>某属性的"相生属性"（它生成的对象——金→水、木→火、水→木、火→土、土→金；None → None。2026-08-24 提纯用）。</summary>
        public static Element GeneratingOf(Element a)
        {
            switch (a)
            {
                case Element.Gold: return Element.Water;  // 金生水
                case Element.Wood: return Element.Fire;   // 木生火
                case Element.Water: return Element.Wood;  // 水生木
                case Element.Fire: return Element.Earth;  // 火生土
                case Element.Earth: return Element.Gold;  // 土生金
                default: return Element.None;
            }
        }
    }
}
