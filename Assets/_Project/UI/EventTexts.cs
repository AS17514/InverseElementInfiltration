using System.Collections.Generic;
using TheLaw.Data;

namespace TheLaw.UI
{
    /// <summary>
    /// 事件小文本（2026-08-25，来源：Assets/test/docs/事件小文本.docx）：
    /// 前端显示层覆盖——能力/玩法/编辑/构筑四事件标题与描述（TMP 富文本 <i> 斜体；{0} = 动态刷新次数）；
    /// 未登记事件回退事件定义原文本（events.json → EventDefinition 资产，数据域未动）。
    /// </summary>
    public static class EventTexts
    {
        static readonly Dictionary<string, string> Titles = new Dictionary<string, string>
        {
            ["ability_pick"] = "“牧场”的漏洞",
            ["edit_standard"] = "车间",
            ["deck_standard"] = "组装车间",
            ["rule_pick"] = "终端黑墙",
        };

        static readonly Dictionary<string, string> Descs = new Dictionary<string, string>
        {
            ["ability_pick"] = "Xeon为你侵入了“伊甸”的后端，你可以在这堆无聊代码里面加点小小的“礼物”，这将会对你有利，并且会伴随游戏的整个过程。\n<i>看起来Xeon对此格外上心，但你也不知道她的虚拟脑袋里在想些什么。</i>\n<i>长按选项按钮刷新事件（刷新次数：{0}）</i>",
            ["edit_standard"] = "密密麻麻的机械臂悬垂在半空，上面夹着的武器或是肢体闪着晶石般的光辉。3号把操作平板递给了你，也许你可以给这些小肢体做些什么。\n<i>“我喜欢下棋，但我并不喜欢拼装玩具”——3号</i>",
            ["deck_standard"] = "你进入棋盘前最后的整备地点，你可以调整随身的棋子牌组，毕竟待会要面对的是真正的敌人。\n<i>也许你也可以就此打道回府，“伊甸”的宽宏大量会原谅这一次的小小入侵。</i>\n<i>真的不能操控这些机械臂去打架吗，感觉真的会很强诶。</i>",
            ["rule_pick"] = "你们来到了终端面前，这是一面黑墙，背后就是“牧场”庞大的后端。你可以为这个游戏添上你的玩法，给“伊甸”一份沉重的“大礼”。这也将会伴随游戏的整个过程。\n<i>你可以一脉相承，也可以试试不同的组合。</i>\n<i>兔子，坦克，最佳搭配！</i>",
        };

        public static string TitleFor(string eventId, EventDefinition fallback)
        {
            if (!string.IsNullOrEmpty(eventId) && Titles.TryGetValue(eventId, out var t)) return NormalizePlus(t);
            return NormalizePlus(fallback != null && !string.IsNullOrEmpty(fallback.title) ? fallback.title : "未知事件");
        }

        /// <summary>全角＋ → 半角 +（2026-08-26：字体缺全角加号字形——显示层统一归一化）。</summary>
        static string NormalizePlus(string s)
        {
            return s == null ? string.Empty : s.Replace('＋', '+');
        }

        public static string DescFor(string eventId, EventDefinition fallback, int? refreshTotal = null)
        {
            if (!string.IsNullOrEmpty(eventId) && Descs.TryGetValue(eventId, out var d))
            {
                if (d.Contains("{0}")) d = d.Replace("{0}", (refreshTotal ?? 0).ToString());
                return NormalizePlus(d);
            }
            return NormalizePlus(fallback != null && !string.IsNullOrEmpty(fallback.description) ? fallback.description
                : fallback != null && !string.IsNullOrEmpty(fallback.title) ? fallback.title : string.Empty);
        }
    }
}
