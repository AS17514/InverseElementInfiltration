using TheLaw.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 战斗结算面板（overlay：模态覆盖 + 遮罩——战斗外的纯展示层）。
    /// 收到 StateChanged(GameOver+winner) 时【立即快照】展示数据（Reset 1 帧后清空 GameState——确认时再读会丢数据）。
    /// 确认按钮只关面板，不触发任何后端逻辑（后端在 EndBattle 已全部落账，无等待项）。
    /// </summary>
    public class BattleResultPanel : PanelBase
    {
        public override string Key => "BattleResult";

        // ====== 快照数据（收到信号时立即保存——防 Reset 清空） ======
        private bool _snapshotted;
        private bool _victory;
        private int _score;
        private int _turnCount;

        private TMP_Text _titleText;
        private TMP_Text _detailText;
        private Button _confirmBtn;

        private void Awake()
        {
            // 纯代码构建 overlay（prefab 占位布局不适用——测试期，后续可换 prefab）
            var bg = gameObject.AddComponent<Image>();
            bg.color = new Color(0, 0, 0, 0.7f); // 全屏遮罩
            var rt = (RectTransform)transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            // 结算卡片
            var card = new GameObject("Card", typeof(RectTransform), typeof(Image));
            card.transform.SetParent(transform, false);
            var cardRt = (RectTransform)card.transform;
            cardRt.sizeDelta = new Vector2(600, 360);
            card.GetComponent<Image>().color = new Color(0.15f, 0.2f, 0.3f, 0.95f);

            var titleGo = new GameObject("Txt_Title", typeof(RectTransform), typeof(TextMeshProUGUI));
            titleGo.transform.SetParent(card.transform, false);
            _titleText = titleGo.GetComponent<TextMeshProUGUI>();
            _titleText.fontSize = 72;
            _titleText.alignment = TextAlignmentOptions.Center;
            ((RectTransform)titleGo.transform).anchoredPosition = new Vector2(0, 80);
            ((RectTransform)titleGo.transform).sizeDelta = new Vector2(500, 100);

            var detailGo = new GameObject("Txt_Detail", typeof(RectTransform), typeof(TextMeshProUGUI));
            detailGo.transform.SetParent(card.transform, false);
            _detailText = detailGo.GetComponent<TextMeshProUGUI>();
            _detailText.fontSize = 32;
            _detailText.alignment = TextAlignmentOptions.Center;
            ((RectTransform)detailGo.transform).anchoredPosition = new Vector2(0, -10);
            ((RectTransform)detailGo.transform).sizeDelta = new Vector2(500, 60);

            var btnGo = new GameObject("Btn_Confirm", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGo.transform.SetParent(card.transform, false);
            var btnRt = (RectTransform)btnGo.transform;
            btnRt.sizeDelta = new Vector2(220, 70);
            btnRt.anchoredPosition = new Vector2(0, -120);
            btnGo.GetComponent<Image>().color = new Color(0.25f, 0.45f, 0.7f, 1f);
            _confirmBtn = btnGo.GetComponent<Button>();
            _confirmBtn.targetGraphic = btnGo.GetComponent<Image>();
            var btnTxtGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            btnTxtGo.transform.SetParent(btnGo.transform, false);
            var btnTxt = btnTxtGo.GetComponent<TextMeshProUGUI>();
            btnTxt.text = "确认";
            btnTxt.fontSize = 32;
            btnTxt.alignment = TextAlignmentOptions.Center;
            ((RectTransform)btnTxtGo.transform).sizeDelta = Vector2.zero;
            _confirmBtn.onClick.AddListener(() => gameObject.SetActive(false)); // 只关面板——不触发任何后端逻辑
        }

        /// <summary>快照并展示结算（收到 StateChanged(GameOver+winner) 时立即调用——防 Reset 清空数据）。</summary>
        public void ShowResult(bool victory, int score, int turnCount)
        {
            _snapshotted = true;
            _victory = victory;
            _score = score;
            _turnCount = turnCount;
            Refresh();
            gameObject.SetActive(true);
        }

        void Refresh()
        {
            if (!_snapshotted) return;
            if (_titleText != null)
            {
                _titleText.text = _victory ? "胜利" : "失败";
                _titleText.color = _victory ? new Color(0.9f, 0.85f, 0.4f) : new Color(0.85f, 0.4f, 0.4f);
            }
            if (_detailText != null)
            {
                _detailText.text = $"得分 {_score}    回合 {_turnCount}";
            }
        }
    }
}
