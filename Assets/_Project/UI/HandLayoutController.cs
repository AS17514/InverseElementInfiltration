using System.Collections.Generic;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 手牌动态布局（槽位区域判定版）：
    /// - 光标在手牌区内 → 按水平 N 等分 slot 实时 hover 对应卡；移出 → 回落
    /// - 卡片表现统一管理：scale（0.35/0.7 插值）、y（上浮=放大补偿 196）、x（排列+让位）、提层
    /// - 判定与卡片位置解耦（卡片动不影响判定，无 enter/exit 抖动）
    /// 挂 Grp_Hand。HandCardDrag 只负责拖拽。
    /// </summary>
    public class HandLayoutController : MonoBehaviour
    {
        const float CardScale = 0.35f;         // 叠放基准缩放
        const float HoverScale = 0.7f;         // hover 放大（2 倍）
        const float SparseGap = 15f;           // 牌少：卡间空隙
        const float DenseReveal = 48f;         // 牌多：每张露出宽度
        const float HoverPushFactor = 0.5f;    // 相邻让位衰减（0.5^距离）
        const float HoverPush = 160f;          // 让位幅度（保序约束：< 4×最小间距=192）
        const float HoverLift = 500f;          // hover 整体上浮（卡顶高出 手牌区顶 500px）
        const float CollapseLiftBonus = 100f;   // 收起状态额外高度修正

        RectTransform _root;
        Canvas _canvas;
        readonly List<RectTransform> _cards = new List<RectTransform>();
        int _hoverIndex = -1;
        int _hoverSibling = -1; // 提层前 sibling（恢复用）
        float _cardWidth = 100f;
        float _cardHeight = 200f;
        bool _collapsed; // 手牌区收起状态（BattleController 阶段驱动，显式设置）

        void Awake()
        {
            _root = (RectTransform)transform;
            _canvas = GetComponentInParent<Canvas>();
            // 手牌区独立渲染层（最高 sortingOrder）——hover 卡提层即可盖住手牌区外 UI
            // 挂在容器上而非卡片上：动态增删卡片 Canvas 会破坏 UGUI 射线（拖拽失效）
            var containerCanvas = GetComponent<Canvas>();
            if (containerCanvas == null) containerCanvas = gameObject.AddComponent<Canvas>();
            containerCanvas.overrideSorting = true;
            containerCanvas.sortingOrder = 50;
            if (GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
            {
                gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>(); // 子 Canvas 需自带 raycaster 保持拖拽
            }
        }

        /// <summary>手牌区状态：true=收起（非准备阶段），false=展开（准备阶段）。由 BattleController 阶段驱动。</summary>
        public void SetCollapsed(bool collapsed)
        {
            _collapsed = collapsed;
        }

        /// <summary>手牌重建后调用：重新收集卡片。instant=true 立即落位（无动画）；false 让布局插值滑动过渡。</summary>
        public void RefreshCards(bool instant = true)
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
            _hoverSibling = -1;
            ApplyLayout(instant: instant); // 用传入参数（false = 复用卡从旧位置插值滑动过渡）
        }

        void Update()
        {
            UpdateHoverBySlot();
            ApplyLayout(instant: false);
        }

        /// <summary>光标在手牌区内 → 按水平 N 等分 slot 判定 hover 卡（实时切换）。</summary>
        void UpdateHoverBySlot()
        {
            int n = _cards.Count;
            if (n == 0)
            {
                SetHover(-1);
                return;
            }
            Camera uiCam = _canvas != null ? _canvas.worldCamera : null;
            if (uiCam == null)
            {
                SetHover(-1);
                return;
            }
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, Input.mousePosition, uiCam, out Vector2 local))
            {
                float halfW = _root.rect.width * 0.5f;
                // 光标必须完整在手牌区矩形内（x + y）
                if (local.x < -halfW || local.x > halfW || local.y < 0f || local.y > _root.rect.height)
                {
                    SetHover(-1);
                    return;
                }
                int slot = Mathf.FloorToInt((local.x + halfW) / (_root.rect.width / n));
                SetHover(Mathf.Clamp(slot, 0, n - 1));
            }
            else
            {
                SetHover(-1);
            }
        }

        void SetHover(int index)
        {
            if (_hoverIndex == index) return;
            // 恢复上一张 hover 卡的层级（渲染层由手牌区 Canvas 统一保证）
            if (_hoverIndex >= 0 && _hoverIndex < _cards.Count && _cards[_hoverIndex] != null)
            {
                if (_hoverSibling >= 0)
                {
                    _cards[_hoverIndex].SetSiblingIndex(Mathf.Min(_hoverSibling, _cards[_hoverIndex].parent.childCount - 1));
                }
            }
            _hoverIndex = index;
            _hoverSibling = -1;
            if (index >= 0 && index < _cards.Count && _cards[index] != null)
            {
                _hoverSibling = _cards[index].GetSiblingIndex();
                _cards[index].SetAsLastSibling(); // 提层（手牌区内；手牌区层 50 已盖住其他 UI）
            }
        }

        void ApplyLayout(bool instant)
        {
            int n = _cards.Count;
            if (n == 0) return;

            float cardW = _cardWidth * CardScale;          // 显示宽 270.7
            float halfW = _root.rect.width * 0.5f;         // 570

            // 间距：≤4 展开（卡宽+15），≥8 堆叠（露出 48），中间平滑插值
            float expanded = cardW + SparseGap;
            float spacing;
            if (n <= 4) spacing = expanded;
            else if (n >= 8) spacing = DenseReveal;
            else spacing = Mathf.Lerp(expanded, DenseReveal, Mathf.SmoothStep(0f, 1f, (n - 4) / 4f));

            // y：基础 = 顶部贴当前手牌区顶（随收起自适应）；hover = 上浮基准固定（展开态 250 高度）——
            // 收起时补齐高度差，上浮卡顶不因手牌区变矮而降低
            float handTop = _root.rect.height;
            const float ExpandedHandHeight = 210f; // 准备阶段手牌区高度（上浮基准，与 BattleController targetH 一致）
            float baseY = handTop - _cardHeight * CardScale * 0.5f;
            float hoverY = ExpandedHandHeight + HoverLift - _cardHeight * HoverScale * 0.5f
                           + (_collapsed ? CollapseLiftBonus : 0f); // 收起时额外高度修正（阶段显式驱动）

            if (instant)
            {
                Debug.Log($"[HandLayout] n={n} cardW={cardW} spacing={spacing} hover={_hoverIndex} baseY={baseY} hoverY={hoverY}");
            }

            for (int i = 0; i < n; i++)
            {
                // 1) 基准中心：排列中心 = 手牌区中心（父本地 x=0）
                float centerX = (i - (n - 1) * 0.5f) * spacing;

                // 2) hover 让位（悬停卡自身不动）
                if (_hoverIndex >= 0 && i != _hoverIndex)
                {
                    int d = Mathf.Abs(i - _hoverIndex);
                    float dir = i > _hoverIndex ? 1f : -1f;
                    centerX += dir * HoverPush * Mathf.Pow(HoverPushFactor, d);
                }

                bool isHover = i == _hoverIndex;
                float centerY = isHover ? hoverY : baseY;
                float targetScale = isHover ? HoverScale : CardScale;

                var rt = _cards[i];
                if (rt == null) continue;
                // 3) 位置：卡片中心（父本地）→ anchoredPosition（+halfW 补偿锚参考点；scale 不参与）
                Vector2 target = new Vector2(centerX + halfW, centerY);
                // 4) 缩放：插值（hover 放大/回落）
                float curScale = rt.localScale.x;

                if (instant)
                {
                    rt.anchoredPosition = target;
                    rt.localScale = Vector3.one * targetScale;
                }
                else
                {
                    rt.anchoredPosition = Vector2.Lerp(rt.anchoredPosition, target, Time.deltaTime * 12f);
                    float s = Mathf.Lerp(curScale, targetScale, Time.deltaTime * 12f);
                    rt.localScale = Vector3.one * s;
                }
            }
        }
    }
}
