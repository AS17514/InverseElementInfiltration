using System.Collections.Generic;
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

        /// <summary>弹窗是否正在展示（2026-08-25：EventPanel 据此延迟下个事件显示——确认后才切换）。</summary>
        public static bool IsShowing { get; private set; }

        private UIManager _uiManager;
        private Image _iconImg;      // Img_Info（物品图标——占位色块；无图标资源时隐藏）
        private TMP_Text _nameText;  // Txt_Name
        private TMP_Text _infoText;  // Txt_Info（描述）
        private Button _confirmBtn;  // Btn_Confirm（仅确认关闭）
        private readonly Queue<RelicDef> _pendingRelics = new Queue<RelicDef>(); // 待展示队列（连续获得多遗物逐个确认——不叠栈）
        private bool _showing;        // 是否正在展示（队列消费中）

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
            IsShowing = false; // 销毁兜底（防静态标记卡死延迟）
        }

        void OnRelicObtained(object data)
        {
            if (!(data is RelicDef relic)) return;
            _pendingRelics.Enqueue(relic); // 入队（连续获得多遗物——逐个确认，不叠栈覆盖）
            if (!_showing) ShowNext();
        }

        /// <summary>纯展示绑定；队列与 Overlay 生命周期仍由 Panel 管理。</summary>
        public void Bind(ItemGettingViewData data)
        {
            // 2026-08-26：全角＋ 字体缺失——显示层归一化为半角 +（数据域 JSON 未动）
            if (_nameText != null) _nameText.text = NormalizePlus(data?.Name);
            if (_infoText != null) _infoText.text = NormalizePlus(data?.Description);
            if (_iconImg != null)
            {
                _iconImg.color = data != null ? data.IconColor : Color.white;
                _iconImg.gameObject.SetActive(data != null && data.ShowIcon);
            }
        }

        static ItemGettingViewData ToViewData(RelicDef relic)
        {
            return new ItemGettingViewData(relic.displayName, relic.description, RelicTint(relic));
        }

        /// <summary>展示队列头部遗物（消费式）：绑定 DTO 后 PushOverlay。</summary>
        void ShowNext()
        {
            if (_pendingRelics.Count == 0) return;
            _showing = true;
            IsShowing = true; // 弹窗展示中——下个事件显示延迟
            Bind(ToViewData(_pendingRelics.Dequeue()));
            // 覆盖显示（不暂停——通知性质；确认关闭）
            if (_uiManager != null) _uiManager.PushOverlay(Key);
            else gameObject.SetActive(true);
        }

        protected override bool CloseOnBgClick => true; // 点背景 = 关闭（2026-08-14）

        /// <summary>背景点击 = 确认关闭（与 Btn_Confirm 同语义——消费队列，避免叠栈残留）。</summary>
        protected override void OnBgClicked()
        {
            OnConfirmClicked();
        }

        void OnConfirmClicked()
        {
            UiSfx.Play(); // 遗物获得确认碰撞音（2026-08-24 音频挂点方案；面板出现音由 PanelBase.Show 覆盖）
            if (_pendingRelics.Count > 0)
            {
                // 队列还有下一个遗物——直接切换内容（Overlay 保持显示，不 Pop 再 Push 避免闪跳）
                _showing = true; // 保持展示态
                Bind(ToViewData(_pendingRelics.Dequeue()));
            }
            else
            {
                _showing = false;
                IsShowing = false; // 队列清空 + 关闭——下个事件可显示
                if (_uiManager != null) _uiManager.PopOverlay();
                else gameObject.SetActive(false);
            }
        }

        /// <summary>占位色块：按遗物配置 Id（GameConfigBase._id）稳定取色（将来有图标资源替换为 sprite）——遗物列表/获取弹窗共用。</summary>
        public static Color RelicTint(RelicDef relic)
        {
            // ⚠️ 用配置 Id（稳定，跨会话/重载不变）——GetInstanceID 是 ScriptableObject 实例 id，域重载/重新导入资产会漂移，颜色不稳定
            int h = Mathf.Abs(relic.Id) % 6;
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

        /// <summary>全角＋ → 半角 +（2026-08-26：字体缺全角加号字形——显示层统一归一化）。</summary>
        static string NormalizePlus(string s)
        {
            return s == null ? string.Empty : s.Replace('＋', '+');
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
