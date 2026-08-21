using System;

namespace TheLaw.Data
{
    /// <summary>
    /// 牌（2026-08-20 牌结构改造——手牌/抽牌堆元素）。
    /// 一张牌要么是棋子牌（defId &gt; 0），要么是麻将牌（value = 点数 1~9）；
    /// element 携带属性（属性玩法复制牌"属性相同"用；普通牌/麻将牌 = None）。
    /// 值语义（struct）——同种可重复（构筑 12 张）；存档直接序列化（无多态类型名）。
    /// 决策依据：决策记录_牌数据结构与玩法语义_20260820.md（单值类型 vs 父类多态 vs 独立管理器）。
    /// ⚠️ 2026-08-21：+ instanceId（运行实例标识——牌进入 Hand/DrawPile 时由 Resolver 统一分配；
    /// 同 defId/同属性可多张各有唯一 id；麻将两张同点数也各分配（机制统一——业务上同点数等价用不上）；
    /// 读卡请求（Deploy/Promote 等）按 id 精确消费；0=未分配（默认）。
    /// </summary>
    [Serializable]
    public struct Card
    {
        public int instanceId;   // 运行实例 id（2026-08-21：进牌区时 Resolver 分配——同一张牌的指认标识；0=未分配）
        public int defId;        // 棋子牌：棋子 Def id；麻将牌 = 0
        public int value;        // 麻将牌：点数 1~9；棋子牌 = 0
        public Element element;  // 属性：None（无属性——基础玩法/麻将牌）/ 金木水火土

        public Card(int defId)
        {
            this.instanceId = 0;
            this.defId = defId;
            this.value = 0;
            this.element = Element.None;
        }

        /// <summary>棋子牌（无属性——instanceId 由统一入口分配）。</summary>
        public static Card Piece(int defId) => new Card { defId = defId };

        /// <summary>棋子牌（带属性——复制牌"属性相同"；instanceId 由统一入口分配）。</summary>
        public static Card Piece(int defId, Element element) => new Card { defId = defId, element = element };

        /// <summary>麻将牌（点数 1~9——非棋子，不带属性；instanceId 由统一入口分配）。</summary>
        public static Card Mahjong(int point) => new Card { value = point };

        /// <summary>是否棋子牌。</summary>
        public bool IsPiece => defId > 0;

        /// <summary>是否麻将牌（非棋子——不受属性玩法影响）。</summary>
        public bool IsMahjong => defId <= 0 && value > 0;

        /// <summary>牌的价值（和牌雀头判定/得分参考用）：棋子牌 = 棋子价值（外部查 EffectiveValue）；麻将牌 = 点数。</summary>
        public override string ToString() => $"#{instanceId} " + (IsMahjong ? $"麻将{value}" : $"牌{defId}");
    }
}
