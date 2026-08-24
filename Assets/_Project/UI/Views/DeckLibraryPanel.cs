using System.Collections;
using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using TMPro;

namespace TheLaw.UI
{
    /// <summary>
    /// 牌库面板（2026-08-25）：抽牌区 + 弃牌堆（=墓地）只读快照展示。
    /// 卡片复用 Piece_Handcard（Addressables 按需加载），排列按战斗手牌同款手动布局（不用弹性布局——李毕约定，Grp_ 的 HorizontalLayoutGroup 只负责列容器排版，卡位由本脚本计算）。
    /// 数据源：GameState.DrawPile（抽牌堆）/ GameState.Discard.PieceDeaths（弃牌堆权威 Card 化——Graveyard 兼容双写，展示用 Discard）。
    /// 刷新：打开读一次快照；打开期间监听 PieceDied/PiecePromoted/HandChanged/StateChanged("mahjong-wall")。
    /// 关闭：Btn_Confirm / 点背景（CloseOnBgClick——PanelBase 自动加根 Button + Grp_ 透明阻挡，不误关）。
    /// </summary>
    public sealed class DeckLibraryPanel : PanelBase
    {
        public override string Key => "DeckLibrary";
        public override bool IsPausing => true; // 浏览牌库冻结世界（同设置/确认）

        UIManager _uiManager;
        GameState _state;

        RectTransform _drawContainer;    // Grp_Pile_Draw_（抽牌区卡容器）
        RectTransform _discardContainer; // Grp_Pile_Discard_（弃牌堆卡容器）
        Button _confirmButton;           // Btn_Confirm（关闭）
        bool _buyMode;                   // 代币购买模式（2026-08-24：点弃牌区牌购买 → 回调索引 → 关闭）
        System.Action<int> _onBuy;       // 购买回调（discardIndex——DiscardView 顺序）
        TMP_Text _titleText;             // Txt_Name（标题——购买模式改提示）
        string _defaultTitle;

        GameObject _cardTemplate;        // Piece_Handcard（Addressables 缓存——实例化后不依赖 handle）
        const string CardAddress = "Piece_Handcard";
        const float CardScale = 0.5f;
        const float Gap = 18f;

        public void Init(UIManager uiManager)
        {
            _uiManager = uiManager;
            _state = GameState.Instance;
        }

        protected override void OnShow()
        {
            base.OnShow();
            if (_state == null) _state = GameState.Instance;
            ResolveNodes();
            if (!_buyMode && _titleText != null && _defaultTitle != null && _titleText.text != _defaultTitle)
                _titleText.text = _defaultTitle; // 非购买模式：标题复原（购买关闭后下次普通浏览）
            EventCenter.Instance.AddEventListener(GameEvent.PieceDied, OnPileChanged);
            EventCenter.Instance.AddEventListener(GameEvent.PiecePromoted, OnPileChanged);
            EventCenter.Instance.AddEventListener(GameEvent.HandChanged, OnHandChanged);
            EventCenter.Instance.AddEventListener(GameEvent.StateChanged, OnStateChanged);
            StartCoroutine(RebuildPilesWhenReady());
        }

        protected override void OnHide()
        {
            EventCenter.Instance.RemoveEventListener(GameEvent.PieceDied, OnPileChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.PiecePromoted, OnPileChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.HandChanged, OnHandChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.StateChanged, OnStateChanged);
            base.OnHide();
        }

        void OnPileChanged(object data) => RebuildPiles();

        // 抽牌堆/手牌变化走 HandChanged（Resolver 统一牌区入口；null = 敌方侧变化，无需重建）
        void OnHandChanged(object data) { if (data != null) RebuildPiles(); }

        void OnStateChanged(object data) { if (data is string s && s == "mahjong-wall") RebuildPiles(); }

        void ResolveNodes()
        {
            if (_drawContainer != null) return;
            _drawContainer = FindDeep(transform, "Grp_Pile_Draw_") as RectTransform;
            _discardContainer = FindDeep(transform, "Grp_Pile_Discard_") as RectTransform;
            var btnGo = FindDeep(transform, "Btn_Confirm");
            if (btnGo != null) _confirmButton = btnGo.GetComponent<Button>();
            if (_confirmButton != null)
            {
                _confirmButton.onClick.RemoveAllListeners();
                _confirmButton.onClick.AddListener(Close);
            }
            _titleText = FindDeep(transform, "Txt_Name")?.GetComponent<TMP_Text>();
            if (_titleText != null && string.IsNullOrEmpty(_defaultTitle)) _defaultTitle = _titleText.text;
            if (_drawContainer == null || _discardContainer == null)
                Debug.LogWarning($"[DeckLibrary] 卡容器缺失：Draw={_drawContainer != null} Discard={_discardContainer != null}");
        }

        /// <summary>关闭 = overlay 弹栈（UIManager 负责 Hide + GamePause.Pop——不能直调 Hide，否则暂停计数泄漏）。</summary>
        void Close()
        {
            if (_buyMode)
            {
                _buyMode = false;
                _onBuy = null;
                if (_titleText != null && _defaultTitle != null) _titleText.text = _defaultTitle;
            }
            if (_uiManager != null) _uiManager.PopOverlay();
            else Hide();
        }

