using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>战斗结算面板：胜利/失败文案 + 重新开始/返回主菜单。（prefab 为占位布局——文案由代码填充）</summary>
    public class BattleResultPanel : PanelBase
    {
        public override string Key => "BattleResult";

        public event Action OnRestartClicked;
        public event Action OnBackToMenuClicked;

        private void Awake()
        {
            Bind("Btn_NewGame", () => OnRestartClicked?.Invoke());
            Bind("Btn_QuitGame", () => OnBackToMenuClicked?.Invoke());
        }

        /// <summary>显示结算内容（胜负文案 + 按钮语义化）。</summary>
        public void ShowResult(bool victory)
        {
            SetText("Txt_Title", victory ? "胜利" : "失败");
            SetText("Txt_Subtitle", victory ? "敌方已全灭" : "我方溃败");
            SetButtonText("Btn_NewGame", "重新开始");
            SetButtonText("Btn_QuitGame", "返回主菜单");
            SetActive("Btn_ContinueGame", false);
            SetActive("Btn_Settings", false);
        }

        private void Bind(string buttonName, Action handler)
        {
            foreach (var b in GetComponentsInChildren<Button>(true))
            {
                if (b.name == buttonName)
                {
                    b.onClick.RemoveAllListeners();
                    b.onClick.AddListener(() => handler?.Invoke());
                    return;
                }
            }
        }

        private void SetText(string node, string text)
        {
            var t = transform.Find(node);
            var tmp = t != null ? t.GetComponent<TextMeshProUGUI>() : null;
            if (tmp != null) tmp.text = text;
        }

        private void SetButtonText(string button, string text)
        {
            foreach (var b in GetComponentsInChildren<Button>(true))
            {
                if (b.name == button)
                {
                    var tmp = b.GetComponentInChildren<TextMeshProUGUI>();
                    if (tmp != null) tmp.text = text;
                    return;
                }
            }
        }

        private void SetActive(string node, bool active)
        {
            var t = transform.Find(node);
            if (t != null) t.gameObject.SetActive(active);
        }
    }
}
