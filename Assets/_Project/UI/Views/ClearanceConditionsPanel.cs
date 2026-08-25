using TMPro;
using TheLaw.Core;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 通关条件面板（2026-08-26，李毕新拼版）：
    /// 节点：Txt_Title（标题）/ Txt_Info（正文）/ Grp_Btns/Btn_Confirm（确认关闭）；
    /// PushOverlay 暂停型——战斗开始自动弹 + 战斗中 Btn_ClearanceConditions 重看；
    /// 点背景/确认 = 关闭（PopOverlay）。
    /// </summary>
    public class ClearanceConditionsPanel : PanelBase
    {
        public override string Key => "ClearanceConditions";

        public override bool IsPausing => true; // 展示期间世界冻结（与 ConfirmPanel 一致）

        private UIManager _uiManager;
        private TMP_Text _titleText;
        private TMP_Text _infoText;
        private Button _confirmBtn;

        public void Init(UIManager uiManager)
        {
            _uiManager = uiManager;
        }

        private void Awake()
        {
            _titleText = FindDeepTxt("Txt_Title");
            _infoText = FindDeepTxt("Txt_Info");
            _confirmBtn = FindDeepBtn("Btn_Confirm");
            if (_confirmBtn != null) _confirmBtn.onClick.AddListener(OnConfirmClicked);
        }

        private void OnDestroy()
        {
            if (_confirmBtn != null) _confirmBtn.onClick.RemoveListener(OnConfirmClicked);
        }

        public void Bind(ClearanceViewData data)
        {
            if (_titleText != null) _titleText.text = data?.Title ?? string.Empty;
            if (_infoText != null) _infoText.text = data?.Body ?? string.Empty;
        }

        /// <summary>弹出（PushOverlay 覆盖显示 + 世界冻结）；确认/点背景关闭。</summary>
        public void Show(ClearanceViewData data)
        {
            Bind(data);
            if (_uiManager != null) _uiManager.PushOverlay(Key);
            else gameObject.SetActive(true); // 兜底（未注入——正常不会）
        }

        protected override bool CloseOnBgClick => true;

        protected override void OnBgClicked()
        {
            UiSfx.Play();
            Close();
        }

        private void OnConfirmClicked()
        {
            UiSfx.Play();
            Close();
        }

        private void Close()
        {
            if (_uiManager != null) _uiManager.PopOverlay();
            else gameObject.SetActive(false);
        }

        private TMP_Text FindDeepTxt(string nodeName)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == nodeName) return t.GetComponent<TMP_Text>();
            }
            return null;
        }

        private Button FindDeepBtn(string nodeName)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == nodeName) return t.GetComponent<Button>();
            }
            return null;
        }
    }
}
