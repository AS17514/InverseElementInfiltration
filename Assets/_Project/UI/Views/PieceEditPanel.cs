using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 棋子编辑面板（程序编排）：左列棋子选择 → 中列程序库拖模板 → 右侧信息区 4 槽位。
    /// 编辑语义：按 defId（种类级）整组替换 4 槽程序（EditorSession.EditProgram，经 Resolver 写种类级表）。
    /// 排序 = 插入语义（网页列表拖拽）：拖入 = 插入该位置（原块及之后顺移）；槽间拖 = 重排；拖出空白 = 移除。
    /// 吸附位点 = 信息区 Grp_InfoProgram 的 Img_InfoProgram1~4（+ Grp_ProgramDesc 的 Txt_InfoProgram1~4Desc 双节点判定）。
    /// 锁定块（模板原始程序块）：绝对固定——不可拖出/移除/作为吸附目标（拖入锁定槽直接拒绝）。
    /// </summary>
    public class PieceEditPanel : PanelBase
    {
        public override string Key => "PieceEdit";

        [Header("拖拽吸附")]
        [Tooltip("吸附判定：槽位矩形外扩量（区域吸附——指针进入外扩矩形即命中，多矩形重叠取中心最近）")]
        [SerializeField] private float _snapExpand = 10f;

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
        private List<bool> _slotLocked = new List<bool>();            // 槽位锁定标记（与 _slotTemplates 同步位移——模板原始程序块）

        // ====== 程序库（全局模板去重） ======
        private List<Template> _programLibrary = new List<Template>();
        private GameObject _progTemplate; // Piece_ProgramInfo prefab（卡面缩略图模板——Addressables）

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
            // 程序编辑落账 → 刷新程序库数量（Txt_ProgramCount——随拖入拖出实时变化）
            EventCenter.Instance.AddEventListener(GameEvent.ProgramEdited, OnProgramEdited);
            // Btn_Next：编辑完成 → 下一步（新局=进战斗 / 事件关=EventCompleted 推进）
            // 路径跟随 2026-08-11 面板重构：Grp/Grp_R/Grp_Low/Btn_Next（旧 Grp_L/Grp_Top 已不存在）
            var next = transform.Find("Grp/Grp_R/Grp_Low/Btn_Next")?.GetComponent<Button>();
            if (next != null)
            {
                next.onClick.RemoveAllListeners();
                next.onClick.AddListener(OnNext);
            }
        }

        void OnDestroy()
        {
            EventCenter.Instance.RemoveEventListener(GameEvent.ProgramEdited, OnProgramEdited);
        }

        void OnNext()
        {
            // 编辑完成 → 结束编辑会话（清全部 EditingDefs 标记——防残留进存档）+ 通知 TowerFlow 推进
            if (_editor != null)
            {
                foreach (var defId in new List<int>(_state.EditingDefs))
                {
                    _editor.EndEdit(defId);
                }
            }
            gameObject.SetActive(false);
            EventCenter.Instance.EventTrigger(GameEvent.EventCompleted);
        }

        protected override void OnShow()
        {
            // 新局重置：清选中 + 隐藏信息区 + 重建棋子列表（卡面程序缩略图随当前数据刷新——
            // ⚠️ 2026-08-12：RefreshPieceList 原只在 Awake 跑一次，面板常驻跨局复用 → 卡面显示旧局编辑结果）
            _selectedDefId = -1;
            _slotTemplates.Clear();
            _slotLocked.Clear(); // 锁定标记与槽同步清（选中后 ShowPieceInfo 重建）
            if (_pieceInfo != null) _pieceInfo.gameObject.SetActive(false);
            RefreshPieceList();
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
            // 程序块排序：类型优先（Move→Attack→Skip），同类型保持原顺序（2026-08-11 需求）
            _programLibrary.Sort((a, b) =>
            {
                int ta = a is MoveTemplate ? 0 : a is AttackTemplate ? 1 : 2;
                int tb = b is MoveTemplate ? 0 : b is AttackTemplate ? 1 : 2;
                return ta.CompareTo(tb);
            });
        }

        // ====== 左列：棋子列表（Piece_Card prefab + ToggleGroup 单选 + 程序图标区） ======
        bool _buildingList; // 防重入：Awake 与 OnShow 都会触发构建（Addressables 异步）——并发会双份卡面闪现

        void RefreshPieceList()
        {
            if (_buildingList) return; // 构建中：跳过（上一次构建会重建全部卡面，结果一致）
            StartCoroutine(BuildPieceList());
        }

        System.Collections.IEnumerator BuildPieceList()
        {
            if (_pieceContent == null) yield break;
            _buildingList = true;
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
            if (progHandle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && progHandle.Result != null)
            {
                _progTemplate = progHandle.Result; // 缓存模板（RefreshPieceCardProgram 动态增删用）
            }
            foreach (Transform child in _pieceContent) Destroy(child.gameObject);
            // 类型优先（初始→部署→升变）+ 同类型价值升序（全场景统一排序——CardTypeColors.SortPieces）
            var defs = new List<PieceDef>(ConfigTable.All<PieceDef>());
            CardTypeColors.SortPieces(defs);
            foreach (var def in defs)
            {
                var go = Instantiate(cardHandle.Result, _pieceContent);
                go.name = $"PieceCard_{def.name}";
                FillPieceCard(go, def, progHandle.Result, group);
            }
            // 滚动位置归零（跨局打开不残留旧滚动）
            var scroll = _pieceContent.GetComponentInParent<ScrollRect>();
            if (scroll != null) scroll.normalizedPosition = Vector2.zero;
            _buildingList = false;
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
            // 程序图标区：每槽放一个 Piece_ProgramInfo（Text=移/攻/跳——缩略图显示，非吸附位点）
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

        /// <summary>吸附判定外扩量（区域吸附——EditorProgramDrag 用）。</summary>
        public float SnapExpand => _snapExpand;

        /// <summary>
        /// 收集吸附候选：信息区 4 槽位——每槽吸附区域 = Img_InfoProgram 与 Txt_InfoProgramDesc 的
        /// 屏幕包围盒并集（两列之间的空隙自动补齐，无需美术拼空组）。OnBeginDrag 调用一次（拖拽期间布局不变）。
        /// </summary>
        public List<InfoSlotTarget> CollectInfoSlotTargets(Camera uiCam)
        {
            var list = new List<InfoSlotTarget>();
            for (int i = 0; i < 4; i++)
            {
                if (_slotImages[i] == null && _slotDescs[i] == null) continue;
                var rect = ScreenUnion(_slotImages[i] != null ? _slotImages[i].rectTransform : null,
                                       _slotDescs[i] != null ? _slotDescs[i].rectTransform : null, uiCam);
                list.Add(new InfoSlotTarget { SlotIndex = i, ScreenRect = rect });
            }
            return list;
        }

        /// <summary>两个节点的屏幕矩形并集（世界角点 → 屏幕 → AABB 合并；单节点=自身矩形）。</summary>
        static Rect ScreenUnion(RectTransform a, RectTransform b, Camera cam)
        {
            var rect = ScreenRectOf(a, cam);
            if (b != null)
            {
                var rb = ScreenRectOf(b, cam);
                rect = Rect.MinMaxRect(Mathf.Min(rect.xMin, rb.xMin), Mathf.Min(rect.yMin, rb.yMin),
                                       Mathf.Max(rect.xMax, rb.xMax), Mathf.Max(rect.yMax, rb.yMax));
            }
            return rect;
        }

        /// <summary>节点世界角点 → 屏幕 AABB（ScreenSpaceCamera 用 uiCam；Overlay 传 null）。</summary>
        static Rect ScreenRectOf(RectTransform rt, Camera cam)
        {
            if (rt == null) return new Rect();
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                var sp = cam != null ? RectTransformUtility.WorldToScreenPoint(cam, corners[i])
                                     : (Vector2)corners[i];
                minX = Mathf.Min(minX, sp.x); maxX = Mathf.Max(maxX, sp.x);
                minY = Mathf.Min(minY, sp.y); maxY = Mathf.Max(maxY, sp.y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        /// <summary>槽位高亮组件（按槽索引——吸附判定命中 Img 或 Desc 都作用于对应 Img 的高亮）。</summary>
        public SlotSnapHighlight GetSlotHighlight(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= 4 || _slotImages[slotIndex] == null) return null;
            var hl = _slotImages[slotIndex].GetComponent<SlotSnapHighlight>();
            if (hl == null) hl = _slotImages[slotIndex].gameObject.AddComponent<SlotSnapHighlight>();
            return hl;
        }

        /// <summary>该槽是否锁定块（不可拖入覆盖——UpdateSnap 命中时不高亮，防"拖入必失败无反馈"）。</summary>
        public bool IsSlotLocked(int slotIndex)
        {
            return slotIndex >= 0 && slotIndex < _slotLocked.Count && _slotLocked[slotIndex];
        }

        /// <summary>刷新棋子卡面程序图标（Grp_PieceProgramInfo 内 Piece_ProgramInfo）——编辑后按当前程序数动态增删缩略图。</summary>
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
                int count = slots != null ? Mathf.Min(slots.Count, 4) : 0;
                // 增：当前程序多于已有缩略图 → 补建（模板未就绪则只更新已有部分）
                int existing = progRoot.childCount;
                if (_progTemplate != null)
                {
                    for (int k = existing; k < count; k++)
                    {
                        Instantiate(_progTemplate, progRoot);
                    }
                }
                // 删：多于程序数的多余缩略图隐藏（不 Destroy——防与模板异步加载竞态）
                int i = 0;
                foreach (Transform p in progRoot)
                {
                    bool show = i < count;
                    if (p.gameObject.activeSelf != show) p.gameObject.SetActive(show);
                    if (show)
                    {
                        var t = p.GetComponentInChildren<TMP_Text>();
                        if (t != null && slots != null) t.text = SlotTypeChar(slots[i]);
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
                // 拖拽源：程序库块（Library 模式——复制放置，原卡不消耗）
                var drag = go.AddComponent<EditorProgramDrag>();
                drag.Init(this, slot, EditorProgramDrag.DragSource.Library, -1);
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

        /// <summary>
        /// 统计模板（类型+id）在全部棋子**当前程序**（默认 + CurrentPrograms 编辑差异）中的出现次数。
        /// 匹配键 = FeatureOf（类型+编号）——id 跨类型共享（Move-4/DirectFire-4 同 id=4），裸 id 统计会串。
        /// </summary>
        int CountInPieces(Template slot)
        {
            string key = SlotDescTable.FeatureOf(slot);
            int n = 0;
            foreach (var def in ConfigTable.All<PieceDef>())
            {
                List<Template> prog;
                if (_state != null && _state.TryGetCurrentProgram(def.Id, out var edited))
                {
                    prog = edited; // 编辑差异优先
                }
                else if (def.programSet != null && def.programSet.Count > 0)
                {
                    prog = def.programSet[0].slots; // 默认程序
                }
                else
                {
                    continue;
                }
                if (prog == null) continue;
                foreach (var s in prog)
                {
                    if (s != null && SlotDescTable.FeatureOf(s) == key) n++;
                }
            }
            return n;
        }

        /// <summary>程序编辑落账（ProgramEdited 事件）→ 程序库卡数量文本刷新（不重建卡片——拖拽中安全）。</summary>
        void OnProgramEdited(object data)
        {
            if (_programContent == null) return;
            foreach (Transform child in _programContent)
            {
                var card = child.gameObject;
                if (card == null) continue;
                var count = FindDeep(card.transform, "Txt_ProgramCount")?.GetComponent<TMP_Text>();
                if (count == null) continue;
                // 卡名 Prog_{FeatureOf} 反查模板（BuildProgramList 命名约定）——无法反查则跳过
                string name = card.name;
                if (!name.StartsWith("Prog_")) continue;
                var slot = FindSlotByFeature(name.Substring(5));
                if (slot == null) continue;
                int n = CountInPieces(slot);
                count.text = n >= 2 ? $"×{n}" : "";
            }
        }

        /// <summary>按卡名（Prog_Move-1 等）反查程序库模板。</summary>
        Template FindSlotByFeature(string feature)
        {
            foreach (var slot in _programLibrary)
            {
                if (SlotDescTable.FeatureOf(slot) == feature) return slot;
            }
            return null;
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
            InitLockedFlags(def);
            FillPieceInfo(def);
        }

        List<Template> GetCurrentProgram(PieceDef def)
        {
            if (_state.TryGetCurrentProgram(def.Id, out var edited)) return new List<Template>(edited);
            if (def.programSet != null && def.programSet.Count > 0) return new List<Template>(def.programSet[0].slots);
            return new List<Template>();
        }

        /// <summary>锁定标记：模板原始程序块（def 默认模组前 N 槽）锁定——绝对固定（当前 _allowShiftLocked=false）。</summary>
        void InitLockedFlags(PieceDef def)
        {
            _slotLocked.Clear();
            int templateCount = def.programSet != null && def.programSet.Count > 0 ? def.programSet[0].slots.Count : 0;
            for (int i = 0; i < _slotTemplates.Count; i++)
            {
                _slotLocked.Add(i < templateCount); // 前 N 槽 = 模板原始块
            }
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
                // 槽位节点常显（未拥有的空槽也可作为吸附位点——2026-08-11 需求：空位也可拖入插入）
                bool has = i < _slotTemplates.Count;
                if (_slotImages[i] != null)
                {
                    _slotImages[i].gameObject.SetActive(true); // 空槽也保留位点（半透明空态）
                    // 拖拽源（槽位块拖出：重排/移除——InfoSlot 模式）。组件复用不销毁（防同帧 Destroy+GetComponent 竞态）：
                    // 锁定/空槽时禁拖（OnBeginDrag 拦截），有块非锁定时重新 Init（刷新 template/sourceSlot）
                    var slotDrag = _slotImages[i].GetComponent<EditorProgramDrag>();
                    if (slotDrag == null) slotDrag = _slotImages[i].gameObject.AddComponent<EditorProgramDrag>();
                    bool draggable = has && !_slotLocked[i];
                    slotDrag.SetDraggable(draggable);
                    if (draggable)
                    {
                        slotDrag.Init(this, _slotTemplates[i], EditorProgramDrag.DragSource.InfoSlot, i);
                    }
                    // 槽位标记组件（OnEndDrag 精确命中识别用；落账不在此）
                    var drop = _slotImages[i].GetComponent<EditorSlotDrop>();
                    if (drop == null) drop = _slotImages[i].gameObject.AddComponent<EditorSlotDrop>();
                    drop.Init(this, i);
                }
                if (_slotDescs[i] != null) _slotDescs[i].gameObject.SetActive(true); // 描述位点同样常显（空文本）
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
                else
                {
                    // 空槽位点：无字 + 半透明浅灰（可吸附——拖入即插入该位置）
                    if (_slotTexts[i] != null) _slotTexts[i].text = "";
                    if (_slotDescs[i] != null) _slotDescs[i].text = "";
                    if (_slotImages[i] != null) _slotImages[i].color = new Color(1f, 1f, 1f, 0.15f);
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

        // ====== 程序编排（锁定块在前绝对固定 + 替换/插入语义——整组提交） ======

        /// <summary>
        /// 拖入到槽 to（2026-08-11 需求对齐 v2）：
        /// - 目标锁定槽 → 拒绝（锁定块绝对固定）
        /// - 程序有空缺（Count &lt; 4）→ **插入 to 位置**（原 to 及之后顺移，空位补齐——如 [锁a 锁b c 空] 拖 x 到 c → [锁a 锁b x c]）
        /// - 程序满 4 槽 → 替换 to 槽（原块回程序库——无限复制语义下无额外动作）
        /// </summary>
        public bool InsertProgram(int to, Template template)
        {
            if (_selectedDefId < 0 || template == null) return false;
            to = Mathf.Clamp(to, 0, 4);
            if (to < _slotLocked.Count && _slotLocked[to]) return false; // 目标锁定槽：拒绝
            if (_slotTemplates.Count >= 4)
            {
                // 满 4 槽：替换目标槽（锁定标记不变——原块本就非锁定）
                _slotTemplates[to] = template;
            }
            else
            {
                // 有空缺：插入 to（顺移——原 to 及之后后移，空缺补齐）
                to = Mathf.Clamp(to, 0, _slotTemplates.Count);
                _slotTemplates.Insert(to, template);
                _slotLocked.Insert(to, false); // 新块不锁定
            }
            CommitProgram();
            return true;
        }

        /// <summary>
        /// 槽间重排（2026-08-12 需求修正：用户实测发现插入语义方向不对称——紧邻上拖下=原位无变化）。
        /// 现语义：目标槽有块 → **交换（对调）**；目标空缺（末尾）→ 插入追加；锁定块不可拖出/不可作目标。
        /// </summary>
        public bool MoveProgram(int from, int to)
        {
            if (_selectedDefId < 0 || from < 0 || from >= _slotTemplates.Count) return false;
            if (_slotLocked[from]) return false; // 锁定块不可拖出
            if (from == to) return false;
            to = Mathf.Clamp(to, 0, _slotTemplates.Count); // 允许 == Count（空缺末尾）
            if (to < _slotLocked.Count && _slotLocked[to]) return false; // 目标锁定槽：拒绝（锁定块绝对固定）
            if (to == _slotTemplates.Count)
            {
                // 空缺末尾：插入追加（顺移——原 from 移除，其余不动）
                var t = _slotTemplates[from];
                var l = _slotLocked[from];
                _slotTemplates.RemoveAt(from);
                _slotLocked.RemoveAt(from);
                _slotTemplates.Add(t);
                _slotLocked.Add(l);
            }
            else
            {
                // 目标槽有块：交换（对调——方向对称：上拖下/下拖上都交换）
                var t = _slotTemplates[from];
                var l = _slotLocked[from];
                _slotTemplates[from] = _slotTemplates[to];
                _slotLocked[from] = _slotLocked[to];
                _slotTemplates[to] = t;
                _slotLocked[to] = l;
            }
            CommitProgram();
            return true;
        }

        /// <summary>移除槽位块（拖出到空白）。锁定块不可移除。</summary>
        public bool RemoveProgramAt(int index)
        {
            if (_selectedDefId < 0 || index < 0 || index >= _slotTemplates.Count) return false;
            if (_slotLocked[index]) return false;
            _slotTemplates.RemoveAt(index);
            _slotLocked.RemoveAt(index); // 锁定标记同步
            CommitProgram();
            return true;
        }

        void CommitProgram()
        {
            if (_selectedDefId < 0) return;
            _editor.EditProgram(_selectedDefId, new List<Template>(_slotTemplates));
            var def = ConfigTable.Find<PieceDef>(_selectedDefId);
            if (def != null)
            {
                FillPieceInfo(def);
                RefreshPieceCardProgram(_selectedDefId); // 左列卡面缩略图同步
            }
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

    /// <summary>吸附候选（信息区槽位——Img∪Desc 屏幕包围盒，命中高亮作用于 Img）。</summary>
    public class InfoSlotTarget
    {
        public int SlotIndex;
        public Rect ScreenRect;   // 屏幕坐标吸附区域（含 Img+Desc 并集——空隙自动补齐）
    }

    /// <summary>
    /// 编辑面板拖拽源（程序库块 Library / 信息区槽位块 InfoSlot）。
    /// 幽灵：Library=Img_ProgramType 图标副本；InfoSlot=槽位块自身副本。ScreenSpaceCamera 下坐标走 RectTransformUtility 换算。
    /// 吸附：指针进入信息区槽位热区（SnapRadius）→ 槽位高亮（SlotSnapHighlight）→ 松手落账（HandledBySnap 防 EventSystem 双触发）。
    /// 落账语义（插入排序）：Library+吸附=插入；InfoSlot+吸附=重排；InfoSlot+空白=移除；Library+空白=无操作。
    /// </summary>
    public class EditorProgramDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>拖拽源类型：程序库卡（复制放置）/ 信息区槽位块（移动/移除）。</summary>
        public enum DragSource { Library, InfoSlot }

        private PieceEditPanel _panel;
        private Template _template;
        private DragSource _source;
        private int _sourceSlot = -1;     // InfoSlot 模式：源槽索引
        private bool _draggable = true;   // 禁拖开关（锁定块/空槽——组件复用不销毁）
        private CanvasGroup _cg;
        private GameObject _ghost;        // 拖拽幽灵（类型图标/槽位块副本）——原对象留原位

        private RectTransform _canvasRect; // UIRoot RectTransform（屏幕→世界换算用）
        private Camera _cam;               // UI 相机（canvas.worldCamera 优先，Overlay 下为 null）
        private List<InfoSlotTarget> _slotTargets;   // 吸附候选（信息区 4 槽双节点，OnBeginDrag 收集一次）
        private InfoSlotTarget _snapTarget;          // 当前吸附槽位（高亮中）
        private SlotSnapHighlight _snapHighlight;    // 当前吸附槽位的高亮组件
        private bool _cancelled;           // Esc 取消拖拽标记（OnEndDrag 跳过落账）
        private Rect _ownSlotRect;         // 自身源槽屏幕矩形（InfoSlot——松手在自身 Desc 区域守卫用）

        public Template Template => _template;
        public DragSource Source => _source;
        public int SourceSlot => _sourceSlot;

        /// <summary>本帧拖拽是否已由吸附落账（EventSystem OnDrop 需跳过——防双触发）。</summary>
        public bool HandledBySnap { get; private set; }

        public void Init(PieceEditPanel panel, Template template, DragSource source, int sourceSlot)
        {
            _panel = panel;
            _template = template;
            _source = source;
            _sourceSlot = sourceSlot;
            _cg = GetComponent<CanvasGroup>();
            if (_cg == null) _cg = gameObject.AddComponent<CanvasGroup>();
        }

        /// <summary>可拖拽开关（锁定块/空槽禁拖——组件复用不销毁，OnBeginDrag 拦截）。</summary>
        public void SetDraggable(bool draggable)
        {
            _draggable = draggable;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_draggable) return; // 锁定块/空槽：禁拖（组件复用——不销毁）
            _cg.alpha = 0.3f; // 原对象半透明留原位
            HandledBySnap = false;
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;
            _canvasRect = canvas.transform as RectTransform;
            _cam = canvas.worldCamera ?? eventData.pressEventCamera;
            // 幽灵源：Library=类型图标；InfoSlot=槽位块自身副本
            Transform ghostSource = null;
            if (_source == DragSource.Library)
            {
                ghostSource = PieceEditPanel.FindDeep(transform, "Img_ProgramType");
            }
            else
            {
                ghostSource = transform; // 槽位块（Img_InfoProgram 含类型字）
            }
            if (ghostSource == null) return;
            _ghost = Instantiate(ghostSource.gameObject, canvas.transform);
            _ghost.name = _source == DragSource.Library ? "ProgDragGhost" : "SlotDragGhost";
            // 幽灵只做视觉跟随：置顶（防被其他面板盖住）+ 移除拖拽/drop/高亮组件（防自身响应事件/引用错乱）
            _ghost.transform.SetAsLastSibling();
            var ghostDrag = _ghost.GetComponent<EditorProgramDrag>();
            if (ghostDrag != null) Destroy(ghostDrag);
            var ghostDrop = _ghost.GetComponent<EditorSlotDrop>();
            if (ghostDrop != null) Destroy(ghostDrop);
            var ghostHl = _ghost.GetComponent<SlotSnapHighlight>();
            if (ghostHl != null) Destroy(ghostHl);
            var ghostCg = _ghost.GetComponent<CanvasGroup>();
            if (ghostCg == null) ghostCg = _ghost.AddComponent<CanvasGroup>();
            ghostCg.alpha = 0.6f;             // 半透明跟随
            ghostCg.blocksRaycasts = false;   // 幽灵不挡槽位 raycast
            // 吸附候选：信息区 4 槽位（Img∪Desc 屏幕包围盒——空隙自动补齐；拖拽期间布局不重建）
            if (_panel != null)
            {
                _slotTargets = _panel.CollectInfoSlotTargets(_cam);
                // 自身源槽矩形（InfoSlot——UpdateSnap 排除自身列 + OnEndDrag 松手在自身 Desc 区域守卫）
                if (_source == DragSource.InfoSlot)
                {
                    foreach (var t in _slotTargets)
                    {
                        if (t.SlotIndex == _sourceSlot) { _ownSlotRect = t.ScreenRect; break; }
                    }
                }
            }
            _cancelled = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            // ScreenSpaceCamera 下世界坐标 ≠ 屏幕坐标（根 Canvas scale 0.01）——必须 RectTransformUtility 换算
            if (_ghost != null && _canvasRect != null)
            {
                if (RectTransformUtility.ScreenPointToWorldPointInRectangle(
                        _canvasRect, eventData.position, _cam, out var world))
                {
                    _ghost.transform.position = world;
                }
            }
            UpdateSnap(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_cg != null) _cg.alpha = 1f;
            if (_ghost != null) Destroy(_ghost);
            _ghost = null;
            if (_cancelled) { _cancelled = false; ClearSnap(true); _slotTargets = null; return; } // Esc 已取消：只清理不落账
            // 落账语义（插入排序）——职责只在 OnEndDrag（Unity ReleaseMouse 顺序：OnDrop 先于 OnEndDrag，
            // 若在 OnDrop 落账会与吸附落账双触发；故 OnDrop 不落账，此处统一处理）：
            //  Library+吸附 = 插入；InfoSlot+吸附 = 重排；InfoSlot+空白 = 移除；Library+空白 = 无操作
            // 吸附优先；无吸附时用 EventSystem 精确命中（pointerCurrentRaycast → 槽位 EditorSlotDrop）
            int dropSlot = -1;
            if (_snapTarget != null)
            {
                dropSlot = _snapTarget.SlotIndex;
            }
            else if (eventData.pointerCurrentRaycast.gameObject != null)
            {
                var drop = eventData.pointerCurrentRaycast.gameObject.GetComponentInParent<EditorSlotDrop>();
                if (drop != null) dropSlot = drop.SlotIndex;
            }

            bool committed = false; // 落账成功？（决定 ClearSnap 是否恢复颜色——成功时 FillPieceInfo 已设新状态色）
            bool droppedOnOwn = _source == DragSource.InfoSlot
                && (dropSlot == _sourceSlot
                    || (dropSlot < 0 && _ownSlotRect.Contains(eventData.position))); // 松手在自身 Desc 区域（无 EditorSlotDrop）
            if (droppedOnOwn)
            {
                HandledBySnap = true; // 拖回自身列 = 无操作（不高亮不落账）
            }
            else if (dropSlot >= 0)
            {
                if (_source == DragSource.Library)
                {
                    committed = _panel.InsertProgram(dropSlot, _template);
                }
                else
                {
                    committed = _panel.MoveProgram(_sourceSlot, dropSlot);
                }
                HandledBySnap = true;
            }
            else if (_source == DragSource.InfoSlot)
            {
                committed = _panel.RemoveProgramAt(_sourceSlot); // 拖出空白 = 移除该块
                HandledBySnap = true;
            }
            ClearSnap(!committed); // 落账成功：不恢复颜色（FillPieceInfo 已刷新）；失败/取消：恢复原色
            _slotTargets = null;
        }

        // ====== 吸附状态机 ======

        /// <summary>
        /// 每帧吸附判定（区域吸附）：指针屏幕点 ∈ 槽位包围盒（Img∪Desc 并集，外扩 SnapExpand）即命中；
        /// 多矩形重叠 → 取中心距离最近。空槽位点（常显）同样可命中——拖入=插入该位置。
        /// 锁定槽（绝对固定不可拖入）与自身源槽直接排除——拖入必失败，不高亮误导。
        /// </summary>
        void UpdateSnap(PointerEventData eventData)
        {
            if (_panel == null || _slotTargets == null) return;
            float expand = _panel.SnapExpand;
            Vector2 pos = eventData.position;
            InfoSlotTarget best = null;
            float bestDist = float.MaxValue;
            foreach (var target in _slotTargets)
            {
                if (target == null) continue;
                if (target.SlotIndex == _sourceSlot) continue; // 自身源槽：不吸附不高亮（Library 模式 _sourceSlot=-1 永不匹配）
                if (_panel.IsSlotLocked(target.SlotIndex)) continue; // 锁定槽不可拖入（绝对固定——恒排除）
                var r = target.ScreenRect;
                // 外扩矩形包含判定（区域吸附——包围盒自动覆盖 Img↔Desc 空隙）
                if (pos.x < r.xMin - expand || pos.x > r.xMax + expand || pos.y < r.yMin - expand || pos.y > r.yMax + expand)
                {
                    continue;
                }
                // 多矩形重叠：取中心距离最近
                float dist = (pos - r.center).magnitude;
                if (dist < bestDist)
                {
                    best = target;
                    bestDist = dist;
                }
            }
            if (best != _snapTarget)
            {
                ClearSnap(true); // 切换目标：先清旧高亮（恢复原色——无落账）
                _snapTarget = best;
                if (_snapTarget != null)
                {
                    _snapHighlight = _panel.GetSlotHighlight(_snapTarget.SlotIndex);
                    if (_snapHighlight != null) _snapHighlight.Activate();
                }
            }
        }

        /// <summary>清除吸附高亮（OnEndDrag/OnDisable/OnDestroy 兜底）。restoreColor：是否恢复原色（落账成功=false）。</summary>
        void ClearSnap(bool restoreColor)
        {
            if (_snapHighlight != null)
            {
                _snapHighlight.Deactivate(restoreColor);
                _snapHighlight = null;
            }
            _snapTarget = null;
        }

        /// <summary>取消拖拽（Esc 兜底——InputSystem 的 cancel 不会路由到拖拽源；恢复 alpha/幽灵/高亮，不落账）。
        /// ⚠️ 幽灵副本组件被 Destroy 时也会触发本方法——Instantiate 不复制 private 字段，副本的 _cg 为 null，必须判空。</summary>
        void CancelDrag()
        {
            _cancelled = true;
            if (_cg != null) _cg.alpha = 1f;
            if (_ghost != null) Destroy(_ghost);
            _ghost = null;
            ClearSnap(true);
            _slotTargets = null;
        }

        void Update()
        {
            // Esc 取消拖拽（InputSystem 下 cancel 事件不会发给拖拽源——轮询兜底）
            if (_ghost != null && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                CancelDrag();
            }
        }

        void OnDisable()
        {
            CancelDrag();
        }

        void OnDestroy()
        {
            CancelDrag();
        }
    }

    /// <summary>
    /// 信息区槽位标记组件（Img_InfoProgram1~4 挂载）。
    /// 落账职责在 EditorProgramDrag.OnEndDrag（Unity ReleaseMouse 顺序 OnDrop 先于 OnEndDrag——此处落账会双触发）。
    /// 本组件只用于：OnEndDrag 精确命中识别（pointerCurrentRaycast → GetComponentInParent&lt;EditorSlotDrop&gt; → SlotIndex）。
    /// </summary>
    public class EditorSlotDrop : MonoBehaviour
    {
        private PieceEditPanel _panel;
        private int _slotIndex;

        public int SlotIndex => _slotIndex;

        public void Init(PieceEditPanel panel, int slotIndex)
        {
            _panel = panel;
            _slotIndex = slotIndex;
        }
    }
}
