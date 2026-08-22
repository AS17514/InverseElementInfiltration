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
                case PieceType.Initial: return new Color(0.80784314f, 1f, 0.72156864f, 1f);      // #CEFFB8
                case PieceType.Deployable: return new Color(0.59607846f, 0.77254903f, 0.9882353f, 1f); // #98C5FC
                default: return new Color(1f, 0.6666667f, 0.6666667f, 1f);                          // #FFAAAA（升变）
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
