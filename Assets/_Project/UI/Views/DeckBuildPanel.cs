using System.Collections;
using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
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
        private readonly List<Toggle> _poolToggles = new List<Toggle>(); // 牌池卡 Toggle（索引 = _sortedDefs 索引）
        private GameObject _cardTemplate; // Piece_Card prefab（Addressables——牌池/出战共用）

        // 当前事件限制（0 = 无限制）
        private int _deckSizeLimit;
        private int _valueLimit;

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
            _nextBtn = transform.Find("Grp/Grp_R/Grp_Low/Btn_Next")?.GetComponent<Button>();
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
            if (_state == null) return; // Init 未注入（Awake 时序）——跳过，OnShow 时再调
            var ev = string.IsNullOrEmpty(_state.CurrentEventId)
                ? null
                : ConfigTable.FindByName<EventDefinition>(_state.CurrentEventId);
            if (ev != null)
            {
                _deckSizeLimit = ev.deckSizeLimit;
                _valueLimit = ev.totalValueLimit;
            }
        }

        // ====== 牌池 ======

        /// <summary>牌池数据：全部棋子按 Id 排序（卡片顺序稳定，与摆放顺序无关）。</summary>
        List<PieceDef> GetSortedDefs()
        {
            var list = new List<PieceDef>(ConfigTable.All<PieceDef>());
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
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
            _poolToggles.Clear();
            _sortedDefs = GetSortedDefs();
            for (int i = 0; i < _sortedDefs.Count; i++)
            {
                var def = _sortedDefs[i];
                var go = Instantiate(_cardTemplate, _poolContent);
                go.name = $"PoolCard_{def.Id}_{def.displayName}";
                FillCardData(go, def);
                var toggle = go.GetComponent<Toggle>();
                if (toggle != null)
                {
                    var defId = def.Id;
                    toggle.onValueChanged.RemoveAllListeners();
                    toggle.onValueChanged.AddListener(on => OnPoolToggle(defId, on));
                    _poolToggles.Add(toggle);
                }
            }
            // 牌池 Toggle 互斥自管：不挂 ToggleGroup（isOn 即"已入队"标记，多选语义）
        }

        System.Collections.IEnumerator RebuildPoolWhenReady()
        {
            while (_cardTemplate == null) yield return null;
            RebuildPool();
        }

        /// <summary>牌池卡点击：on=true 入队（超限拒绝回弹）；on=false 出队。</summary>
        void OnPoolToggle(int defId, bool on)
        {
            if (on)
            {
                if (!CanAdd(defId)) return; // 超限：Toggle 已置 true——手动回弹
                _deck.Add(defId);
            }
            else
            {
                _deck.Remove(defId);
            }
            RebuildDeck();
            RefreshLimits();
            if (on)
            {
                var def = ConfigTable.Find<PieceDef>(defId);
                if (def != null) ShowPieceInfo(def);
            }
            else
            {
                ClearPieceInfo();
            }
        }

        /// <summary>限制校验（数量/总价值——与 Resolver.BuildDeck 同规则，UI 提前拦截）。</summary>
        bool CanAdd(int defId)
        {
            if (_deck.Contains(defId)) return true; // 已在队：出队而非入队（Toggle 回弹场景）
            if (_deckSizeLimit > 0 && _deck.Count >= _deckSizeLimit)
            {
                Debug.LogWarning($"[DeckBuild] 牌组数量已达上限 {_deckSizeLimit}");
                return false;
            }
            if (_valueLimit > 0)
            {
                var def = ConfigTable.Find<PieceDef>(defId);
                if (def != null)
                {
                    int total = 0;
                    foreach (var id in _deck) total += ConfigTable.Find<PieceDef>(id).value;
                    if (total + def.value > _valueLimit)
                    {
                        Debug.LogWarning($"[DeckBuild] 总价值超限（{total}+{def.value} > {_valueLimit}）");
                        return false;
                    }
                }
            }
            return true;
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
            for (int i = 0; i < _deck.Count; i++)
            {
                var def = ConfigTable.Find<PieceDef>(_deck[i]);
                if (def == null || _cardTemplate == null) continue;
                var go = Instantiate(_cardTemplate, _deckContent);
                go.name = $"DeckCard_{def.Id}_{def.displayName}";
                FillCardData(go, def);
                // 出战卡点击 = 出队（Button 组件，targetGraphic=自身 Image）
                var img = go.GetComponent<Image>();
                var btn = go.AddComponent<Button>();
                btn.targetGraphic = img;
                var defId = def.Id;
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => OnDeckCardClick(defId, go));
                _deckCards.Add(go);
            }
        }

        /// <summary>出战卡点击：出队 + 牌池对应 Toggle 回弹（isOn=false）。</summary>
        void OnDeckCardClick(int defId, GameObject card)
        {
            _deck.Remove(defId);
            // 牌池卡 Toggle 回弹（索引 = defId 在 _sortedDefs 中的位置）
            for (int i = 0; i < _sortedDefs.Count && i < _poolToggles.Count; i++)
            {
                if (_sortedDefs[i].Id == defId && _poolToggles[i] != null && _poolToggles[i].isOn)
                {
                    _poolToggles[i].isOn = false;
                    break;
                }
            }
            RebuildDeck();
            RefreshLimits();
            ClearPieceInfo();
        }

        // ====== 限制显示 ======

        /// <summary>刷新数量/价值 tag（模板复制或代码创建——见 EnsureTags）。</summary>
        void RefreshLimits()
        {
            EnsureTags();
            int total = 0;
            foreach (var id in _deck) total += ConfigTable.Find<PieceDef>(id).value;
            if (_tagSize != null)
                _tagSize.text = _deckSizeLimit > 0 ? $"数量 {_deck.Count}/{_deckSizeLimit}" : $"数量 {_deck.Count}";
            if (_tagValue != null)
                _tagValue.text = _valueLimit > 0 ? $"价值 {total}/{_valueLimit}" : $"价值 {total}";
        }

        /// <summary>
        /// 构筑限制 tag：优先用 Grp_DeckLimit 下已有实例作模板（复制一份）；无模板则代码创建两个 TMP。
        /// 注：Tag_DeckLimit 预制体未注册 Addressables，运行时无法按地址加载——双保险。
        /// </summary>
        void EnsureTags()
        {
            if (_tagSize != null && _tagValue != null) return;
            if (_limitRoot == null) return;
            if (_tagSize == null)
            {
                var template = _limitRoot.childCount > 0 ? _limitRoot.GetChild(0).gameObject : null;
                if (template != null)
                {
                    _tagSize = template.GetComponent<TMP_Text>();
                    if (_tagSize == null) _tagSize = template.GetComponentInChildren<TMP_Text>();
                    var clone = Instantiate(template, _limitRoot);
                    clone.name = "Tag_DeckValue";
                    _tagValue = clone.GetComponent<TMP_Text>();
                    if (_tagValue == null) _tagValue = clone.GetComponentInChildren<TMP_Text>();
                    template.name = "Tag_DeckSize";
                }
            }
            // 无模板兜底：代码创建（样式从简——测试期可接受）
            if (_tagSize == null) _tagSize = CreateTagText("Tag_DeckSize");
            if (_tagValue == null) _tagValue = CreateTagText("Tag_DeckValue");
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
                EventCenter.Instance.EventTrigger(GameEvent.EventCompleted); // 推进（TowerFlow 监听）
            }
            else
            {
                Debug.LogWarning("[DeckBuild] 构筑校验失败（规则层拒绝）——保持编辑态");
            }
        }

        // ====== 卡片数据填充（Piece_Card：Bg/Portrait/BaseInfo(类型·足迹·价值)/ProgramInfo）======

        static void FillCardData(GameObject card, PieceDef def)
        {
            var bg = card.GetComponent<Image>();
            if (bg != null) bg.color = CardTypeColors.For(def.pieceType);
            var valueText = FindDeep(card.transform, "Img_PieceValue")?.GetComponentInChildren<TMP_Text>();
            if (valueText != null) valueText.text = def.value.ToString();
            var typeText = FindDeep(card.transform, "Img_PieceType")?.GetComponentInChildren<TMP_Text>();
            if (typeText != null)
            {
                typeText.text = def.pieceType == PieceType.Initial ? "始" : def.pieceType == PieceType.Deployable ? "部" : "升";
            }
            // 立绘/足迹/程序块图标：资源与结构后续补充（先占位——不填不报错）
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

        void ShowPieceInfo(PieceDef def)
        {
            if (_infoName != null) _infoName.text = VerticalName(def.displayName);
            if (_infoValueText != null) _infoValueText.text = def.value.ToString();
            if (_infoTypeText != null)
            {
                _infoTypeText.text = def.pieceType == PieceType.Initial ? "始" : def.pieceType == PieceType.Deployable ? "部" : "升";
            }
            var slots = def.programSet != null && def.programSet.Count > 0 ? def.programSet[0].slots : null;
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
                    var c = CardTypeColors.For(def.pieceType);
                    infoImg.color = new Color(c.r, c.g, c.b, 0.45f);
                }
                _pieceInfo.gameObject.SetActive(true);
            }
        }

        void ClearPieceInfo()
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
            yield return handle;
            if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && handle.Result != null)
            {
                _cardTemplate = handle.Result;
            }
            else
            {
                Debug.LogWarning("[DeckBuild] Piece_Card 加载失败——牌池/出战列表为空");
            }
            // 重建统一由 OnShow（模板未就绪时 RebuildPoolWhenReady 等待）驱动——此处不重复触发
        }
    }
}
