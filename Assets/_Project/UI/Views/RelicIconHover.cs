using TheLaw.Data;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TheLaw.UI
{
    /// <summary>
    /// 遗物图标 hover 描述（2026-08-14）：鼠标悬停 → TooltipManager 显示遗物名称+描述；移开隐藏。
    /// 挂 Image.prefab 实例（遗物列表 Grp_RelicDisplay 内）。
    /// </summary>
    public class RelicIconHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        private RelicDef _relic;

        public void Init(RelicDef relic)
        {
            _relic = relic;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_relic == null) return;
            string text = $"{_relic.displayName}\n{_relic.description}";
            // ⚠️ UI 元素世界坐标属 UICamera 系——必须传 canvas.worldCamera（主相机斜俯视投影错位）
            var canvas = GetComponentInParent<Canvas>();
            TooltipManager.Instance.Show(new TooltipViewData(text), transform.position,
                canvas != null ? canvas.worldCamera : Camera.main);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            TooltipManager.Instance.Hide();
        }

        // ⚠️ 空实现：图标若挂在按钮下（如 Btn_Relic 子级），消费点击阻止冒泡——点图标不触发父按钮（关列表）
        public void OnPointerClick(PointerEventData eventData) { }
    }
}
