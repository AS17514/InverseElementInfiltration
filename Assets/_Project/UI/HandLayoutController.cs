using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

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
        const float HoverPushFactor = 0.5f;    // 相邻让位衰减（0.5^距离）
        const float HoverPush = 160f;          // 让位幅度（保序约束：< 4×最小间距=192）
        const float HoverLift = 650f;          // hover 整体上浮（卡顶高出 手牌区顶 650px）
        const float CollapseLiftBonus = 100f;   // 收起状态额外高度修正

        RectTransform _root;
        Canvas _canvas;
        readonly List<RectTransform> _cards = new List<RectTransform>();
        int _hoverIndex = -1;
        int _hoverSibling = -1; // 提层前 sibling（恢复用）
        float _cardWidth = 100f;
        float _cardHeight = 200f;
        bool _collapsed; // 手牌区收起状态（BattleController 阶段驱动，显式设置）
        bool _dragging;  // 拖拽中标记：冻结 hover/让位，防被拖卡受布局插值干扰

        void Awake()
        {
            _root = (RectTransform)transform;
            // 从父级找 Canvas（跳过自身——Grp_Hand 运行时加了 overrideSorting 子 Canvas，worldCamera 为 null）
            _canvas = transform.parent != null ? transform.parent.GetComponentInParent<Canvas>() : null;
            // 手牌区独立排序，但渲染模式、相机、planeDistance 必须继承根 Canvas。
            // 子 Canvas 不能混用 Overlay 与父级 ScreenSpaceCamera，否则坐标和射线口径分裂。
            var containerCanvas = GetComponent<Canvas>();
            if (containerCanvas == null) containerCanvas = gameObject.AddComponent<Canvas>();
            if (_canvas != null)
            {
                containerCanvas.renderMode = _canvas.renderMode;
                containerCanvas.worldCamera = _canvas.worldCamera;
                containerCanvas.planeDistance = _canvas.planeDistance;
            }
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

        /// <summary>拖拽中冻结 hover（BattleController 拖拽起止时调用）——被拖卡不再参与 hover 让位/提层，手感更稳。</summary>
        public void SetDragging(bool dragging)
        {
            _dragging = dragging;
            if (_dragging) SetHover(-1); // 立即清 hover，防拖拽瞬间残留放大/让位
        }

        /// <summary>手牌重建后调用：重新收集卡片。instant=true 立即落位（无动画）；false 让布局插值滑动过渡。</summary>
        public void RefreshCards(bool instant = true)
        {
            // 2026-08-26 修复：面板隐藏中 AddComponent 时 Awake 被推迟（_root 未赋值）——
            // 手牌构建（LoadAndBuildHand）可能在面板显示前完成 → ApplyLayout 空引用炸协程。此处兜底补初始化。
            if (_root == null) _root = (RectTransform)transform;
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
            // 根 Canvas 由 PanelBase/UIRoot 装配；布局组件只读，不得每帧改全局 Canvas。
            UpdateHoverBySlot();
            ApplyLayout(instant: false);
        }

        /// <summary>光标在手牌区内 → 按水平 N 等分 slot 判定 hover 卡（实时切换）。</summary>
        void UpdateHoverBySlot()
        {
            if (_dragging)
            {
                SetHover(-1);
                return;
            }
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
            // 2026-08-12：activeInputHandler=2（纯 Input System）——旧 Input.mousePosition 失效 → 迁移 InputSystem
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero, uiCam, out Vector2 local))
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
            if (_root == null) return; // 面板隐藏中 AddComponent → Awake 推迟（激活后自动补齐，Update 每帧重试）
            int n = _cards.Count;
            if (n == 0) return;

            float cardW = _cardWidth * CardScale;          // 显示宽 270.7
            float halfW = _root.rect.width * 0.5f;         // 570

            // 间距 = 手牌区全宽均分——与 UpdateHoverBySlot 的 N 等分判定完全对齐（显示位置 = hover 触发区域）
            float spacing = n > 0 ? _root.rect.width / n : 0f;

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
