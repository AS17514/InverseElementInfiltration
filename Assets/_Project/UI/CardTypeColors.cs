using System.Collections.Generic;
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

        /// <summary>类型权重（初始=0 部署=1 升变=2——排序用）。</summary>
        public static int TypeOrder(PieceType type)
        {
            switch (type)
            {
                case PieceType.Initial: return 0;
                case PieceType.Deployable: return 1;
                default: return 2; // Promoted
            }
        }

        /// <summary>棋子排序：类型优先（初始→部署→升变），同类型价值从小到大（全场景统一——编辑/构筑/手牌）。</summary>
        public static void SortPieces(List<PieceDef> defs)
        {
            if (defs == null) return;
            defs.Sort((a, b) =>
            {
                int t = TypeOrder(a.pieceType).CompareTo(TypeOrder(b.pieceType));
                return t != 0 ? t : a.value.CompareTo(b.value);
            });
        }
    }
}
