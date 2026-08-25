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
        private bool _layoutFixed; // 退化 rect 兜底已执行（李毕拼好布局后不触发）

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
            EnsureTextRects(); // 2026-08-26：prefab 文本节点 0 尺寸兜底（Bind 时布局已定，可算父区尺寸）
            if (_titleText != null) _titleText.text = data?.Title ?? string.Empty;
            if (_infoText != null) _infoText.text = data?.Body ?? string.Empty;
        }

        /// <summary>prefab 未拼位兜底（测试反馈：Txt_Title/Txt_Info 尺寸 0 文字不可见）：
        /// 仅当文本 Rect 退化（锚点重合且尺寸≈0）时按父容器自动摆位；李毕拼好布局后不生效。</summary>
        void EnsureTextRects()
        {
            if (_layoutFixed) return;
            _layoutFixed = true;
            EnsureRect(_titleText, true);
            EnsureRect(_infoText, false);
        }

        void EnsureRect(TMP_Text txt, bool isTitle)
        {
            if (txt == null) return;
            var rt = txt.rectTransform;
            if (rt.anchorMin != rt.anchorMax) return; // 已拉伸布局，无需兜底
            if (rt.sizeDelta.sqrMagnitude > 1f) return; // 已有尺寸，无需兜底
            var parent = rt.parent as RectTransform;
            float w = parent != null && parent.rect.width > 1f ? parent.rect.width - 40f : 760f;
            float h = parent != null && parent.rect.height > 1f ? parent.rect.height : 480f;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            if (isTitle)
            {
                rt.anchoredPosition = new Vector2(0f, -12f);
                rt.sizeDelta = new Vector2(w, 56f);
                txt.alignment = TextAlignmentOptions.Center;
            }
            else
            {
                rt.anchoredPosition = new Vector2(0f, -84f);
                rt.sizeDelta = new Vector2(w, Mathf.Max(120f, h - 100f));
                txt.alignment = TextAlignmentOptions.TopLeft;
            }
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
