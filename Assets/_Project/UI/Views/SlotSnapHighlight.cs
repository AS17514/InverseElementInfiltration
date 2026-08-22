using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 槽位吸附高亮（棋子编辑面板拖拽吸附的视觉提醒）：放大 1.3× + 变色金色，可复用可清理。
    /// 挂 Piece_ProgramInfo 槽位节点（FillPieceCard 中幂等 AddComponent），由 EditorProgramDrag 的吸附状态机驱动。
    /// </summary>
    public class SlotSnapHighlight : MonoBehaviour
    {
        private Image _img;
        private RectTransform _rt;
        private Color _origColor;
        private Vector3 _origScale;
        private Tween _tween;

        void Awake()
        {
            _img = GetComponent<Image>();
            _rt = (RectTransform)transform;
        }

        /// <summary>吸附生效：放大 + 金色。每次激活重录原值（拖拽始于上次落账/选中后——记录即最新状态色）。</summary>
        public void Activate()
        {
            if (_img == null) return;
            _origColor = _img.color;
            _origScale = _rt.localScale;
            Kill();
            _tween = DOTween.Sequence()
                .Append(_rt.DOScale(_origScale * 1.3f, 0.08f).SetEase(Ease.OutBack))
                // 颜色用核心 API（Image.DOColor 属 DOTweenModuleUI——可能受程序集可见性影响不可用）
                .Join(DOTween.To(() => _img.color, c => _img.color = c, new Color(1f, 0.84f, 0.2f, 1f), 0.08f)); // 金色 #FFD75C
        }

        /// <summary>
        /// 吸附解除：Kill tween + 恢复颜色/缩放。
        /// restoreColor=false：落账成功路径（颜色已由 CommitProgram→FillPieceInfo 设新状态色，不覆盖）；
        /// restoreColor=true：取消/失败/无效释放路径（无落账无刷新——恢复原色防金色残留）。
        /// </summary>
        public void Deactivate(bool restoreColor = true)
        {
            Kill();
            if (_rt != null)
            {
                _rt.localScale = _origScale;
            }
            if (_img != null && restoreColor)
            {
                _img.color = _origColor;
            }
        }

        void Kill()
        {
            if (_tween != null)
            {
                _tween.Kill();
                _tween = null;
            }
        }

        void OnDestroy()
        {
            Kill();
        }
    }
}
