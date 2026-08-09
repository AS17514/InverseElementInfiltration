using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 战斗面板：引用关键节点（按命名 Find 定位，运行时挂到 prefab）。
    /// 阶段按钮三形态：结束准备 / 结束回合 / 敌方回合中（灰置）。
    /// </summary>
    public class BattlePanel : PanelBase
    {
        public override string Key => "Battle";

        public Button PhaseButton { get; private set; }
        public Button ExitButton { get; private set; }
        public TMP_Text PhaseButtonText { get; private set; }
        public TMP_Text APValueText { get; private set; }
        public TMP_Text EventNameText { get; private set; }
        public RectTransform HandRoot { get; private set; }
        public GameObject HandCardTemplate { get; private set; }

        protected override void OnShow()
        {
            if (PhaseButton == null) ResolveNodes();
        }

        void ResolveNodes()
        {
            PhaseButton = transform.Find("Btn_PhaseAction")?.GetComponent<Button>();
            ExitButton = transform.Find("Btn_Exit")?.GetComponent<Button>();
            if (PhaseButton != null)
            {
                // 按钮文本子节点未命名——直接找子级 TMP
                var tmp = PhaseButton.GetComponentInChildren<TMP_Text>();
                if (tmp != null) PhaseButtonText = tmp;
            }
            APValueText = transform.Find("Grp_AP/Txt_APValue")?.GetComponent<TMP_Text>();
            EventNameText = transform.Find("Grp_TopBar/Txt_EventName")?.GetComponent<TMP_Text>();
            if (EventNameText == null)
            {
                // 兜底：深层按名查找（防路径漂移）
                foreach (var t in GetComponentsInChildren<TMP_Text>(true))
                {
                    if (t.name == "Txt_EventName") { EventNameText = t; break; }
                }
            }
            // 隐藏静态"当前进度"标签（代码不更新它——防被误认为阶段状态文本）
            var progress = transform.Find("Grp_TopBar/Txt_CurrentProgress");
            if (progress != null) progress.gameObject.SetActive(false);
            HandRoot = transform.Find("Grp_Hand") as RectTransform;

            // 手牌模板（Grp_Hand 下名为 Piece_Handcard 的节点，保留作克隆模板）
            var template = transform.Find("Grp_Hand/Piece_Handcard");
            if (template != null) HandCardTemplate = template.gameObject;

            if (PhaseButton == null || APValueText == null || HandRoot == null)
            {
                Debug.LogWarning($"[BattlePanel] 节点引用缺失：Btn={PhaseButton != null} AP={APValueText != null} Hand={HandRoot != null} EventName={(EventNameText != null)}");
            }
            Debug.Log($"[BattlePanel] 节点解析：Btn={PhaseButton != null} AP={APValueText != null} Hand={HandRoot != null} EventName={(EventNameText != null)}");
        }

        public void SetAP(int current, int max)
        {
            if (APValueText != null) APValueText.text = $"{current}/{max}";
        }

        public void SetEventName(string name)
        {
            if (EventNameText != null) EventNameText.text = name;
        }
    }
}
