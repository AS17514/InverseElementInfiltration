using TheLaw.Data;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 五行属性视觉（2026-08-25 五行玩法前端适配）：元素 → 描边色（金木水火土）。
    /// 手牌外描边与棋子静态描边共用；颜色为表现常量（前端代码自决，不依赖美术资产）。
    /// </summary>
    public static class ElementColors
    {
        public static Color ColorOf(Element element)
        {
            switch (element)
            {
                case Element.Gold: return new Color(1f, 0.84f, 0.2f);    // 金
                case Element.Wood: return new Color(0.3f, 0.75f, 0.35f);  // 木
                case Element.Water: return new Color(0.25f, 0.6f, 1f);    // 水
                case Element.Fire: return new Color(0.95f, 0.3f, 0.2f);   // 火
                case Element.Earth: return new Color(0.62f, 0.45f, 0.3f); // 土
                default: return Color.white;
            }
        }

        public static string NameOf(Element element)
        {
            switch (element)
            {
                case Element.Gold: return "金";
                case Element.Wood: return "木";
                case Element.Water: return "水";
                case Element.Fire: return "火";
                case Element.Earth: return "土";
                default: return "";
            }
        }
    }
}
