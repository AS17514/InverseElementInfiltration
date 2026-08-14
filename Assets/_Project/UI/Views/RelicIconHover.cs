using TheLaw.Data;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TheLaw.UI
{
    /// <summary>
    /// 遗物图标 hover 描述（2026-08-14）：鼠标悬停 → TooltipManager 显示遗物名称+描述；移开隐藏。
    /// 挂 Image.prefab 实例（遗物列表 Grp_RelicDisplay 内）。
    /// </summary>
    public class RelicIconHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
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
            TooltipManager.Instance.Show(text, transform.position); // 世界坐标（UI 元素——用其世界位置）
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            TooltipManager.Instance.Hide();
        }
    }
}
