using System.Collections.Generic;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 手牌动态布局：间距按手牌数双模式插值（少=宽松，多=堆叠）+ hover 让位（指数衰减）。
    /// 挂 Grp_Hand；每帧把卡片位置插值到目标位（布局管 x，卡片自身管 y/scale/层级）。
    /// </summary>
    public class HandLayoutController : MonoBehaviour
    {
        const float SparseGap = 15f;         // 牌少：卡间空隙
        const float DenseReveal = 48f;       // 牌多：每张露出宽度
        const float HoverPushFactor = 0.5f;  // 相邻让位衰减（0.5^距离）
        const float HoverPush = 160f;        // 让位幅度（保序约束：< 4×最小间距=192）
        const float CardScale = 0.35f;       // 叠放基准缩放（与 HandCardDrag 一致）

        RectTransform _root;
        readonly List<RectTransform> _cards = new List<RectTransform>();
        int _hoverIndex = -1;
        float _cardWidth = 100f;
        float _cardHeight = 200f;

        void Awake()
        {
            _root = (RectTransform)transform;
        }

        /// <summary>手牌重建后调用：重新收集卡片。</summary>
        public void RefreshCards()
        {
            _cards.Clear();
            foreach (Transform child in transform)
            {
                if (!child.gameObject.activeSelf) continue;
                var rt = child as RectTransform;
                if (rt != null)
                {
                    _cards.Add(rt);
                    if (rt.rect.width > 0) _cardWidth = rt.rect.width;
                    if (rt.rect.height > 0) _cardHeight = rt.rect.height;
                }
            }
            _hoverIndex = -1;
            // 立即落位（无动画）
            ApplyLayout(instant: true);
        }

        public void SetHoverIndex(int index)
        {
            _hoverIndex = index;
        }

        void Update()
        {
            _cards.RemoveAll(rt => rt == null); // 已销毁引用过滤（手牌重建 Destroy 延迟）
            ApplyLayout(instant: false);
        }

        void ApplyLayout(bool instant)
        {
            int n = _cards.Count;
            if (n == 0) return;

            // 卡片显示宽（原始宽 × 基准缩放 0.35）
            float cardW = _cardWidth * CardScale;
            // 锚参考点 = 手牌区左下角（父本地 −halfW, 0）；卡片 pivot(0.5,0.5) → 中心 = 锚参考点 + anchoredPosition
            float halfW = _root.rect.width * 0.5f;

            // 间距：≤4 展开（卡宽+15），≥8 堆叠（露出 48），中间平滑插值
            float expanded = cardW + SparseGap;
            float spacing;
            if (n <= 4) spacing = expanded;
            else if (n >= 8) spacing = DenseReveal;
            else spacing = Mathf.Lerp(expanded, DenseReveal, Mathf.SmoothStep(0f, 1f, (n - 4) / 4f));

            // y：卡片底部贴手牌区底部（中心 = 显示高/2）；hover 上浮 0.8 × 基准卡高
            float baseY = _cardHeight * CardScale * 0.5f;
            float hoverLift = _cardHeight * CardScale * 0.8f;

            if (instant)
            {
                Debug.Log($"[HandLayout] n={n} cardW={cardW} spacing={spacing} baseY={baseY} halfW={halfW}");
            }

            for (int i = 0; i < n; i++)
            {
                // 1) 基准中心：排列中心 = 手牌区中心（父本地 x=0）
                float centerX = (i - (n - 1) * 0.5f) * spacing;

                // 2) hover 让位：相邻卡向两侧指数衰减位移（悬停卡自身不动）
                if (_hoverIndex >= 0 && i != _hoverIndex)
                {
                    int d = Mathf.Abs(i - _hoverIndex);
                    float dir = i > _hoverIndex ? 1f : -1f;
                    centerX += dir * HoverPush * Mathf.Pow(HoverPushFactor, d);
                }

                float centerY = i == _hoverIndex ? baseY + hoverLift : baseY;

                // 3) 卡片中心（父本地）→ anchoredPosition（+halfW 补偿锚参考点；scale 不参与）
                var rt = _cards[i];
                Vector2 target = new Vector2(centerX + halfW, centerY);
                if (instant)
                {
                    rt.anchoredPosition = target;
                }
                else
                {
                    rt.anchoredPosition = Vector2.Lerp(rt.anchoredPosition, target, Time.deltaTime * 12f);
                }
            }
        }
    }
}
