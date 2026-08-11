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
        private bool _origRecorded;
        private Tween _tween;

        void Awake()
        {
            _img = GetComponent<Image>();
            _rt = (RectTransform)transform;
        }

        /// <summary>吸附生效：放大 + 金色（首次激活记录原始值——防重复 Activate 覆盖）。</summary>
        public void Activate()
        {
            if (_img == null) return;
            if (!_origRecorded)
            {
                _origColor = _img.color;
                _origScale = _rt.localScale;
                _origRecorded = true;
            }
            Kill();
            _tween = DOTween.Sequence()
                .Append(_rt.DOScale(_origScale * 1.3f, 0.08f).SetEase(Ease.OutBack))
                // 颜色用核心 API（Image.DOColor 属 DOTweenModuleUI——可能受程序集可见性影响不可用）
                .Join(DOTween.To(() => _img.color, c => _img.color = c, new Color(1f, 0.84f, 0.2f, 1f), 0.08f)); // 金色 #FFD75C
        }

        /// <summary>吸附解除：恢复原始颜色/缩放（幂等）。</summary>
        public void Deactivate()
        {
            Kill();
            if (_img != null && _rt != null && _origRecorded)
            {
                _img.color = _origColor;
                _rt.localScale = _origScale;
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
