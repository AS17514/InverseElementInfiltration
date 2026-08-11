using System.Collections.Generic;
using TheLaw.Core;
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
            // Btn_Next：编辑完成 → 下一步（新局=进战斗 / 事件关=EventCompleted 推进）
            // 路径跟随 2026-08-11 面板重构：Grp/Grp_R/Grp_Low/Btn_Next（旧 Grp_L/Grp_Top 已不存在）
            var next = transform.Find("Grp/Grp_R/Grp_Low/Btn_Next")?.GetComponent<Button>();
            if (next != null)
            {
                next.onClick.RemoveAllListeners();
                next.onClick.AddListener(OnNext);
            }
        }

        void OnNext()
        {
            // 编辑完成 → 通知 TowerFlow 推进（面板关闭——下一节点 EventOpened 会再激活）
            gameObject.SetActive(false);
            EventCenter.Instance.EventTrigger(GameEvent.EventCompleted);
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
            // 路径跟随 2026-08-11 面板重构：棋子列表在 Grp/Grp_R/Grp_Pieces/...，程序库在 Grp/Grp_Programs/...（无 Grp_L 层）
            _pieceContent = transform.Find("Grp/Grp_R/Grp_Pieces/Grp_PieceDisplay/Viewport/Content");
            _programContent = transform.Find("Grp/Grp_Programs/Grp_ProgramDisplay/Viewport/Content");
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
                // 兜底：深层按名查找（prefab 复制可能带 (1) 后缀）
                foreach (var t in GetComponentsInChildren<TMP_Text>(true))
                {
                    if (t.name == "Txt_InfoName") { _infoName = t; break; }
                }
            }
            var portrait = _nonOverlap?.Find("Grp_PortraitNameDisplay/Img_InfoPortrait");
            _infoPortrait = portrait != null ? portrait.GetComponent<Image>() : null;
        }

        // ====== 程序库（独立模板库优先，回退棋子自带去重） ======
        void BuildProgramLibrary()
        {
            _programLibrary.Clear();
            var seen = new HashSet<string>();
            // 独立模板库（TemplateLibrary——编辑候选池，协作者 templates.json 导入）
            foreach (var t in TemplateLibrary.All())
            {
                var key = SlotDescTable.FeatureOf(t);
                if (seen.Add(key)) _programLibrary.Add(t);
            }
            // 回退：模板库未注册时用棋子自带模板去重
            if (_programLibrary.Count == 0)
            {
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
        }

        // ====== 左列：棋子列表（Piece_Card prefab + ToggleGroup 单选 + 程序图标区） ======
        void RefreshPieceList()
        {
            StartCoroutine(BuildPieceList());
        }

        System.Collections.IEnumerator BuildPieceList()
        {
            if (_pieceContent == null) yield break;
            EnsureScrollContent(_pieceContent);
            // 加载 Piece_Card / Piece_ProgramInfo 模板（Addressables）
            var cardHandle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Piece_Card");
            var progHandle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Piece_ProgramInfo");
            yield return cardHandle;
            yield return progHandle;
            if (cardHandle.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || cardHandle.Result == null)
            {
                Debug.LogWarning("[PieceEdit] Piece_Card 加载失败——棋子列表为空");
                yield break;
            }
            // ToggleGroup：单选管理（挂 Content 上）
            var group = _pieceContent.GetComponent<ToggleGroup>();
            if (group == null) group = _pieceContent.gameObject.AddComponent<ToggleGroup>();
            group.allowSwitchOff = false;
            foreach (Transform child in _pieceContent) Destroy(child.gameObject);
            foreach (var def in ConfigTable.All<PieceDef>())
            {
                var go = Instantiate(cardHandle.Result, _pieceContent);
                go.name = $"PieceCard_{def.name}";
                FillPieceCard(go, def, progHandle.Result, group);
            }
        }

        void FillPieceCard(GameObject go, PieceDef def, GameObject progTemplate, ToggleGroup group)
        {
            // 背景种类色（与手牌一致）
            var bg = go.GetComponent<Image>();
            if (bg != null) bg.color = CardTypeColors.For(def.pieceType);
            // 价值数字
            var valueText = FindDeep(go.transform, "Img_PieceValue")?.GetComponentInChildren<TMP_Text>();
            if (valueText != null) valueText.text = def.value.ToString();
            // 类型文字（有 Text 子级才填）
            var typeText = FindDeep(go.transform, "Img_PieceType")?.GetComponentInChildren<TMP_Text>();
            if (typeText != null)
            {
                typeText.text = def.pieceType == PieceType.Initial ? "始" : def.pieceType == PieceType.Deployable ? "部" : "升";
            }
            // 程序图标区：每槽放一个 Piece_ProgramInfo（Text=移/攻/跳）
            var progRoot = FindDeep(go.transform, "Grp_PieceProgramInfo");
            if (progRoot != null && progTemplate != null)
            {
                var slots = def.programSet != null && def.programSet.Count > 0 ? def.programSet[0].slots : null;
                int count = slots != null ? Mathf.Min(slots.Count, 4) : 0;
                for (int i = 0; i < count; i++)
                {
                    var p = Instantiate(progTemplate, progRoot);
                    var t = p.GetComponentInChildren<TMP_Text>();
                    if (t != null) t.text = SlotTypeChar(slots[i]);
                }
            }
            // Toggle 单选：选中 → SelectPiece
            var toggle = go.GetComponent<Toggle>();
            if (toggle != null)
            {
                toggle.group = group;
                var defId = def.Id;
                toggle.onValueChanged.AddListener(on => { if (on) SelectPiece(defId); });
            }
        }

        /// <summary>深层按名查找（容错 prefab 复制 (1) 后缀）。</summary>
        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

        // ====== 中列：程序库 ======
        void RefreshProgramList()
        {
            if (_programContent == null) return;
            EnsureScrollContent(_programContent);
            // GridLayout 与视口不匹配（2×550+15=1115 > 视口 550）→ 重配为单列（2026-08-11 排查：列宽超视口被 Mask 裁掉半边）
            FitGridToViewport(_programContent);
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

        /// <summary>程序库 GridLayout 列宽适配：cellSize.x×列数+spacing 超出 Content 宽 → 缩 cellSize 到能放 2 列（保持网格布局语义）。</summary>
        void FitGridToViewport(Transform content)
        {
            var grid = content.GetComponent<UnityEngine.UI.GridLayoutGroup>();
            if (grid == null) return;
            var rt = content as RectTransform;
            if (rt == null || rt.rect.width <= 0) return;
            int cols = grid.constraint == UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount
                ? Mathf.Max(1, grid.constraintCount)
                : 1;
            float need = grid.cellSize.x * cols + grid.spacing.x * (cols - 1);
            if (need > rt.rect.width)
            {
                float cellX = (rt.rect.width - grid.spacing.x * (cols - 1)) / cols;
                grid.cellSize = new Vector2(Mathf.Max(50f, cellX), grid.cellSize.y);
            }
        }
        /// <summary>保底：Content 加 ContentSizeFitter（垂直撑高——GridLayoutGroup 不改变 Content 尺寸，无 CSF 则滚动条永远满）。
        /// 顶部锚 + 顶 pivot：CSF 撑高向下生长（防顶部被裁）。
        /// 2026-08-11 重构后 Content 是 Viewport 子级：锚点横向拉伸（anchorMax.x=1）→ Content 宽 = Viewport 宽，
        /// 否则 sizeDelta.x=0 时卡片横向溢出/被 Mask 裁剪（原 (0,1)-(0,1) 固定宽导致中列程序卡不可见）。</summary>
        void EnsureScrollContent(Transform content)
        {
            var csf = content.GetComponent<UnityEngine.UI.ContentSizeFitter>();
            if (csf == null) csf = content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
            csf.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            var rt = content as RectTransform;
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f); // 横向拉伸：宽度 = 视口宽度（修复卡片溢出/被裁）
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(0f, rt.sizeDelta.y);
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
            // 右侧信息区底色按种类标识（半透明——不盖子级内容）
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
        private Transform _originParent;   // 拖前父级（放回用——程序库 Content）
        private Vector3 _originLocalPos;   // 拖前局部位置（无布局组件时恢复用）

        public Template Template => _template;

        public void Init(PieceEditPanel panel, Template template)
        {
            _panel = panel;
            _template = template;
            _cg = GetComponent<CanvasGroup>();
            if (_cg == null) _cg = gameObject.AddComponent<CanvasGroup>();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _cg.alpha = 0.6f;
            _cg.blocksRaycasts = false;
            // 记录原位后脱离列表父级（防 GridLayout 布局重建拉回 + Viewport Mask 裁剪 + 被兄弟卡遮挡）
            _originParent = transform.parent;
            _originLocalPos = transform.localPosition;
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null) transform.SetParent(canvas.transform, true);
        }

        public void OnDrag(PointerEventData eventData)
        {
            // 已脱离 Content：世界坐标直接跟随鼠标（不再被 GridLayout 覆盖）
            transform.position = eventData.position;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _cg.alpha = 1f;
            _cg.blocksRaycasts = true;
            if (_originParent != null)
            {
                transform.SetParent(_originParent, false); // 放回程序库 Content
                transform.localPosition = _originLocalPos; // 恢复拖前位置（视觉无跳变）
            }
            // 显式触发父级布局重建：GridLayoutGroup 只在脏标记时重排——不触发则卡停在原位
            if (_originParent is RectTransform parentRt)
            {
                UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(parentRt);
            }
            _originParent = null;
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
