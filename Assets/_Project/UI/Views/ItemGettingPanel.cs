using TheLaw.Core;
using TheLaw.Data;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 获取物品弹窗（2026-08-14：统一"获得遗物"提示——取代事件面板描述区追加）。
    /// 监听 RelicObtained → 填充（Img_Info 图标色块 + Txt_Name 名称 + Txt_Info 描述）→ PushOverlay 显示。
    /// 仅 Btn_Confirm 关闭（确认式提醒）。无图标资源时 Img_Info 隐藏（自动适配——用户预制体约定）。
    /// 节点：Img_Bg/Grp_/Grp_Info（Img_Info + Txt_Name + Txt_Info）/Grp_Btns/Btn_Confirm。
    /// </summary>
    public class ItemGettingPanel : PanelBase
    {
        public override string Key => "ItemGetting";

        private UIManager _uiManager;
        private Image _iconImg;      // Img_Info（物品图标——占位色块；无图标资源时隐藏）
        private TMP_Text _nameText;  // Txt_Name
        private TMP_Text _infoText;  // Txt_Info（描述）
        private Button _confirmBtn;  // Btn_Confirm（仅确认关闭）

        public void Init(UIManager uiManager)
        {
            _uiManager = uiManager;
        }

        private void Awake()
        {
            _iconImg = FindDeep(transform, "Img_Info")?.GetComponent<Image>();
            _nameText = FindDeep(transform, "Txt_Name")?.GetComponent<TMP_Text>();
            _infoText = FindDeep(transform, "Txt_Info")?.GetComponent<TMP_Text>();
            _confirmBtn = FindDeep(transform, "Btn_Confirm")?.GetComponent<Button>();
            if (_confirmBtn != null)
            {
                _confirmBtn.onClick.AddListener(OnConfirmClicked);
            }
            EventCenter.Instance.AddEventListener(GameEvent.RelicObtained, OnRelicObtained);
        }

        void OnDestroy()
        {
            EventCenter.Instance.RemoveEventListener(GameEvent.RelicObtained, OnRelicObtained);
            if (_confirmBtn != null) _confirmBtn.onClick.RemoveListener(OnConfirmClicked);
        }

        void OnRelicObtained(object data)
        {
            if (!(data is RelicDef relic)) return;
            // 填充内容
            if (_nameText != null) _nameText.text = relic.displayName;
            if (_infoText != null) _infoText.text = relic.description;
            if (_iconImg != null)
            {
                _iconImg.color = RelicTint(relic); // 占位色块（RelicDef 无图标资源——按 id 上色）
                _iconImg.gameObject.SetActive(true);
            }
            // 覆盖显示（不暂停——通知性质；确认关闭）
            if (_uiManager != null) _uiManager.PushOverlay(Key);
            else gameObject.SetActive(true);
        }

        protected override bool CloseOnBgClick => true; // 点背景 = 关闭（2026-08-14）

        void OnConfirmClicked()
        {
            if (_uiManager != null) _uiManager.PopOverlay();
            else gameObject.SetActive(false);
        }

        /// <summary>占位色块：按遗物 id 稳定取色（将来有图标资源替换为 sprite）——遗物列表/获取弹窗共用。</summary>
        public static Color RelicTint(RelicDef relic)
        {
            int h = Mathf.Abs(relic.GetInstanceID()) % 6;
            switch (h)
            {
                case 0: return new Color(0.95f, 0.75f, 0.25f); // 金
                case 1: return new Color(0.30f, 0.70f, 0.95f); // 蓝
                case 2: return new Color(0.40f, 0.85f, 0.45f); // 绿
                case 3: return new Color(0.95f, 0.45f, 0.45f); // 红
                case 4: return new Color(0.75f, 0.55f, 0.95f); // 紫
                default: return new Color(0.95f, 0.65f, 0.40f); // 橙
            }
        }

        /// <summary>递归按名查找（容错 prefab 层级嵌套）。</summary>
        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }
    }
}
