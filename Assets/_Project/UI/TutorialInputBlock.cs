using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 教程输入阻挡层（Graphic 子类，无网格渲染）：
    /// 教程激活时全屏阻挡点击，但【挖孔内放行】——玩家可操作被高亮的目标，孔外一律挡住。
    /// 由 TutorialMask 管理开关与挖孔矩形（屏幕像素坐标，含 padding）。
    /// </summary>
    public class TutorialInputBlock : Graphic
    {
        TutorialMask _mask;

        public void Bind(TutorialMask mask)
        {
            _mask = mask;
        }

        public override bool Raycast(Vector2 sp, Camera eventCamera)
        {
            // 用户定案：教程期间全屏阻挡，挖孔内也不放行（挖孔仅作视觉高亮）
            return _mask != null && _mask.IsBlocking;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            // ⚠️ 必须有网格：空网格会被 CanvasRenderer 裁剪（cull=true）→ GraphicRaycaster 直接跳过 → 挡不住点击。
            // 填充全屏透明四边形：alpha=0 不可见，但参与射线检测。
            vh.Clear();
            Rect r = GetPixelAdjustedRect();
            Color c = color;
            vh.AddVert(new Vector3(r.xMin, r.yMin), c, Vector2.zero);
            vh.AddVert(new Vector3(r.xMax, r.yMin), c, Vector2.one);
            vh.AddVert(new Vector3(r.xMax, r.yMax), c, Vector2.one);
            vh.AddVert(new Vector3(r.xMin, r.yMax), c, Vector2.zero);
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }
    }
}
