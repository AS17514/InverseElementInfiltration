using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>主菜单：标题 + 开始/继续/设置/退出。（prefab 布局优先，代码构建兜底——按钮点击一律转发事件）</summary>
    public class MainMenuPanel : PanelBase
    {
        public override string Key => "MainMenu";

        // 按钮事件（Bootstrap 订阅响应——面板只转发输入，不持有规则层引用）
        public event Action OnNewGameClicked;
        public event Action OnContinueClicked;
        public event Action OnSettingsClicked;
        public event Action OnQuitClicked;

        private void Awake()
        {
            // prefab 路径：CreateAsync 加载含完整布局的 prefab（AddComponent 后 Awake 触发）——有子节点则跳过代码构建（防双份 UI）
            if (transform.childCount == 0)
            {
                Build();
            }
            BindButtons();
        }

        private void BindButtons()
        {
            Bind("Btn_NewGame", OnNewGameClicked);
            Bind("Btn_ContinueGame", OnContinueClicked);
            Bind("Btn_Settings", OnSettingsClicked);
            Bind("Btn_QuitGame", OnQuitClicked);
        }

        private void Bind(string buttonName, Action handler)
        {
            Button btn = null;
            foreach (var b in GetComponentsInChildren<Button>(true))
            {
                if (b.name == buttonName) { btn = b; break; } // 按钮可能在分组子级下（如 Grp_MenuOptions/Btn_NewGame）
            }
            if (btn == null)
            {
                Debug.LogWarning($"[MainMenu] 未找到按钮 {buttonName}");
                return;
            }
            btn.onClick.RemoveAllListeners(); // 防重复绑定（面板重建）
            btn.onClick.AddListener(() => { Debug.Log($"[MainMenu] 点击 {buttonName}"); handler?.Invoke(); });
            Debug.Log($"[MainMenu] 绑定按钮 {buttonName}");
        }

        private void Build()
        {
            // 标题
            var titleGo = new GameObject("Title", typeof(RectTransform));
            titleGo.transform.SetParent(transform, false);
            var title = titleGo.AddComponent<TextMeshProUGUI>();
            title.text = "逆元渗透";
            title.fontSize = 96;
            title.alignment = TextAlignmentOptions.Center;
            title.color = Color.white;
            Stretch(titleGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0.65f), new Vector2(0.5f, 0.65f), new Vector2(0, 0), new Vector2(1200, 160));

            // 开始按钮（代码版兜底——prefab 路径不会走到这）
            var btnGo = new GameObject("StartButton", typeof(RectTransform));
            btnGo.transform.SetParent(transform, false);
            var img = btnGo.AddComponent<Image>();
            img.color = new Color(0.2f, 0.5f, 0.9f);
            var btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = img;
            Stretch(btnGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0.4f), new Vector2(0.5f, 0.4f), new Vector2(0, 0), new Vector2(320, 90));

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(btnGo.transform, false);
            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = "开始游戏";
            label.fontSize = 36;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            Stretch(labelGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            btn.onClick.AddListener(() => OnNewGameClicked?.Invoke());
        }

        private static void Stretch(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPos, Vector2 size)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
        }
    }
}
