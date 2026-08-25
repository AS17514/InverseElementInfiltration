using TheLaw.Core;
using TheLaw.Data;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 玩法详情弹窗（2026-08-26：Grp_Mode 介绍按钮 → PushOverlay 显示玩法介绍；参考 ItemGettingPanel 交互——仅确认关闭、点背景关闭）。
    /// 节点契约（与 ItemGettingPanel 同构）：Img_Bg / Grp_ / Grp_Info（Txt_Name + Txt_Info）/ Grp_Btns / Btn_Confirm。
    /// 玩法名 = DisplayNames.OfStyle；介绍文案 = GetDescription（源：Assets/test/docs/玩法文本.docx——2026-08-26 初稿，待文案确认）。
    /// </summary>
    public class FloorPlayDetailePanel : PanelBase
    {
        public override string Key => "FloorPlayDetaile";

        private UIManager _uiManager;
        private TMP_Text _nameText;   // Txt_Name
        private TMP_Text _infoText;   // Txt_Info
        private Button _confirmBtn;   // Btn_Confirm（仅确认关闭）

        public void Init(UIManager uiManager)
        {
            _uiManager = uiManager;
        }

        private void Awake()
        {
            _nameText = FindDeep(transform, "Txt_Name")?.GetComponent<TMP_Text>();
            _infoText = FindDeep(transform, "Txt_Info")?.GetComponent<TMP_Text>();
            _confirmBtn = FindDeep(transform, "Btn_Confirm")?.GetComponent<Button>();
            if (_confirmBtn != null)
            {
                _confirmBtn.onClick.AddListener(OnConfirmClicked);
            }
        }

        private void OnDestroy()
        {
            if (_confirmBtn != null) _confirmBtn.onClick.RemoveListener(OnConfirmClicked);
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

        /// <summary>填充玩法名 + 介绍文本（PushOverlay 前调用）。</summary>
        public void Bind(string name, string description)
        {
            if (_nameText != null) _nameText.text = name ?? string.Empty;
            if (_infoText != null) _infoText.text = description ?? string.Empty;
        }

        protected override bool CloseOnBgClick => true; // 点背景 = 关闭（与 Btn_Confirm 同语义）

        protected override void OnBgClicked()
        {
            OnConfirmClicked();
        }

        void OnConfirmClicked()
        {
            UiSfx.Play(); // 碰撞音（2026-08-24 音频挂点方案）
            if (_uiManager != null) _uiManager.PopOverlay();
            else gameObject.SetActive(false);
        }

        /// <summary>玩法介绍文案（源 = Assets/test/docs/玩法文本.docx——初稿，待文案确认）。</summary>
        public static string GetDescription(string styleId)
        {
            switch (styleId)
            {
                case StyleRegistry.Mahjong:
                    return "选择此玩法时，玩家将获得 1-9 各两张、总计 18 张的「麻将」牌。\n\n"
                        + "「麻将」在手牌中时可以执行「摸切」：将手牌中的此牌填入牌山，并抽一张牌。\n"
                        + "「麻将」牌不视为棋子，其价值将作为自身的点数。\n"
                        + "「麻将」牌可以通过消耗行动点打出，作为「墙体」在棋盘上任意非敌方部署区 1x2 竖向空格部署。部署在场上的「麻将」受到任意攻击时（无论是哪一格）都会被破坏。\n"
                        + "「麻将」被破坏时也会填入牌山，此时基础得分+1。\n\n附加规则：\n"
                        + "对方的棋子被击败或己方部署的「麻将」牌时，此牌价值的数字将由左至右地填充至牌山，填入牌山的数字超出两个时，先填入牌山的数字将被移出。\n"
                        + "若填入的数字与原来牌山中的数字能组成刻子（三个相同的数字）或顺子（三个连续的数字），则移出牌山的所有数字，并让番数+1。\n"
                        + "和牌：当手牌存在雀头（两个相同价值的牌）且番数不为 0 时，可以花费一点行动点和牌，本回合的倍率增加番数，并让番数清零。";
                case StyleRegistry.Element:
                    return "选择此玩法时，开局游戏中双方所有的棋子都会从金、木、水、火、土五种属性中随机获得一种属性（表现为棋子或牌周围对应颜色的光圈）。\n"
                        + "五种属性按下图相生相克，依据相生相克的关系，棋子获得以下特性：\n\n相克：\n"
                        + "若棋子的攻击攻击到其克制属性的目标，则击败目标（无论目标是否拥有护盾或抗性），基础得分+棋盘上与攻击棋子属性相同棋子的数量。\n"
                        + "升变时，若被升变的棋子被升变棋子的属性克制，则倍率+1。\n\n相生：\n"
                        + "若棋子的攻击攻击到其相生属性的目标，则不会对目标造成任何伤害（场上的「麻将」也不会被破坏），并获得一个目标的复制牌加入手牌（属性相同）。\n"
                        + "升变时，若被升变的棋子被升变棋子的属性相生，则获得一个被升变的棋子的复制牌加入手牌（属性相同）。";
                case StyleRegistry.Dice:
                    return "选择此玩法时，玩法机制区新增：骰子。\n"
                        + "玩家可以消耗一点行动点进行一次骰子的投掷，基础得分+骰子的点数。\n"
                        + "此骰子的点数将持续保留直到下一次投掷骰子；或当玩家进行棋子的行动时，可以不消耗行动点而是消耗骰子的点数，进行一次骰子点数的直线移动（必须能够走到终点）。\n"
                        + "当玩家部署骰子点数的棋子时，倍率+1。";
                case StyleRegistry.Go:
                    return "选择此玩法时，游戏开始时将获得一张「棋子」（不占用初始手牌数）。\n"
                        + "「棋子」牌也视为棋子，但「棋子」不可以行动，也不可以被用来升变。\n"
                        + "部署「棋子」不消耗行动点，且一回合只能部署一次，棋子可以被部署在棋盘上任意空格，受到对方的伤害也会退场。\n"
                        + "「棋子」首次部署为蓝色，之后每次部署将切换一次颜色，蓝色视为我方棋子，红色视为敌方棋子。\n"
                        + "部署「棋子」后，若棋子颜色那一方的棋子围住了另一方的棋子，则将被围住的棋子击败，并使倍率+1。";
                case StyleRegistry.Token:
                    return "选择此玩法时，玩法机制区新增：代币篮。\n"
                        + "每局游戏玩家的初始代币为 0，每回合开始时获得一个代币。\n"
                        + "回合中玩家可以点击购买，消耗棋子价值数的代币来获得一张弃牌区的棋子，并让基础得分+消耗的代币数。\n"
                        + "（若初始棋子回到手牌，可以不消耗行动点将其部署在我方部署区）。";
                default:
                    Debug.LogWarning($"[FloorPlayDetaile] 未知玩法 {styleId}");
                    return styleId;
            }
        }
    }
}
