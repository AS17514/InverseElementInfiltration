using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 行为逻辑块 hover 检测（3D SpriteRenderer + BoxCollider，主相机射线触发）。
    /// hover → 通知 BattleController 显示 UI 浮窗（sprite 左上角对齐浮窗右上角）。
    /// </summary>
    public class BehaviorSlotHover : MonoBehaviour
    {
        BattleController _controller;
        int _slotIndex;

        public void Init(BattleController controller, int slotIndex)
        {
            _controller = controller;
            _slotIndex = slotIndex;
        }

        void OnMouseEnter()
        {
            _controller?.ShowBehaviorTooltip(_slotIndex, GetLeftTop());
        }

        void OnMouseExit()
        {
            _controller?.HideBehaviorTooltip();
        }

        /// <summary>sprite 左上角世界坐标（bounds AABB 左上）。</summary>
        Vector3 GetLeftTop()
        {
            var sr = GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                var b = sr.bounds;
                return new Vector3(b.min.x, b.max.y, b.center.z);
            }
            return transform.position;
        }
    }
}
