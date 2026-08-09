using System.Collections.Generic;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 棋子编辑面板（程序编排）：左列棋子选择 → 中列程序库拖模板 → 右侧槽位替换/移除。
    /// 编辑语义：按 defId（种类级）替换整份 4 槽程序（EditorSession.EditProgram，经 Resolver 写种类级表）。
    /// 槽位状态：locked 模板灰色 + 禁拖（一版数据无锁，全部可编辑——结构支持）。
    /// 排序：默认不排序（替换只改目标槽），EnableSlotReorder 参数开启自动排列。
    /// </summary>
    public class PieceEditPanel : PanelBase
    {
        public override string Key => "PieceEdit";

        /// <summary>编辑完成（Btn_Next——下一步进战斗）。</summary>
        public event System.Action OnNextClicked;

        [Header("编辑行为")]
        [SerializeField] private bool _enableSlotReorder; // 排序开关（一版默认关：替换只改目标槽）

        // ====== 节点引用 ======
        private Transform _pieceContent;   // 左列棋子列表 Content
        private Transform _programContent; // 中列程序库 Content
        private Transform _pieceInfo;      // 右侧信息区（Grp_PieceInfo）
        private Transform _overlapDisplay; // 右侧叠加显示（Grp_OverlapDisplay）
        private Transform _nonOverlap;     // 右侧文字显示（Grp_NonOverlapDisplay）
        private Image[] _slotImages;       // Img_InfoProgram1~4
        private TMP_Text[] _slotTexts;     // 槽位内文字（移/攻/跳）
        private TMP_Text[] _slotDescs;     // Txt_InfoProgram1~4Desc
        private Image _infoValueImg; private TMP_Text _infoValueText;
        private Image _infoTypeImg; private TMP_Text _infoTypeText;
        private TMP_Text _infoName; private Image _infoPortrait;

        // ====== 运行时状态 ======
        private EditorSession _editor;
        private GameState _state;
        private int _selectedDefId = -1;
        private List<Template> _slotTemplates = new List<Template>(); // 当前选中棋子的程序（编辑副本）
        private bool[] _slotLocked = new bool[4];                       // 槽位锁定标记（一版全 false）

        // ====== 程序库（全局模板去重） ======
        private List<Template> _programLibrary = new List<Template>();

        public void Init(EditorSession editor, GameState state)
        {
            _editor = editor;
            _state = state;
        }

        private void Awake()
        {
            ResolveNodes();
            BuildProgramLibrary();
            RefreshPieceList();
            RefreshProgramList();
            // Btn_Next：编辑完成 → 下一步（Bootstrap 接线进战斗）
            var next = transform.Find("Grp/Grp_L/Grp_Top/Btn_Next")?.GetComponent<Button>();
            if (next != null)
            {
                next.onClick.RemoveAllListeners();
                next.onClick.AddListener(() => OnNextClicked?.Invoke());
            }
        }

        protected override void OnShow()
        {
            // 新局重置：清选中 + 隐藏信息区
            _selectedDefId = -1;
            _slotTemplates.Clear();
            if (_pieceInfo != null) _pieceInfo.gameObject.SetActive(false);
        }

        void ResolveNodes()
        {
            _pieceContent = transform.Find("Grp/Grp_L/Grp_Pieces/Grp_PieceDisplay/Viewport/Content");
            _programContent = transform.Find("Grp/Grp_L/Grp_Programs/Grp_ProgramDisplay/Viewport/Content");
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
            var portrait = _nonOverlap?.Find("Grp_PortraitNameDisplay/Img_InfoPortrait");
            _infoPortrait = portrait != null ? portrait.GetComponent<Image>() : null;
        }

        // ====== 程序库（所有棋子 programSet 模板去重——全局模板库） ======
        void BuildProgramLibrary()
        {
            _programLibrary.Clear();
            var seen = new HashSet<string>();
            foreach (var def in ConfigTable.All<PieceDef>())
            {
                foreach (var prog in def.programSet)
                {
                    foreach (var slot in prog.slots)
                    {
                        var key = SlotDescTable.FeatureOf(slot);
                        if (seen.Add(key)) _programLibrary.Add(slot);
                    }
                }
            }
        }

        // ====== 左列：棋子列表 ======
        void RefreshPieceList()
        {
            if (_pieceContent == null) return;
            foreach (Transform child in _pieceContent) Destroy(child.gameObject);
            foreach (var def in ConfigTable.All<PieceDef>())
            {
                var card = CreatePieceCard(def);
                var btn = card.GetComponent<Button>();
                if (btn == null) btn = card.AddComponent<Button>();
                var defId = def.Id;
                btn.onClick.AddListener(() => SelectPiece(defId));
            }
        }

        GameObject CreatePieceCard(PieceDef def)
        {
            // 一版纯代码创建（后续换 Piece_Card prefab——Addressables 加载 + FillCard 填参）
            var go = new GameObject($"PieceCard_{def.name}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_pieceContent, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(150, 150);
            var img = go.GetComponent<Image>();
            img.color = new Color(0.9f, 0.9f, 0.9f, 1f);
            // 名称文本（临时——正式用 prefab Piece_Card）
            var txtGo = new GameObject("Txt", typeof(RectTransform), typeof(TextMeshProUGUI));
            txtGo.transform.SetParent(go.transform, false);
            var txt = txtGo.GetComponent<TextMeshProUGUI>();
            txt.text = def.displayName;
            txt.fontSize = 20;
            txt.alignment = TextAlignmentOptions.Center;
            ((RectTransform)txtGo.transform).anchoredPosition = Vector2.zero;
            return go;
        }

        // ====== 中列：程序库 ======
        void RefreshProgramList()
        {
            if (_programContent == null) return;
            foreach (Transform child in _programContent) Destroy(child.gameObject);
            foreach (var slot in _programLibrary)
            {
                var go = new GameObject($"Prog_{SlotDescTable.FeatureOf(slot)}", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(_programContent, false);
                var rt = (RectTransform)go.transform;
                rt.sizeDelta = new Vector2(200, 60);
                go.GetComponent<Image>().color = new Color(0.75f, 0.75f, 0.75f, 1f);
                var txtGo = new GameObject("Desc", typeof(RectTransform), typeof(TextMeshProUGUI));
                txtGo.transform.SetParent(go.transform, false);
                var txt = txtGo.GetComponent<TextMeshProUGUI>();
                txt.text = SlotDetailDescStatic(slot);
                txt.fontSize = 20;
                txt.alignment = TextAlignmentOptions.Left;
                ((RectTransform)txtGo.transform).sizeDelta = new Vector2(190, 50);
                ((RectTransform)txtGo.transform).anchoredPosition = Vector2.zero;
                // 拖拽源：程序块 → 槽位
                var drag = go.AddComponent<EditorProgramDrag>();
                drag.Init(this, slot);
            }
        }
        // ====== 选中棋子 ======
        void SelectPiece(int defId)
        {
            _selectedDefId = defId;
            var def = ConfigTable.Find<PieceDef>(defId);
            if (def == null) return;
            _editor.BeginEdit(defId); // 编辑会话：记录初始快照
            _slotTemplates = GetCurrentProgram(def);
            FillPieceInfo(def);
        }

        List<Template> GetCurrentProgram(PieceDef def)
        {
            if (_state.TryGetCurrentProgram(def.Id, out var edited)) return new List<Template>(edited);
            if (def.programSet != null && def.programSet.Count > 0) return new List<Template>(def.programSet[0].slots);
            return new List<Template>();
        }

        void FillPieceInfo(PieceDef def)
        {
            if (_infoName != null) _infoName.text = VerticalName(def.displayName);
            if (_infoValueText != null) _infoValueText.text = def.value.ToString();
            if (_infoTypeText != null)
            {
                _infoTypeText.text = def.pieceType == PieceType.Initial ? "始" : def.pieceType == PieceType.Deployable ? "部" : "升";
            }
            for (int i = 0; i < 4; i++)
            {
                bool has = i < _slotTemplates.Count;
                if (_slotImages[i] != null)
                {
                    _slotImages[i].gameObject.SetActive(has);
                    // 挂槽位放置目标（幂等）
                    var drop = _slotImages[i].GetComponent<EditorSlotDrop>();
                    if (drop == null) drop = _slotImages[i].gameObject.AddComponent<EditorSlotDrop>();
                    drop.Init(this, i);
                }
                if (_slotDescs[i] != null) _slotDescs[i].gameObject.SetActive(has);
                if (has)
                {
                    var t = _slotTemplates[i];
                    if (_slotTexts[i] != null) _slotTexts[i].text = SlotTypeChar(t);
                    if (_slotDescs[i] != null) _slotDescs[i].text = SlotDetailDescStatic(t);
                    // 状态颜色：锁定=灰（一版全部可编辑）
                    if (_slotImages[i] != null)
                    {
                        _slotImages[i].color = _slotLocked[i] ? new Color(0.5f, 0.5f, 0.5f, 1f) : Color.white;
                    }
                }
            }
            if (_pieceInfo != null) _pieceInfo.gameObject.SetActive(true);
        }

        /// <summary>替换槽位程序块（拖入放置/内部调用）。</summary>
        public void ReplaceSlot(int slotIndex, Template template)
        {
            if (_selectedDefId < 0 || slotIndex < 0 || slotIndex >= 4) return;
            if (_slotLocked[slotIndex]) return; // 锁定槽不可替换
            while (_slotTemplates.Count <= slotIndex) _slotTemplates.Add(null);
            _slotTemplates[slotIndex] = template;
            CommitProgram();
        }

        /// <summary>移除槽位程序块（拖回列表）。</summary>
        public void RemoveSlot(int slotIndex)
        {
            if (_selectedDefId < 0 || slotIndex < 0 || slotIndex >= _slotTemplates.Count) return;
            if (_slotLocked[slotIndex]) return;
            // 置 Skip 占位（保留槽位——后续可做压缩排序）
            _slotTemplates[slotIndex] = new SkipTemplate();
            CommitProgram();
        }

        void CommitProgram()
        {
            if (_selectedDefId < 0) return;
            _editor.EditProgram(_selectedDefId, new List<Template>(_slotTemplates));
            var def = ConfigTable.Find<PieceDef>(_selectedDefId);
            if (def != null) FillPieceInfo(def);
        }

        // ====== 工具 ======
        static string SlotTypeChar(Template t)
        {
            switch (t)
            {
                case MoveTemplate: return "移";
                case AttackTemplate: return "攻";
                default: return "跳";
            }
        }

        static string SlotDetailDescStatic(Template t)
        {
            var mapped = SlotDescTable.Get(t);
            if (mapped != null) return mapped;
            // 回退：程序块类型简述
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
    }

    /// <summary>编辑面板拖拽源（程序块卡 → 槽位）。</summary>
    public class EditorProgramDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private PieceEditPanel _panel;
        private Template _template;
        private CanvasGroup _cg;

        public Template Template => _template;

        public void Init(PieceEditPanel panel, Template template)
        {
            _panel = panel;
            _template = template;
            _cg = GetComponent<CanvasGroup>();
            if (_cg == null) _cg = gameObject.AddComponent<CanvasGroup>();
        }

        public void OnBeginDrag(PointerEventData eventData) { _cg.alpha = 0.6f; _cg.blocksRaycasts = false; }
        public void OnDrag(PointerEventData eventData) { transform.position = eventData.position; }
        public void OnEndDrag(PointerEventData eventData)
        {
            _cg.alpha = 1f;
            _cg.blocksRaycasts = true;
            transform.localPosition = Vector3.zero; // 放回列表原位（放置成功与否由槽位 OnDrop 处理）
        }
    }

    /// <summary>编辑面板槽位放置目标（Img_InfoProgram1~4 挂载）。</summary>
    public class EditorSlotDrop : MonoBehaviour, IDropHandler
    {
        private PieceEditPanel _panel;
        private int _slotIndex;

        public void Init(PieceEditPanel panel, int slotIndex)
        {
            _panel = panel;
            _slotIndex = slotIndex;
        }

        public void OnDrop(PointerEventData eventData)
        {
            var drag = eventData.pointerDrag != null ? eventData.pointerDrag.GetComponent<EditorProgramDrag>() : null;
            if (drag == null) return;
            _panel.ReplaceSlot(_slotIndex, drag.Template);
        }
    }
}
