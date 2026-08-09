using TheLaw.Data;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>卡片背景色（种类标识，低饱和度：初始=绿 / 部署=蓝 / 升变=红）——手牌卡/编辑面板共用。</summary>
    public static class CardTypeColors
    {
        public static Color For(PieceType type)
        {
            switch (type)
            {
                case PieceType.Initial: return new Color(0.38f, 0.58f, 0.38f, 1f);   // 绿
                case PieceType.Deployable: return new Color(0.38f, 0.52f, 0.70f, 1f); // 蓝
                default: return new Color(0.68f, 0.42f, 0.42f, 1f);                   // 红（升变）
            }
        }
    }
}
