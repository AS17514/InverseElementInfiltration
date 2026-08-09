using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>主菜单：标题 + 开始游戏。（纯代码构建 UI——测试阶段不依赖手搓 prefab）</summary>
    public class MainMenuPanel : PanelBase
    {
        public override string Key => "MainMenu";

        private void Awake()
        {
            // prefab 路径：CreateAsync 加载含完整布局的 prefab（AddComponent 后 Awake 触发）——有子节点则跳过代码构建（防双份 UI）
            if (transform.childCount == 0)
            {
                Build();
            }
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

            // 开始按钮
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

            btn.onClick.AddListener(OnStartClicked);
        }

        private void OnStartClicked()
        {
            // TODO: 接入爬塔地图（TowerFlow.EnterFloor(0)）——棋盘/地图面板就绪后接通
            Debug.Log("[MainMenu] 开始游戏（待接入爬塔地图）");
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
