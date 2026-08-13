using System;
using TheLaw.Core;
using TheLaw.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 通用确认面板（UI 架构重构：常驻缓存 + 暂停型 overlay）：
    /// ShowConfirm(message, onConfirm) → PushOverlay 盖住当前界面（IsPausing=true——世界冻结）
    /// → 玩家确认/取消 → PopOverlay 恢复下层。所有确认场景复用（撤回全部/退出/重开…）。
    /// 节点：Img_Bg/Grp_/Txt_Info（信息文本）、Grp_Btns/Btn_Confirm、Btn_Cancel。
    /// </summary>
    public class ConfirmPanel : PanelBase
    {
        public override string Key => "Confirm";

        public override bool IsPausing => true; // 确认时世界冻结（暂停机制 §四）

        private UIManager _uiManager;
        private TMP_Text _infoText;
        private Button _confirmBtn;
        private Button _cancelBtn;
        private Action _onConfirm; // 确认回调（一次性——Close 后清）

        public void Init(UIManager uiManager)
        {
            _uiManager = uiManager;
        }

        private void Awake()
        {
            // 二级嵌套（Img_Bg/Grp_）——FindDeep 容错
            _infoText = FindDeep(transform, "Txt_Info")?.GetComponent<TMP_Text>();
            _confirmBtn = FindDeep(transform, "Btn_Confirm")?.GetComponent<Button>();
            _cancelBtn = FindDeep(transform, "Btn_Cancel")?.GetComponent<Button>();
            if (_confirmBtn != null) _confirmBtn.onClick.AddListener(OnConfirmClicked);
            if (_cancelBtn != null) _cancelBtn.onClick.AddListener(OnCancelClicked);
        }

        void OnDestroy()
        {
            if (_confirmBtn != null) _confirmBtn.onClick.RemoveListener(OnConfirmClicked);
            if (_cancelBtn != null) _cancelBtn.onClick.RemoveListener(OnCancelClicked);
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

        /// <summary>弹出确认（覆盖显示 + 世界冻结）；确认后执行 onConfirm 并关闭。</summary>
        public void ShowConfirm(string message, Action onConfirm)
        {
            _onConfirm = onConfirm;
            if (_infoText != null) _infoText.text = message;
            if (_uiManager != null) _uiManager.PushOverlay(Key);
            else gameObject.SetActive(true); // 兜底（未注入——正常不会）
        }

        void OnConfirmClicked()
        {
            var cb = _onConfirm;
            Close();
            cb?.Invoke(); // 先关再回调（防回调内再弹确认面板的时序）
        }

        void OnCancelClicked()
        {
            Close();
        }

        void Close()
        {
            _onConfirm = null;
            if (_uiManager != null) _uiManager.PopOverlay();
            else gameObject.SetActive(false);
        }
    }
}
