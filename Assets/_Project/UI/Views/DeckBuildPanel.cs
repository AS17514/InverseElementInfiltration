using System.Collections;
using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 牌组构筑面板（DeckBuild 事件——"整备营地"）：从牌池选牌组成出战牌组，确认后经 Resolver.BuildDeck 落账。
    /// 布局（场景拼接，脚本只对接）：
    ///   Grp/Grp_PieceInfo                      —— 右侧信息区（选中棋子详情，同 PieceEditPanel）
    ///   Grp/Grp_BuildAndLimit/Grp_Build/Viewport/Content —— 出战列表（当前手牌；cell 180×212）
    ///   Grp/Grp_BuildAndLimit/Grp_DeckLimit    —— 构筑限制 tag 容器（数量/价值）
    ///   Grp/Grp_R/Grp_Pieces/Grp_PieceDisplay/Viewport/Content  —— 牌池列表（所有棋子；cell 150×150）
    ///   Grp/Grp_R/Grp_Low/Btn_Next             —— 确认按钮
    /// 交互：
    ///   点牌池卡 = 入队（Toggle isOn 切换）；点出战卡 = 出队（Button 点击）
    ///   限制校验（数量/总价值，来自当前事件 EventDefinition，0=无限制）——超限拒绝并回弹
    ///   确认 = Resolver.BuildDeck（唯一写入口，落账纪律）——失败返回 false 保持编辑态
    /// 完成 = 关面板 + EventCompleted（TowerFlow 推进——与 PieceEditPanel 同模式）
    /// </summary>
    public class DeckBuildPanel : PanelBase
    {
        public override string Key => "DeckBuild";

        private Resolver _resolver;
        private GameState _state;

        // ====== 节点引用 ======
        private Transform _poolContent;   // 牌池列表 Content（Grp_R/Grp_Pieces/Grp_PieceDisplay/Viewport/Content——所有棋子）
        private Transform _deckContent;   // 出战列表 Content（Grp_BuildAndLimit/Grp_Build/Viewport/Content——当前手牌）
        private Transform _limitRoot;     // 构筑限制 tag 容器（Grp_BuildAndLimit/Grp_DeckLimit）
        private TMP_Text _tagSize;        // 数量 tag（"数量 x/y"）
        private TMP_Text _tagValue;       // 价值 tag（"价值 x/y"）
        private TMP_Text _tagDuplicate;   // 可复数 tag
        private TMP_Text _tagPromote;     // 升变≤初始 tag（含实时计数）
        private Button _nextBtn;

        // ====== 信息区节点（同 PieceEditPanel——Grp_PieceInfo 直接挂 Grp 下）======
        private Transform _pieceInfo;
        private Transform _overlapDisplay;
        private Transform _nonOverlap;
        private Image[] _slotImages;   // Img_InfoProgram1~4（程序槽图标）
        private TMP_Text[] _slotTexts;
        private TMP_Text[] _slotDescs; // Txt_InfoProgram1~4Desc
        private Image _infoValueImg; private TMP_Text _infoValueText;
        private Image _infoTypeImg; private TMP_Text _infoTypeText;
        private TMP_Text _infoName; private Image _infoPortrait;

        // ====== 运行时状态 ======
        private List<PieceDef> _sortedDefs = new List<PieceDef>(); // 牌池数据（按 Id 排序——牌池卡顺序确定）
        private readonly List<int> _deck = new List<int>();        // 当前出战（defId 列表，顺序 = 入队顺序）
        private readonly List<GameObject> _deckCards = new List<GameObject>(); // 出战卡实例（按 _deck 同步）
        private readonly List<GameObject> _poolCards = new List<GameObject>(); // 牌池卡实例（索引 = _sortedDefs 索引）
        private GameObject _cardTemplate; // Piece_Card prefab（Addressables——牌池/出战共用）
        private GameObject _progTemplate; // Piece_ProgramInfo prefab（卡面程序槽图标——Addressables）

        // 当前事件限制（0 = 无限制）
        private int _deckSizeLimit;
        private int _valueLimit;
        private bool _allowDuplicate;        // 允许同种棋子复数编入（策划开关——左键加/右键减）
        private bool _promoteLimitByInitial; // 升变数量 ≤ 初始数量（策划开关）

        // ====== 注入（Bootstrap 创建面板后调用）======
        public void Init(Resolver resolver, GameState state)
        {
            _resolver = resolver;
            _state = state;
        }

        private void Awake()
        {
            ResolveNodes();
            // LoadLimits 不在此调用——Awake 先于 Init（_state 为 null）会 NRE；改到 OnShow（显示时必已 Init）
            StartCoroutine(LoadCardTemplate());
            // 确认按钮：构筑完成 → 落账 + 推进（失败保持编辑态）
            // ⚠️ 2026-08-15：PieceEdit prefab 已加 Grp_Btns 层——本面板同款防御（硬路径优先 + FindDeep 兜底）
            _nextBtn = transform.Find("Grp/Grp_R/Grp_Low/Btn_Next")?.GetComponent<Button>();
            if (_nextBtn == null)
            {
                var nextGo = FindDeep(transform, "Btn_Next");
                if (nextGo != null) _nextBtn = nextGo.GetComponent<Button>();
            }
            if (_nextBtn != null)
            {
                _nextBtn.onClick.RemoveAllListeners();
                _nextBtn.onClick.AddListener(OnConfirm);
            }
        }

        protected override void OnShow()
        {
            // 每次打开：出战清空（从零构筑——事件发生前手牌为全量，构筑 = 选新的出战）
            _deck.Clear();
            LoadLimits(); // 显示时 _state 必已 Init（Awake 时序问题：CreateAsync 回调后才 Init）
            RebuildPool();
            RebuildDeck();
            RefreshLimits();
            RefreshPoolAvailability(); // 初始刷新可选中性（空牌组时全可选——限制生效后按限制禁选）
            ClearPieceInfo(); // 初始化无选中棋子——Grp_PieceInfo 隐藏（悬停卡片才显示）
        }

        // ====== 节点解析 ======

        void ResolveNodes()
        {
            _poolContent = transform.Find("Grp/Grp_R/Grp_Pieces/Grp_PieceDisplay/Viewport/Content"); // 牌池（所有棋子）
            _deckContent = transform.Find("Grp/Grp_BuildAndLimit/Grp_Build/Viewport/Content");       // 出战（当前手牌）
            _limitRoot = transform.Find("Grp/Grp_BuildAndLimit/Grp_DeckLimit");

            _pieceInfo = transform.Find("Grp/Grp_PieceInfo");
            if (_pieceInfo == null) return;
            _overlapDisplay = _pieceInfo.Find("Grp_OverlapDisplay");
            _nonOverlap = _pieceInfo.Find("Grp_NonOverlapDisplay");

            _slotImages = new Image[4];
            _slotTexts = new TMP_Text[4];
            _slotDescs = new TMP_Text[4];
            for (int i = 0; i < 4; i++)
            {
                var img = _overlapDisplay?.Find($"Grp_InfoProgram/Img_InfoProgram{i + 1}");
                _slotImages[i] = img != null ? img.GetComponent<Image>() : null;
                _slotTexts[i] = img != null ? img.GetComponentInChildren<TMP_Text>() : null;
                var desc = _nonOverlap?.Find($"Grp_ProgramDesc/Txt_InfoProgram{i + 1}Desc");
                _slotDescs[i] = desc != null ? desc.GetComponent<TMP_Text>() : null;
            }
            var value = _overlapDisplay?.Find("Grp_InfoBase/Img_InfoValue");
            _infoValueImg = value != null ? value.GetComponent<Image>() : null;
            _infoValueText = value != null ? value.GetComponentInChildren<TMP_Text>() : null;
            var type = _overlapDisplay?.Find("Grp_InfoBase/Img_InfoType");
            _infoTypeImg = type != null ? type.GetComponent<Image>() : null;
            _infoTypeText = type != null ? type.GetComponentInChildren<TMP_Text>() : null;
            var name = _nonOverlap?.Find("Grp_PortraitNameDisplay/Txt_InfoName");
            _infoName = name != null ? name.GetComponent<TMP_Text>() : null;
            if (_infoName == null)
            {
                foreach (var t in GetComponentsInChildren<TMP_Text>(true))
                {
                    if (t.name == "Txt_InfoName") { _infoName = t; break; }
                }
            }
            var portrait = _nonOverlap?.Find("Grp_PortraitNameDisplay/Img_InfoPortrait");
            _infoPortrait = portrait != null ? portrait.GetComponent<Image>() : null;
        }

        /// <summary>构筑限制：当前事件 EventDefinition 的 deckSizeLimit/totalValueLimit（0=无限制）。判空防御（Init 前不应调用）。</summary>
        void LoadLimits()
        {
            _deckSizeLimit = 0;
            _valueLimit = 0;
            _allowDuplicate = false;
            _promoteLimitByInitial = false;
            if (_state == null) return; // Init 未注入（Awake 时序）——跳过，OnShow 时再调
            var ev = string.IsNullOrEmpty(_state.CurrentEventId)
                ? null
                : ConfigTable.FindByName<EventDefinition>(_state.CurrentEventId);
            if (ev == null)
            {
                // 2026-08-11 排查：无活动事件（CurrentEventId 空）→ 限制降级 0——不再静默，打日志暴露
                Debug.LogWarning($"[DeckBuild] 无活动事件（CurrentEventId='{_state.CurrentEventId}'）——构筑限制降级为 0；需从事件关进入构筑");
            }
            else
            {
                _deckSizeLimit = ev.deckSizeLimit;
                _valueLimit = ev.totalValueLimit;
                _allowDuplicate = ev.allowDuplicate;
                _promoteLimitByInitial = ev.promoteLimitByInitial;
            }
        }

        // ====== 牌池 ======

        /// <summary>牌池数据：类型优先（初始→部署→升变）+ 同类型价值升序（全场景统一排序——CardTypeColors.SortPieces）。</summary>
        List<PieceDef> GetSortedDefs()
        {
            var list = new List<PieceDef>(ConfigTable.All<PieceDef>());
            CardTypeColors.SortPieces(list);
            return list;
        }

        /// <summary>重建牌池：清空 → 按数据动态生成 Piece_Card（Toggle=入队标记）。</summary>
        void RebuildPool()
        {
            if (_poolContent == null || _cardTemplate == null)
            {
                StartCoroutine(RebuildPoolWhenReady());
                return;
            }
            foreach (Transform child in _poolContent) Destroy(child.gameObject);
            _poolCards.Clear();
            _sortedDefs = GetSortedDefs();
            for (int i = 0; i < _sortedDefs.Count; i++)
            {
                var def = _sortedDefs[i];
                var go = Instantiate(_cardTemplate, _poolContent);
                go.name = $"PoolCard_{def.Id}_{def.displayName}";
                FillCardData(go, def);
                // 悬停显示信息（CardHover——PointerEnter/PointerExit）
                var hover = go.GetComponent<CardHover>();
                if (hover == null) hover = go.AddComponent<CardHover>();
                hover.Init(this, def);
                // 左键加/右键减：模板自带 Toggle 会抢占左键切换语义——销毁 Toggle，改自定义 PointerClick
                var toggle = go.GetComponent<Toggle>();
                if (toggle != null) Destroy(toggle);
                var click = go.GetComponent<DeckCardClickHandler>();
                if (click == null) click = go.AddComponent<DeckCardClickHandler>();
                var defId = def.Id;
                click.onClick = eventData => OnPoolClicked(defId, eventData);
                _poolCards.Add(go);
            }
            // 牌池互斥/复数由 _deck 计数驱动，不依赖 ToggleGroup
        }

        System.Collections.IEnumerator RebuildPoolWhenReady()
        {
            int guard = 0;
            while (_cardTemplate == null && guard++ < 300) yield return null; // 防死等（大审查 H2：加载失败不再无限空等）
            if (_cardTemplate == null)
            {
                Debug.LogWarning("[DeckBuild] 卡面模板加载超时——跳过本次构建");
                yield break;
            }
            RebuildPool();
        }

        /// <summary>牌池卡点击：左键加 / 右键减。信息显示由悬停（CardHover）负责——点击不显示。</summary>
        void OnPoolClicked(int defId, PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                AddToDeck(defId);
            }
            else if (eventData.button == PointerEventData.InputButton.Right)
            {
                RemoveFromDeck(defId);
            }
        }

        /// <summary>左键：加入一张（超限拒绝，不闪烁）。</summary>
        void AddToDeck(int defId)
        {
            if (!CanAdd(defId))
            {
                Debug.LogWarning($"[DeckBuild] 无法加入棋子 {defId}——违反构筑限制");
                return;
            }
            _deck.Add(defId);
            RefreshAfterDeckChange();
        }

        /// <summary>右键：移出一张（可复数时逐张减）。</summary>
        void RemoveFromDeck(int defId)
        {
            if (!_deck.Remove(defId)) return; // 不在队：忽略
            RefreshAfterDeckChange();
        }

        void RefreshAfterDeckChange()
        {
            RebuildDeck();
            RefreshLimits();
            RefreshPoolAvailability(); // 限制变化 → 刷新剩余棋子的可选中性/数量角标
        }

        /// <summary>
        /// 刷新牌池卡可加性（2026-08-11：超限/单卡超限 → 置灰禁选；2026-08-16 改左加右减）：
        /// - 未入队且不能加 → 半透明
        /// - 已入队保持正常（可右键减少；可复数时还可左键再加）
        /// - 入队数量看中间出战列表（同种相邻展示）
        /// </summary>
        void RefreshPoolAvailability()
        {
            if (_sortedDefs == null || _poolCards == null) return;
            for (int i = 0; i < _sortedDefs.Count && i < _poolCards.Count; i++)
            {
                var card = _poolCards[i];
                if (card == null) continue;
                var def = _sortedDefs[i];
                int count = CountInDeck(def.Id);
                bool canAdd = CanAdd(def.Id);
                SetCardBlocked(card, count == 0 && !canAdd); // 未入队且不能加 → 灰；已入队/可减保持正常
            }
        }

        /// <summary>限制校验（数量/总价值/复数/升变≤初始——与 Resolver.BuildDeck 同规则，UI 提前拦截）。</summary>
        bool CanAdd(int defId)
        {
            // 单枚模式：已在队不能再加（左键加第二张被拦）
            if (!_allowDuplicate && CountInDeck(defId) > 0)
            {
                return false;
            }
            if (_deckSizeLimit > 0 && _deck.Count >= _deckSizeLimit)
            {
                return false;
            }
            if (_valueLimit > 0)
            {
                int total = GetDeckTotalValue();
                int value = GetEffectiveValue(defId);
                if (total + value > _valueLimit)
                {
                    return false;
                }
            }
            if (_promoteLimitByInitial && GetEffectiveType(defId) == PieceType.Promoted)
            {
                int initialCount = CountByEffectiveType(PieceType.Initial);
                int promotedCount = CountByEffectiveType(PieceType.Promoted);
                if (promotedCount + 1 > initialCount)
                {
                    return false;
                }
            }
            return true;
        }

        int GetDeckTotalValue()
        {
            int total = 0;
            foreach (var id in _deck) total += GetEffectiveValue(id);
            return total;
        }

        int CountInDeck(int defId)
        {
            int count = 0;
            foreach (var id in _deck) if (id == defId) count++;
            return count;
        }

        int CountByEffectiveType(PieceType type)
        {
            int count = 0;
            foreach (var id in _deck)
            {
                if (GetEffectiveType(id) == type) count++;
            }
            return count;
        }

        int GetEffectiveValue(int defId)
        {
            if (_state != null) return _state.GetEffectiveValue(defId);
            return ConfigTable.Find<PieceDef>(defId)?.value ?? 0;
        }

        PieceType GetEffectiveType(int defId)
        {
            if (_state != null) return _state.GetEffectiveType(defId);
            var def = ConfigTable.Find<PieceDef>(defId);
            return def != null ? def.pieceType : PieceType.Initial;
        }

        void SetCardBlocked(GameObject card, bool blocked)
        {
            if (card == null) return;
            var cg = card.GetComponent<CanvasGroup>();
            if (cg == null) cg = card.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = blocked ? 0.5f : 1f;
        }

        // ====== 出战 ======

        /// <summary>重建出战列表：清空 → 按 _deck 生成（点击 = 出队）。</summary>
        void RebuildDeck()
        {
            if (_deckContent == null) return;
            foreach (var card in _deckCards)
            {
                if (card != null) Destroy(card);
            }
            _deckCards.Clear();
            var displayOrder = GetDeckDisplayOrder();
            foreach (var deckId in displayOrder)
            {
                var def = ConfigTable.Find<PieceDef>(deckId);
                if (def == null || _cardTemplate == null) continue;
                var go = Instantiate(_cardTemplate, _deckContent);
                go.name = $"DeckCard_{def.Id}_{def.displayName}";
                FillCardData(go, def);
                // 悬停显示信息（CardHover——PointerEnter/PointerExit）
                var hover = go.GetComponent<CardHover>();
                if (hover == null) hover = go.AddComponent<CardHover>();
                hover.Init(this, def);
                // 出战卡点击 = 出队。⚠️ 根节点已有 Toggle（Selectable 子类）——AddComponent<Button> 返回 null（实测），
                // 复用 Toggle 的 onValueChanged：isOn 置 true → 出队 + 回弹 false（玩家点击切换）
                var toggle = go.GetComponent<Toggle>();
                if (toggle != null)
                {
                    var defId = def.Id;
                    toggle.onValueChanged.RemoveAllListeners();
                    toggle.onValueChanged.AddListener(on =>
                    {
                        if (on)
                        {
                            OnDeckCardClick(defId, go);
                            toggle.isOn = false; // 回弹（点击 = 瞬时出队，不留选中态）
                        }
                    });
                }
                _deckCards.Add(go);
            }
        }

        /// <summary>出战展示顺序：同种棋子相邻（按首次入队顺序分组，保持不同种类相对顺序）。</summary>
        IEnumerable<int> GetDeckDisplayOrder()
        {
            var groups = new List<int>();
            var seen = new HashSet<int>();
            foreach (var id in _deck)
            {
                if (seen.Add(id)) groups.Add(id);
            }
            foreach (var groupId in groups)
            {
                foreach (var id in _deck)
                {
                    if (id == groupId) yield return id;
                }
            }
        }

        /// <summary>出战卡点击：出队（等价右键减）。信息显示由悬停（CardHover）负责。</summary>
        void OnDeckCardClick(int defId, GameObject card)
        {
            RemoveFromDeck(defId);
        }

        // ====== 限制显示 ======

        /// <summary>刷新数量/价值/复数/升变 tag（模板复制或代码创建——见 EnsureTags）。</summary>
        void RefreshLimits()
        {
            EnsureTags();
            int total = GetDeckTotalValue();
            if (_tagSize != null)
                _tagSize.text = _deckSizeLimit > 0 ? $"数量 {_deck.Count}/{_deckSizeLimit}" : $"数量 {_deck.Count}";
            if (_tagValue != null)
                _tagValue.text = _valueLimit > 0 ? $"价值 {total}/{_valueLimit}" : $"价值 {total}";
            if (_tagDuplicate != null)
                _tagDuplicate.text = _allowDuplicate ? "可复数" : "";
            if (_tagPromote != null)
            {
                if (_promoteLimitByInitial)
                {
                    int initial = CountByEffectiveType(PieceType.Initial);
                    int promoted = CountByEffectiveType(PieceType.Promoted);
                    _tagPromote.text = $"初始 {initial}｜升变 {promoted}（升变≤初始）";
                }
                else
                {
                    _tagPromote.text = "";
                }
            }
        }

        /// <summary>
        /// 构筑限制 tag：优先用 Grp_DeckLimit 下已有实例作模板（复制多份）；无模板则代码创建。
        /// 注：Tag_DeckLimit 预制体未注册 Addressables，运行时无法按地址加载——双保险。
        /// </summary>
        void EnsureTags()
        {
            if (_limitRoot == null) return;
            if (_tagSize != null && _tagValue != null && _tagDuplicate != null && _tagPromote != null) return;
            var template = _limitRoot.childCount > 0 ? _limitRoot.GetChild(0).gameObject : null;
            var templateTxt = template != null
                ? template.GetComponent<TMP_Text>() ?? template.GetComponentInChildren<TMP_Text>()
                : null;

            if (_tagSize == null)
            {
                if (templateTxt != null)
                {
                    _tagSize = templateTxt;
                    template.name = "Tag_DeckSize";
                }
                else
                {
                    _tagSize = CreateTagText("Tag_DeckSize");
                }
            }
            if (_tagValue == null)
            {
                if (templateTxt != null)
                {
                    var clone = Instantiate(template, _limitRoot);
                    clone.name = "Tag_DeckValue";
                    _tagValue = clone.GetComponent<TMP_Text>() ?? clone.GetComponentInChildren<TMP_Text>();
                }
                else
                {
                    _tagValue = CreateTagText("Tag_DeckValue");
                }
            }
            if (_tagDuplicate == null)
            {
                if (templateTxt != null)
                {
                    var clone = Instantiate(template, _limitRoot);
                    clone.name = "Tag_DeckDuplicate";
                    _tagDuplicate = clone.GetComponent<TMP_Text>() ?? clone.GetComponentInChildren<TMP_Text>();
                }
                else
                {
                    _tagDuplicate = CreateTagText("Tag_DeckDuplicate");
                }
            }
            if (_tagPromote == null)
            {
                if (templateTxt != null)
                {
                    var clone = Instantiate(template, _limitRoot);
                    clone.name = "Tag_DeckPromote";
                    _tagPromote = clone.GetComponent<TMP_Text>() ?? clone.GetComponentInChildren<TMP_Text>();
                }
                else
                {
                    _tagPromote = CreateTagText("Tag_DeckPromote");
                }
            }
        }

        TMP_Text CreateTagText(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(_limitRoot, false);
            var txt = go.GetComponent<TextMeshProUGUI>();
            txt.fontSize = 36;
            txt.color = Color.white;
            txt.alignment = TextAlignmentOptions.Center;
            txt.text = "";
            return txt;
        }

        // ====== 确认 ======

        void OnConfirm()
        {
            if (_resolver == null) return;
            if (_deck.Count == 0)
            {
                Debug.LogWarning("[DeckBuild] 出战牌组为空——无法确认");
                return;
            }
            if (_resolver.BuildDeck(new List<int>(_deck)))
            {
                gameObject.SetActive(false);
                EventCenter.Instance.EventTrigger(GameEvent.EventCompleted, _state != null ? _state.CurrentEventId : null); // 推进（携带事件 id——TowerFlow 校验匹配；防重复信号跳节点）
            }
            else
            {
                Debug.LogWarning("[DeckBuild] 构筑校验失败（规则层拒绝）——保持编辑态");
            }
        }

        // ====== 卡片数据填充（Piece_Card：Bg/Portrait/BaseInfo(类型·足迹·价值)/ProgramInfo）======

        /// <summary>填充牌池/出战卡数据。程序 = 编辑差异优先（CurrentPrograms），回退 Def 默认模组。</summary>
        void FillCardData(GameObject card, PieceDef def)
        {
            var bg = card.GetComponent<Image>();
            if (bg != null) bg.color = CardTypeColors.For(GetEffectiveType(def.Id));
            var valueText = FindDeep(card.transform, "Img_PieceValue")?.GetComponentInChildren<TMP_Text>();
            if (valueText != null) valueText.text = GetEffectiveValue(def.Id).ToString();
            var typeText = FindDeep(card.transform, "Img_PieceType")?.GetComponentInChildren<TMP_Text>();
            if (typeText != null)
            {
                typeText.text = GetEffectiveType(def.Id) == PieceType.Initial ? "始" : GetEffectiveType(def.Id) == PieceType.Deployable ? "部" : "升";
            }
            // 程序槽图标（Grp_PieceProgramInfo 内 Piece_ProgramInfo——编辑差异优先，2026-08-11 数据链修复）
            var progRoot = FindDeep(card.transform, "Grp_PieceProgramInfo");
            if (progRoot != null && _progTemplate != null)
            {
                List<Template> slots = null;
                if (_state != null && _state.TryGetCurrentProgram(def.Id, out var edited)) slots = edited;
                else if (def.programSet != null && def.programSet.Count > 0) slots = def.programSet[0].slots;
                int count = slots != null ? Mathf.Min(slots.Count, 4) : 0;
                // 复用已有图标，超出补建（卡片重建时已清空子物体——此处幂等）
                int existing = progRoot.childCount;
                for (int k = existing; k < count; k++) Instantiate(_progTemplate, progRoot);
                int i = 0;
                foreach (Transform p in progRoot)
                {
                    bool show = i < count;
                    if (p.gameObject.activeSelf != show) p.gameObject.SetActive(show);
                    if (show && slots != null)
                    {
                        var t = p.GetComponentInChildren<TMP_Text>();
                        if (t != null) t.text = SlotTypeChar(slots[i]);
                    }
                    i++;
                }
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

        // ====== 信息区（同 PieceEditPanel 逻辑——选中棋子详情）======

        /// <summary>悬停显示棋子信息（CardHover 调用——跨类所以 internal）。</summary>
        internal void ShowPieceInfo(PieceDef def)
        {
            if (_infoName != null) _infoName.text = VerticalName(def.displayName);
            if (_infoValueText != null) _infoValueText.text = GetEffectiveValue(def.Id).ToString();
            if (_infoTypeText != null)
            {
                _infoTypeText.text = GetEffectiveType(def.Id) == PieceType.Initial ? "始" : GetEffectiveType(def.Id) == PieceType.Deployable ? "部" : "升";
            }
            // 程序 = 编辑差异优先（CurrentPrograms——编辑结果在此），回退 Def 默认模组（2026-08-11 数据链修复）
            List<Template> slots = null;
            if (_state != null && _state.TryGetCurrentProgram(def.Id, out var edited)) slots = edited;
            else if (def.programSet != null && def.programSet.Count > 0) slots = def.programSet[0].slots;
            int slotCount = slots != null ? Mathf.Min(slots.Count, 4) : 0;
            for (int i = 0; i < 4; i++)
            {
                bool has = i < slotCount;
                if (_slotImages[i] != null) _slotImages[i].gameObject.SetActive(has);
                if (_slotDescs[i] != null) _slotDescs[i].gameObject.SetActive(has);
                if (has)
                {
                    var t = slots[i];
                    if (_slotTexts[i] != null) _slotTexts[i].text = SlotTypeChar(t);
                    if (_slotDescs[i] != null) _slotDescs[i].text = SlotDetailDesc(t);
                }
            }
            if (_pieceInfo != null)
            {
                var infoImg = _pieceInfo.GetComponent<Image>();
                if (infoImg != null)
                {
                    var c = CardTypeColors.For(GetEffectiveType(def.Id));
                    infoImg.color = new Color(c.r, c.g, c.b, 0.45f);
                }
                _pieceInfo.gameObject.SetActive(true);
            }
        }

        /// <summary>隐藏信息区（CardHover 调用——跨类所以 internal）。</summary>
        internal void ClearPieceInfo()
        {
            if (_pieceInfo != null) _pieceInfo.gameObject.SetActive(false);
            for (int i = 0; i < 4; i++)
            {
                if (_slotImages[i] != null) _slotImages[i].gameObject.SetActive(false);
                if (_slotDescs[i] != null) _slotDescs[i].gameObject.SetActive(false);
            }
        }

        static string SlotTypeChar(Template t)
        {
            switch (t)
            {
                case MoveTemplate: return "移";
                case AttackTemplate: return "攻";
                default: return "跳";
            }
        }

        static string SlotDetailDesc(Template t)
        {
            var mapped = SlotDescTable.Get(t);
            if (mapped != null) return mapped;
            switch (t)
            {
                case MoveTemplate: return "移：移动";
                case AttackTemplate: return "攻：攻击";
                default: return "跳：跳过";
            }
        }

        static string VerticalName(string name)
        {
            return string.Join("\n", name.ToCharArray());
        }

        // ====== Piece_Card 模板（Addressables——与预制体同名）======

        System.Collections.IEnumerator LoadCardTemplate()
        {
            var handle = Addressables.LoadAssetAsync<GameObject>("Piece_Card");
            var progHandle = Addressables.LoadAssetAsync<GameObject>("Piece_ProgramInfo"); // 卡面程序槽图标模板
            yield return handle;
            yield return progHandle;
            if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && handle.Result != null)
            {
                _cardTemplate = handle.Result;
            }
            else
            {
                Debug.LogWarning("[DeckBuild] Piece_Card 加载失败——牌池/出战列表为空");
            }
            if (progHandle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && progHandle.Result != null)
            {
                _progTemplate = progHandle.Result;
            }
            // 重建统一由 OnShow（模板未就绪时 RebuildPoolWhenReady 等待）驱动——此处不重复触发
        }

        /// <summary>牌池卡左/右键处理组件（Toggle 销毁后的替代：左键加、右键减）。</summary>
        public class DeckCardClickHandler : MonoBehaviour, IPointerClickHandler
        {
            public System.Action<PointerEventData> onClick;

            public void OnPointerClick(PointerEventData eventData)
            {
                if (onClick != null) onClick(eventData);
            }
        }
    }

    /// <summary>
    /// 卡片悬停显示信息（牌池/出战卡通用）：PointerEnter → ShowPieceInfo；PointerExit → ClearPieceInfo。
    /// 2026-08-11 需求：信息显示时机 = 光标覆盖卡片，而非点击。
    /// </summary>
    public class CardHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private DeckBuildPanel _panel;
        private PieceDef _def;

        public void Init(DeckBuildPanel panel, PieceDef def)
        {
            _panel = panel;
            _def = def;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_panel != null && _def != null) _panel.ShowPieceInfo(_def);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (_panel != null) _panel.ClearPieceInfo();
        }
    }
}
