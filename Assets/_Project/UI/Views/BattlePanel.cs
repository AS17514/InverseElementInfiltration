using System;
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

        // 设置按钮（Bootstrap 订阅 → PushOverlay("Settings")——面板只转发输入）
        public event Action OnSettingsClicked;

        public Button PhaseButton { get; private set; }
        public Button ExitButton { get; private set; }
        public TMP_Text PhaseButtonText { get; private set; }
        public TMP_Text APValueText { get; private set; }
        public TMP_Text EventNameText { get; private set; }
        public Button DrawButton { get; private set; }
        public TMP_Text DrawPileCountText { get; private set; }
        public TMP_Text DrawCostText { get; private set; }
        public Slider TurnProgressSlider { get; private set; } // Sld_TurnProgress（回合进度条）
        public RectTransform WaveNodesRoot { get; private set; } // Grp_WaveNodes（波次节点容器）
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
            BindSettingsButton();
            if (PhaseButton != null)
            {
                // 按钮文本子节点未命名——直接找子级 TMP
                var tmp = PhaseButton.GetComponentInChildren<TMP_Text>();
                if (tmp != null) PhaseButtonText = tmp;
            }
            APValueText = transform.Find("Grp_AP/Txt_APValue")?.GetComponent<TMP_Text>();
            DrawButton = (transform.Find("Grp_DrawPile/Btn_Draw") ?? FindDeep("Btn_Draw"))?.GetComponent<Button>();
            DrawPileCountText = (transform.Find("Grp_DrawPile/Txt_DrawPileCount") ?? FindDeep("Txt_DrawPileCount"))?.GetComponent<TMP_Text>();
            DrawCostText = (transform.Find("Grp_DrawPile/Txt_DrawCost") ?? FindDeep("Txt_DrawCost"))?.GetComponent<TMP_Text>();
            if (DrawCostText != null) DrawCostText.text = "1 AP";
            EventNameText = transform.Find("Grp_TopBar/Txt_EventName")?.GetComponent<TMP_Text>();
            if (EventNameText == null)
            {
                // 兜底：深层按名查找（防路径漂移）
                foreach (var t in GetComponentsInChildren<TMP_Text>(true))
                {
                    if (t.name == "Txt_EventName") { EventNameText = t; break; }
                }
            }
            HandRoot = transform.Find("Grp_Hand") as RectTransform;
            // 回合进度（2026-08-12：进度条 + 波次节点；Txt_CurrentProgress 是关卡名不动）
            TurnProgressSlider = transform.Find("Grp_TopBar/Grp_M/Sld_TurnProgress")?.GetComponent<Slider>();
            WaveNodesRoot = transform.Find("Grp_TopBar/Grp_M/Grp_WaveNodes") as RectTransform;

            // 手牌模板（Grp_Hand 下名为 Piece_Handcard 的节点，保留作克隆模板）
            var template = transform.Find("Grp_Hand/Piece_Handcard");
            if (template != null) HandCardTemplate = template.gameObject;

            if (PhaseButton == null || APValueText == null || HandRoot == null)
            {
                Debug.LogWarning($"[BattlePanel] 节点引用缺失：Btn={PhaseButton != null} AP={APValueText != null} Hand={HandRoot != null} EventName={(EventNameText != null)}");
            }
            Debug.Log($"[BattlePanel] 节点解析：Btn={PhaseButton != null} AP={APValueText != null} Hand={HandRoot != null} EventName={(EventNameText != null)} Draw={(DrawButton != null)} DrawCount={(DrawPileCountText != null)}");
        }

        /// <summary>设置按钮可能在任何分组下——按名搜全层级绑定（Bootstrap 订阅事件打开 Settings overlay）。</summary>
        Transform FindDeep(string nodeName)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == nodeName) return t;
            }
            return null;
        }

        void BindSettingsButton()
        {
            Button btn = null;
            foreach (var b in GetComponentsInChildren<Button>(true))
            {
                if (b.name == "Btn_Settings") { btn = b; break; }
            }
            if (btn == null)
            {
                Debug.LogWarning("[BattlePanel] 未找到设置按钮 Btn_Settings");
                return;
            }
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => OnSettingsClicked?.Invoke());
        }

        public void SetDrawPile(int remaining, bool interactable)
        {
            if (DrawCostText != null) DrawCostText.text = "1 AP";
            if (DrawPileCountText != null) DrawPileCountText.text = $"剩余 {Mathf.Max(0, remaining)}";
            if (DrawButton != null) DrawButton.interactable = interactable;
        }

        public void SetAP(int current, int max)
        {
            if (APValueText != null) APValueText.text = $"{current}/{max}";
        }

        public void SetEventName(string name)
        {
            if (EventNameText != null) EventNameText.text = name;
        }

        /// <summary>进度条值（0~1）。</summary>
        public void SetTurnProgress(float value)
        {
            if (TurnProgressSlider != null) TurnProgressSlider.value = value;
        }
    }
}
