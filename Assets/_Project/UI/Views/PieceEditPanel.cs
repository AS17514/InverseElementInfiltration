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
                    // 卡面槽位放置目标：拖程序块到棋子卡上对应程序块位置 → 替换该棋子的该槽（策划内容：拖拽修改棋子行动逻辑）
                    var drop = p.GetComponent<PieceCardSlotDrop>();
                    if (drop == null) drop = p.gameObject.AddComponent<PieceCardSlotDrop>();
                    drop.Init(this, def.Id, i);
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

        /// <summary>棋子卡面槽位放置（拖程序块到卡上程序块图标 → 替换该槽——与信息区槽位同语义，直接改棋子行动逻辑）。</summary>
        public void ReplacePieceCardSlot(int defId, int slotIndex, Template template)
        {
            if (_selectedDefId != defId)
            {
                SelectPiece(defId); // 未选中该棋子：先切编辑会话（BeginEdit 记录快照）
            }
            ReplaceSlot(slotIndex, template);
            RefreshPieceCardProgram(defId); // 卡面程序图标文字同步（移/攻/跳）
        }

        /// <summary>刷新棋子卡面程序图标（Grp_PieceProgramInfo 内 Piece_ProgramInfo 文本——编辑后与当前程序一致）。</summary>
        void RefreshPieceCardProgram(int defId)
        {
            if (_pieceContent == null) return;
            foreach (Transform card in _pieceContent)
            {
                if (card.name != $"PieceCard_{ConfigTable.Find<PieceDef>(defId)?.name}") continue;
                var progRoot = FindDeep(card, "Grp_PieceProgramInfo");
                if (progRoot == null) return;
                _state.TryGetCurrentProgram(defId, out var edited);
                var slots = edited ?? (ConfigTable.Find<PieceDef>(defId)?.programSet?[0].slots);
                int i = 0;
                foreach (Transform p in progRoot)
                {
                    var t = p.GetComponentInChildren<TMP_Text>();
                    if (t != null && slots != null && i < slots.Count)
                    {
                        t.text = SlotTypeChar(slots[i]);
                    }
                    i++;
                }
                return;
            }
        }

        /// <summary>深层按名查找（容错 prefab 复制 (1) 后缀）。EditorProgramDrag 幽灵构建也用它。</summary>
        internal static Transform FindDeep(Transform root, string name)
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
            // 程序库卡 = Program_Card 预制体（李毕编排：Img_ProgramType 类型图标 + Txt_ProgramCount + Txt_ProgramDesc）——异步加载后填充
            StartCoroutine(BuildProgramList());
        }

        System.Collections.IEnumerator BuildProgramList()
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Program_Card");
            yield return handle;
            if (handle.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                Debug.LogWarning("[PieceEdit] Program_Card 加载失败——程序库为空");
                yield break;
            }
            var template = handle.Result;
            foreach (var slot in _programLibrary)
            {
                var go = Instantiate(template, _programContent);
                go.name = $"Prog_{SlotDescTable.FeatureOf(slot)}";
                FillProgramCard(go, slot);
                // 拖拽源：程序块 → 槽位
                var drag = go.AddComponent<EditorProgramDrag>();
                drag.Init(this, slot);
            }
        }

        /// <summary>填充程序库卡（Program_Card 预制体）：类型图标字 + 数量 + 描述。
        /// 数量 = 该模板 id 在全部棋子程序中的出现次数（库存数；模板库独有=0；单张不显示）。</summary>
        void FillProgramCard(GameObject go, Template slot)
        {
            var desc = FindDeep(go.transform, "Txt_ProgramDesc")?.GetComponent<TMP_Text>();
            if (desc != null) desc.text = SlotDetailDescStatic(slot);
            var typeTxt = FindDeep(go.transform, "Img_ProgramType")?.GetComponentInChildren<TMP_Text>();
            if (typeTxt != null) typeTxt.text = SlotTypeChar(slot);
            var count = FindDeep(go.transform, "Txt_ProgramCount")?.GetComponent<TMP_Text>();
            if (count != null)
            {
                int n = CountInPieces(slot);
                // 优化显示：仅多张（≥2）显示 ×N——单张/无库存不占位
                count.text = n >= 2 ? $"×{n}" : "";
            }
        }

        /// <summary>统计模板 id 在全部棋子程序中的出现次数（id=0 未编号不计——同 id=同结构）。</summary>
        static int CountInPieces(Template slot)
        {
            if (slot.id <= 0) return 0;
            int n = 0;
            foreach (var def in ConfigTable.All<PieceDef>())
            {
                if (def.programSet == null) continue;
                foreach (var prog in def.programSet)
                {
                    if (prog.slots == null) continue;
                    foreach (var s in prog.slots)
                    {
                        if (s != null && s.id == slot.id) n++;
                    }
                }
            }
            return n;
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
        private GameObject _ghost;        // 拖拽幽灵（视觉跟随副本）——原卡留原位，GridLayout 不重排

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
            _cg.alpha = 0.3f; // 原卡半透明留原位（库卡不消耗——拖拽语义=复制放置）
            // 幽灵 = 仅类型图标（Img_ProgramType 副本）挂 Canvas 根跟随鼠标——半透明，不挡 raycast
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var typeNode = PieceEditPanel.FindDeep(transform, "Img_ProgramType");
            if (typeNode == null) return;
            _ghost = Instantiate(typeNode.gameObject, canvas.transform);
            _ghost.name = "ProgDragGhost";
            // 幽灵只做视觉跟随：移除复制来的拖拽组件（防 ghost 自身响应拖拽事件/引用错乱）
            var ghostDrag = _ghost.GetComponent<EditorProgramDrag>();
            if (ghostDrag != null) Destroy(ghostDrag);
            var ghostCg = _ghost.GetComponent<CanvasGroup>();
            if (ghostCg == null) ghostCg = _ghost.AddComponent<CanvasGroup>();
            ghostCg.alpha = 0.6f;             // 半透明跟随
            ghostCg.blocksRaycasts = false;   // 幽灵不挡槽位 raycast
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_ghost != null) _ghost.transform.position = eventData.position;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _cg.alpha = 1f;
            if (_ghost != null) Destroy(_ghost);
            _ghost = null;
            // 原卡从未离开 Content——放置成功与否由槽位 OnDrop 处理（pointerDrag=原卡，EditorProgramDrag 可查）
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

    /// <summary>
    /// 棋子卡面槽位放置目标（Piece_Card 上 Grp_PieceProgramInfo 内每个 Piece_ProgramInfo 挂载）。
    /// 策划内容：拖程序块到棋子卡上对应程序块位置 → 直接修改该棋子的行动逻辑（与信息区槽位同语义）。
    /// </summary>
    public class PieceCardSlotDrop : MonoBehaviour, IDropHandler
    {
        private PieceEditPanel _panel;
        private int _defId;
        private int _slotIndex;

        public void Init(PieceEditPanel panel, int defId, int slotIndex)
        {
            _panel = panel;
            _defId = defId;
            _slotIndex = slotIndex;
        }

        public void OnDrop(PointerEventData eventData)
        {
            var drag = eventData.pointerDrag != null ? eventData.pointerDrag.GetComponent<EditorProgramDrag>() : null;
            if (drag == null) return;
            _panel.ReplacePieceCardSlot(_defId, _slotIndex, drag.Template);
        }
    }
}