        /// <summary>代币购买模式（2026-08-24）：标题改提示 + 弃牌区牌可点击（点击=购买该牌 → 回调 discardIndex → 关闭）。</summary>
        public void EnterBuyMode(System.Action<int> onBuy)
        {
            _buyMode = true;
            _onBuy = onBuy;
            if (_titleText != null) _titleText.text = "选择要购买的牌";
        }

        void OnDiscardCardClicked(int discardIndex)
        {
            if (!_buyMode) return;
            var handler = _onBuy;
            Close(); // 先关闭（弹栈恢复）再回调——回调内发购买请求
            handler?.Invoke(discardIndex);
        }

        protected override void OnBgClicked() => Close(); // 点背景关闭（PanelBase 根 Button 回调）

        IEnumerator RebuildPilesWhenReady()
        {
            if (_cardTemplate == null)
            {
                var handle = Addressables.LoadAssetAsync<GameObject>(CardAddress);
                yield return handle;
                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    Debug.LogWarning("[DeckLibrary] 手牌卡 prefab 加载失败（address=Piece_Handcard）");
                    Addressables.Release(handle);
                    yield break;
                }
                _cardTemplate = handle.Result;
                Addressables.Release(handle); // 实例化后的卡不依赖 prefab handle（同 BattleController）
            }
            RebuildPiles();
        }

        void RebuildPiles()
        {
            if (_state == null || _cardTemplate == null) return;
            if (_drawContainer == null || _discardContainer == null) ResolveNodes();
            BuildPile(_drawContainer, _state.DrawPile, false);
            BuildPile(_discardContainer, _state.Discard.PieceDeaths, _buyMode); // 购买模式：弃牌区牌可点击（索引 = DiscardView 前段）
        }

        /// <summary>重建单个牌堆：清空 → 生成手牌卡（仅棋子牌——麻将表现留待玩法实现，同战斗手牌口径）→ 手动排列。</summary>
        void BuildPile(RectTransform container, IReadOnlyList<Card> cards, bool clickable = false)
        {
            if (container == null) return;
            // 同步销毁防同帧双份（2026-08-23 事件面板同款经验）
            foreach (Transform child in container) DestroyImmediate(child.gameObject);
            if (cards == null || cards.Count == 0) return;

            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (!card.IsPiece) continue;
                var def = ConfigTable.Find<PieceDef>(card.defId);
                if (def == null) continue;
                var data = PiecePresentationMapper.ToHandCard(
                    def,
                    _state.GetEffectiveType(def.Id),
                    _state.GetEffectiveValue(def.Id),
                    GetDisplayProgram(def) ?? new List<Template>());
                var view = UIComponentFactory.CreateHandCard(_cardTemplate, container, data);
                view.gameObject.name = $"PileCard_{def.Id}_{def.displayName}";
                view.gameObject.SetActive(true);
                if (clickable)
                {
                    var btn = view.gameObject.GetComponent<Button>();
                    if (btn == null) btn = view.gameObject.AddComponent<Button>();
                    btn.onClick.RemoveAllListeners();
                    int index = i; // 弃牌区行序 = DiscardView 前段（PieceDeaths 全棋子卡，无跳过偏移）
                    btn.onClick.AddListener(() => OnDiscardCardClicked(index));
                }
            }
            RefreshLayout(); // 容器尺寸由父布局驱动——先落布局再算卡位
            ArrangeCards(container);
        }

        /// <summary>战斗手牌同款手动排列（不用弹性布局）：按容器宽均分列、行换行、整体居中；锚点统一中心。</summary>
        void ArrangeCards(RectTransform container)
        {
            int count = container.childCount;
            if (count == 0) return;
            float w = TemplateWidth * CardScale;
            float h = TemplateHeight * CardScale;
            float usableW = Mathf.Max(240f, container.rect.width - 8f);
            int cols = Mathf.Max(1, Mathf.FloorToInt((usableW + Gap) / (w + Gap)));
            int rows = Mathf.CeilToInt(count / (float)cols);
            for (int i = 0; i < count; i++)
            {
                var rt = container.GetChild(i) as RectTransform;
                if (rt == null) continue;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                int row = i / cols;
                int col = i % cols;
                float x = (col - (cols - 1) * 0.5f) * (w + Gap);
                float y = (rows - 1) * 0.5f * (h + Gap) - row * (h + Gap);
                rt.anchoredPosition = new Vector2(x, y);
                rt.localScale = Vector3.one * CardScale;
            }
        }

        float TemplateWidth => _cardTemplate != null ? ((RectTransform)_cardTemplate.transform).rect.width : 160f;
        float TemplateHeight => _cardTemplate != null ? ((RectTransform)_cardTemplate.transform).rect.height : 230f;

        /// <summary>程序展示（编辑后取当前程序，否则默认程序集第一套槽）——与 DeckBuildPanel 同口径。</summary>
        List<Template> GetDisplayProgram(PieceDef def)
        {
            if (def == null) return null;
            if (_state != null && _state.TryGetCurrentProgram(def.Id, out var edited)) return edited;
            return def.programSet != null && def.programSet.Count > 0 ? def.programSet[0].slots : null;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }
    }
}
