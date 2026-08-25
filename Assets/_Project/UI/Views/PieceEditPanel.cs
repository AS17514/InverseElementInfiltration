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
        private Transform _pieceInfo;      // 右侧统一详情卡（Piece_Handcard）
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
        private int _editableDefId = -1; // 本次编辑事件唯一允许写入的棋子
        private List<Template> _slotTemplates = new List<Template>(); // 当前选中棋子的程序（编辑副本）
        private List<bool> _slotLocked = new List<bool>();            // 槽位锁定标记（与 _slotTemplates 同步位移——2026-08-24 起移动/攻击默认槽可编辑；效果槽锁定[取消效果编辑]）

        // ====== 程序库（按当前棋子查询后端候选池） ======
        private List<Template> _programLibrary = new List<Template>();
        private int _programListGeneration; // 候选切换时丢弃旧的异步列表构建结果
        private GameObject _programCardTemplate; // Program_Card 缓存，避免每次候选刷新重复加载 Addressable
        private bool _loadingProgramCardTemplate;
        private GameObject _progTemplate; // Piece_ProgramInfo prefab（卡面缩略图模板——Addressables）
        private Button _nextBtn;             // Btn_Next（仅选中本次指定棋子时可完成）
        private Button _undoBtn;             // Btn_Undo（单击撤一步 / 长按全部撤回）
        private UndoButtonHandler _undoHandler;

        // ====== 拖拽幽灵登记（防孤儿残留：原生路径偶发不触发 OnEndDrag/OnDestroy 时兜底清理）======
        private readonly List<GameObject> _dragGhosts = new List<GameObject>();

        public void Init(EditorSession editor, GameState state)
        {
            _editor = editor;
            _state = state;
        }

        /// <summary>设置本次编辑事件唯一可写棋子；其他棋子仅允许查看。</summary>
        public void SetEditableDefId(int defId)
        {
            _editableDefId = defId;
            _selectedDefId = defId;
            _slotTemplates.Clear();
            _slotLocked.Clear();
            // 2026-08-26 修复（第 2 关起左右空白）：inactive 面板只记标记、不启动刷新链——
            // RefreshProgramList 在 inactive 对象上 StartCoroutine 会失败（程序区被清空且不重建）；
            // 刷新统一由 OnShow 执行（面板激活后全量重建左右列表）。
            if (gameObject.activeInHierarchy && _editor != null && defId >= 0)
            {
                BuildProgramLibrary();
                RefreshProgramList();
            }
        }

        private bool CanEditSelected()
        {
            return _editableDefId >= 0 && _selectedDefId == _editableDefId && _editor != null;
        }

        private void Awake()
        {
            ResolveNodes();
            BuildProgramLibrary();
            RefreshPieceList();
            RefreshProgramList();
            // 程序编辑落账 → 刷新棋子卡面与详情。
            EventCenter.Instance.AddEventListener(GameEvent.ProgramEdited, OnProgramEdited);
            // Btn_Next：编辑完成 → 下一步（新局=进战斗 / 事件关=EventCompleted 推进）
            // 路径跟随 2026-08-11 面板重构：Grp/Grp_R/Grp_Low/Btn_Next（旧 Grp_L/Grp_Top 已不存在）
            // ⚠️ 2026-08-15：prefab 加 Grp_Btns 层（Grp_Low/Grp_Btns/Btn_Next）——硬路径 Find 失效按钮未绑定，
            // 改为硬路径优先 + FindDeep 兜底（与 Btn_Undo 同模式）
            _nextBtn = transform.Find("Grp/Grp_R/Grp_Low/Btn_Next")?.GetComponent<Button>();
            if (_nextBtn == null)
            {
                var nextGo = FindDeep(transform, "Btn_Next");
                if (nextGo != null) _nextBtn = nextGo.GetComponent<Button>();
            }
            if (_nextBtn != null)
            {
                _nextBtn.onClick.RemoveAllListeners();
                _nextBtn.onClick.AddListener(OnNext);
            }
            // Btn_Undo（2026-08-13：单击撤一步 / 长按全部撤回 / 悬停提示）——按名查找（路径随面板布局变化，FindDeep 兜底）
            _undoBtn = transform.Find("Grp/Grp_R/Grp_Low/Btn_Undo")?.GetComponent<Button>();
            if (_undoBtn == null)
            {
                var undoGo = FindDeep(transform, "Btn_Undo");
                if (undoGo != null) _undoBtn = undoGo.GetComponent<Button>();
            }
            if (_undoBtn != null)
            {
                _undoBtn.onClick.RemoveAllListeners(); // 全部走 UndoButtonHandler（防 Button.onClick 双触发）
                var handler = _undoBtn.gameObject.GetComponent<UndoButtonHandler>();
                if (handler == null) handler = _undoBtn.gameObject.AddComponent<UndoButtonHandler>();
                _undoHandler = handler;
                _undoHandler.OnClick += OnUndoClicked;
                _undoHandler.OnLongPress += OnUndoLongPressed;
                _undoHandler.OnHoverEnter += ShowUndoTooltip;
                _undoHandler.OnHoverExit += HideUndoTooltip;
                var disabledTooltip = _undoBtn.gameObject.GetComponent<DisabledUndoTooltip>();
                if (disabledTooltip == null) disabledTooltip = _undoBtn.gameObject.AddComponent<DisabledUndoTooltip>();
                disabledTooltip.Init(_undoBtn);
            }
            RefreshEditorButtons();
        }

                // ====== 拖拽幽灵登记/清理（EditorProgramDrag 调用；防孤儿残留）======

        internal void RegisterDragGhost(GameObject ghost)
        {
            if (ghost != null && !_dragGhosts.Contains(ghost)) _dragGhosts.Add(ghost);
        }

        /// <summary>取消托管并销毁幽灵（EditorProgramDrag 的 OnEndDrag/CancelDrag 调用）。</summary>
        internal void UnregisterDragGhost(GameObject ghost)
        {
            if (ghost == null) return;
            _dragGhosts.Remove(ghost);
            if (ghost != null) Destroy(ghost);
        }

        /// <summary>清空全部拖拽幽灵（新拖拽开始/面板隐藏/销毁时兜底——清孤儿）。</summary>
        internal void CleanupDragGhosts()
        {
            foreach (var g in _dragGhosts)
            {
                if (g != null) Destroy(g);
            }
            _dragGhosts.Clear();
        }

        void OnDisable()
        {
            CleanupDragGhosts();
        }

        void OnDestroy()
        {
            CleanupDragGhosts();
            EventCenter.Instance.RemoveEventListener(GameEvent.ProgramEdited, OnProgramEdited);
            if (_undoHandler != null)
            {
                _undoHandler.OnClick -= OnUndoClicked;
                _undoHandler.OnLongPress -= OnUndoLongPressed;
                _undoHandler.OnHoverEnter -= ShowUndoTooltip;
                _undoHandler.OnHoverExit -= HideUndoTooltip;
            }
        }

        // ====== 撤销（2026-08-13：单击撤一步 / 长按全部撤回 / 悬停提示）======

        /// <summary>单击：撤销当前选中棋子上一步（无栈无操作——按钮置灰已防）。</summary>
        void OnUndoClicked()
        {
            if (!CanEditSelected()) return;
            UiSfx.Play(); // 撤销一步碰撞音（2026-08-24 音频挂点方案）
            _editor.Undo(_selectedDefId);
            var def = ConfigTable.Find<PieceDef>(_selectedDefId);
            if (def != null)
            {
                _slotTemplates = GetCurrentProgram(def);
                InitLockedFlags(def);
                FillPieceInfo(def);
                RefreshPieceCardProgram(_selectedDefId);
                RefreshUndoButton();
            }
        }

        /// <summary>长按：弹确认面板"确认全部撤回？" → 全部还原（RestoreAll）+ 清历史。
        /// ⚠️ 2026-08-16：与 RefreshUndoButton 同条件守卫——未选中/无撤销栈时即使事件漏进也不弹框。</summary>
        void OnUndoLongPressed()
        {
            if (!CanEditSelected() || !_editor.CanUndo(_selectedDefId)) return;
            var confirm = FindObjectOfType<ConfirmPanel>(true);
            if (confirm != null)
            {
                confirm.ShowConfirm(new ConfirmViewData("确认全部撤回？"), RestoreAllAndReset);
            }
            else
            {
                RestoreAllAndReset(); // 防御：无确认面板直接还原——行为与确认后一致（含重置）
            }
        }

        /// <summary>全部撤回 + 显式重置（2026-08-13 修复：不依赖 OnShow 时序——确认面板未就绪的降级路径
        /// 曾跳过重置 → 信息区/选中残留；现统一为"重新开始编辑"状态：清选中/清槽/隐藏信息区）。</summary>
        void RestoreAllAndReset()
        {
            if (!CanEditSelected()) return;
            _editor.RestoreAll();
            _selectedDefId = -1; // 清选中（重新开始编辑）
            _slotTemplates.Clear();
            _slotLocked.Clear();
            if (_pieceInfo != null) _pieceInfo.gameObject.SetActive(false); // 隐藏信息区
            // 卡面缩略图由 RestoreAll 的 ProgramEdited 事件驱动刷新（逐棋子）
            RefreshEditorButtons();
        }

        /// <summary>完成与撤回按钮统一跟随当前选中棋子的后端可编辑资格。</summary>
        void RefreshEditorButtons()
        {
            if (_nextBtn != null) _nextBtn.interactable = CanEditSelected();
            RefreshUndoButton();
        }

        /// <summary>空撤销栈 → 置灰（选中变化/每次编辑后/OnShow 刷新）。</summary>
        void RefreshUndoButton()
        {
            if (_undoBtn == null) return;
            _undoBtn.interactable = CanEditSelected() && _editor.CanUndo(_selectedDefId);
        }

        /// <summary>撤回按钮不可用时显示当前选择不足的提示。</summary>
        public class DisabledUndoTooltip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            private const string Message = "存在编辑历史或选定待编辑棋子以激活撤回按钮";
            private Button _button;

            public void Init(Button button)
            {
                _button = button;
            }

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (_button == null || _button.interactable) return;
                var canvas = GetComponentInParent<Canvas>();
                TooltipManager.Instance?.Show(Message, transform.position, canvas != null ? canvas.worldCamera : null);
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                TooltipManager.Instance?.Hide();
            }
        }

        /// <summary>悬停提示浮窗：Addressables 加载通用 TipPanel 预制体（2026-08-13——与行为描述浮窗共用；Txt_Desc 写提示文本）。</summary>
        void ShowUndoTooltip()
        {
            if (_undoBtn == null) return;
            // 2026-08-13 重构：通用 TooltipManager——按钮屏幕坐标提示（根 Canvas 的 worldCamera=UICamera）
            var canvas = _undoBtn.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, _undoBtn.transform.position);
                TooltipManager.Instance.ShowAtScreen(new TooltipViewData("单击撤回一次\n长按全部撤回"), screen);
            }
        }
        void HideUndoTooltip()
        {
            TooltipManager.Instance.Hide();
        }

        void OnNext()
        {
            // 编辑完成 → 结束编辑会话（清全部 EditingDefs 标记——防残留进存档）+ 通知 TowerFlow 推进
            if (!CanEditSelected())
            {
                Debug.LogWarning("[PieceEdit] 尚未选择本次编辑事件指定棋子——无法完成编辑");
                return;
            }
            if (!_editor.EndEdit(_editableDefId))
            {
                // 空程序（至少 1 槽校验失败——2026-08-12 修复）：提示并保持编辑态，防"废棋子"进战斗
                var def = ConfigTable.Find<PieceDef>(_editableDefId);
                Debug.LogWarning($"[PieceEdit] {def?.displayName ?? _editableDefId.ToString()} 程序为空——无法完成编辑，请至少保留 1 个程序块");
                return;
            }
            UiSfx.Play(); // 编辑完成（下一步）按钮碰撞音（2026-08-24 音频挂点方案）
            // ⚠️ 2026-08-26 不隐藏自身：EventCompleted 同步推进 → 下一面板 ShowWithLoading 遮挡下切换（防裸场景）
            EventCenter.Instance.EventTrigger(GameEvent.EventCompleted, _state != null ? _state.CurrentEventId : null); // 推进（携带事件 id——TowerFlow 校验匹配；防重复信号跳节点）
        }

        protected override void OnShow()
        {
            // 2026-08-26 留痕（第 2 关起左右空白排查）：构建链异常不得静默——try/catch 留栈，防"左右空白无日志"难定位。
            try
            {
            // 新局重置：清选中 + 隐藏信息区 + 重建棋子列表（卡面程序缩略图随当前数据刷新——
            // ⚠️ 2026-08-12：RefreshPieceList 原只在 Awake 跑一次，面板常驻跨局复用 → 卡面显示旧局编辑结果）
            _selectedDefId = _editableDefId >= 0 ? _editableDefId : -1;
            _slotTemplates.Clear();
            _slotLocked.Clear(); // 锁定标记与槽同步清（选中后 ShowPieceInfo 重建）
            if (_selectedDefId >= 0)
            {
                BuildProgramLibrary();
                RefreshProgramList();
            }
            if (_pieceInfo != null) _pieceInfo.gameObject.SetActive(false);
            // ⚠️ 2026-08-12：UGUI 组件状态必须【同步】立即重置——BuildPieceList 是异步协程
            // （Addressables 加载 + Destroy 延迟帧末），打开瞬间若依赖它则旧内容/旧选中/旧滚动仍在显示：
            // - Grp_PieceDisplay 卡面：同步 DestroyImmediate 清空（无延迟窗口）
            // - 选中态：ToggleGroup.SetAllTogglesOff（清残留选中高亮）
            // - 滚动条：ScrollRect.normalizedPosition 归零
            if (_pieceContent != null)
            {
                for (int i = _pieceContent.childCount - 1; i >= 0; i--)
                {
                    DestroyImmediate(_pieceContent.GetChild(i).gameObject); // 同步清空（Destroy 延迟帧末→旧卡残留）
                }
                var group = _pieceContent.GetComponent<ToggleGroup>();
                if (group != null) group.SetAllTogglesOff();
                var scroll = _pieceContent.GetComponentInParent<ScrollRect>();
                if (scroll != null) scroll.normalizedPosition = Vector2.zero;
            }
            RefreshPieceList();
            RefreshEditorButtons(); // 新会话：按钮状态与当前可编辑棋子保持一致
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PieceEdit] OnShow 构建链异常（左右列表可能空白）：{e}");
            }
        }

        void ResolveNodes()
        {
            // 列表和程序库仍按面板自身节点绑定；右侧详情统一复用内嵌 Piece_Handcard。
            _pieceContent = transform.Find("Grp/Grp_R/Grp_Pieces/Grp_PieceDisplay/Viewport/Content");
            _programContent = transform.Find("Grp/Grp_Programs/Grp_ProgramDisplay/Viewport/Content");
            _pieceInfo = FindDeep(transform, "Piece_Handcard");

            _slotImages = new Image[4];
            _slotTexts = new TMP_Text[4];
            _slotDescs = new TMP_Text[4];
            if (_pieceInfo == null)
            {
                Debug.LogError("[PieceEdit] 未找到右侧统一详情卡 Piece_Handcard");
                return;
            }

            for (int i = 0; i < 4; i++)
            {
                var img = FindDeep(_pieceInfo, $"Img_InfoProgram{i + 1}");
                _slotImages[i] = img != null ? img.GetComponent<Image>() : null;
                _slotTexts[i] = img != null ? img.GetComponentInChildren<TMP_Text>(true) : null;
                var desc = FindDeep(_pieceInfo, $"Txt_InfoProgram{i + 1}Desc");
                _slotDescs[i] = desc != null ? desc.GetComponent<TMP_Text>() : null;
            }

            var value = FindDeep(_pieceInfo, "Img_InfoValue");
            _infoValueImg = value != null ? value.GetComponent<Image>() : null;
            _infoValueText = value != null ? value.GetComponentInChildren<TMP_Text>(true) : null;
            var type = FindDeep(_pieceInfo, "Img_InfoType");
            _infoTypeImg = type != null ? type.GetComponent<Image>() : null;
            _infoTypeText = type != null ? type.GetComponentInChildren<TMP_Text>(true) : null;
            var name = FindDeep(_pieceInfo, "Txt_InfoName");
            _infoName = name != null ? name.GetComponent<TMP_Text>() : null;
            var portrait = FindDeep(_pieceInfo, "Img_InfoPortrait");
            _infoPortrait = portrait != null ? portrait.GetComponent<Image>() : null;
        }

        // ====== 程序库（当前编辑棋子优先使用后端候选池） ======
        void BuildProgramLibrary()
        {
            _programLibrary.Clear();
            var seen = new HashSet<string>();
            IEnumerable<Template> source = null;

            // 编辑事件中：候选必须由后端按 defId 决定。
            // 未选中棋子/编辑器未就绪时，才回退完整模板库，供面板初始化预览使用。
            if (_editor != null && _selectedDefId >= 0)
            {
                source = _editor.GetEditCandidates(_selectedDefId);
            }
            else
            {
                source = TemplateLibrary.All();
            }

            foreach (var t in source)
            {
                if (t == null) continue;
                var key = SlotDescTable.FeatureOf(t);
                if (seen.Add(key)) _programLibrary.Add(t);
            }

            // 模板库尚未注册时用棋子自带模块兜底，避免初始化阶段候选区完全为空。
            if (_programLibrary.Count == 0 && _selectedDefId < 0)
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

            // 程序块排序：移动 → 攻击 → 效果 → 跳过；同类保持后端候选池顺序。
            _programLibrary.Sort((a, b) =>
            {
                int ta = a is MoveTemplate ? 0 : a is AttackTemplate ? 1 : a is EffectTemplate ? 2 : 3;
                int tb = b is MoveTemplate ? 0 : b is AttackTemplate ? 1 : b is EffectTemplate ? 2 : 3;
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
            try
            {
                yield return BuildPieceListInner();
            }
            finally
            {
                _buildingList = false; // ⚠️ 2026-08-12：所有退出路径（含异常/加载失败 yield break）必须复位——
                // 否则永久 true → 后续 RefreshPieceList 永远跳过 → 卡面永不重建（重开显示旧局）
            }
        }

        System.Collections.IEnumerator BuildPieceListInner()
        {
            // 加载 Piece_Card / Piece_ProgramInfo 模板（Addressables）。预制体负责布局，运行时只添加实例。
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
            // 类型优先（初始→部署→升变）+ 同类型有效价值升序（程序编辑可能改变类型/价值）
            var defs = new List<PieceDef>(ConfigTable.All<PieceDef>());
            defs.Sort((a, b) =>
            {
                int typeOrder = CardTypeColors.TypeOrder(GetEffectiveType(a))
                    .CompareTo(CardTypeColors.TypeOrder(GetEffectiveType(b)));
                return typeOrder != 0
                    ? typeOrder
                    : GetEffectiveValue(a).CompareTo(GetEffectiveValue(b));
            });
            foreach (var def in defs)
            {
                var data = PiecePresentationMapper.ToPieceCard(
                    def,
                    GetEffectiveType(def),
                    GetEffectiveValue(def),
                    GetCurrentProgram(def));
                var go = UIComponentFactory.CreatePieceCard(cardHandle.Result, _pieceContent, data, _progTemplate).gameObject;
                go.name = $"PieceCard_{def.name}";
                BindPieceCardSelection(go, def, group);
            }
            // 三选一已确认：进入面板后自动选中唯一可编辑棋子；其他卡仍可切换查看信息。
            if (_editableDefId >= 0)
            {
                var editableDef = ConfigTable.Find<PieceDef>(_editableDefId);
                if (editableDef != null)
                {
                    foreach (Transform card in _pieceContent)
                    {
                        if (card.name != $"PieceCard_{editableDef.name}") continue;
                        var toggle = card.GetComponent<Toggle>();
                        if (toggle != null) toggle.SetIsOnWithoutNotify(true);
                        SelectPiece(_editableDefId);
                        break;
                    }
                }
            }
            // 滚动位置归零（跨局打开不残留旧滚动）
            var scroll = _pieceContent.GetComponentInParent<ScrollRect>();
            if (scroll != null) scroll.normalizedPosition = Vector2.zero;
        }

        void BindPieceCardSelection(GameObject go, PieceDef def, ToggleGroup group)
        {
            // Toggle 单选：选中 → SelectPiece。
            var toggle = go.GetComponent<Toggle>();
            if (toggle == null) return;
            toggle.group = group;
            var defId = def.Id;
            toggle.onValueChanged.AddListener(on => { if (on) SelectPiece(defId); });
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
            if (_slotImages == null || _slotDescs == null) return list;
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
            if (_slotImages == null || slotIndex < 0 || slotIndex >= 4 || _slotImages[slotIndex] == null) return null;
            var hl = _slotImages[slotIndex].GetComponent<SlotSnapHighlight>();
            if (hl == null) hl = _slotImages[slotIndex].gameObject.AddComponent<SlotSnapHighlight>();
            return hl;
        }

        /// <summary>该槽是否锁定块（不可拖入覆盖——UpdateSnap 命中时不高亮，防"拖入必失败无反馈"）。</summary>
        public bool IsSlotLocked(int slotIndex)
        {
            return slotIndex >= 0 && slotIndex < _slotLocked.Count && _slotLocked[slotIndex];
        }

        /// <summary>
        /// 锁定槽通常拒绝拖入；唯一例外是 show 模式下把缺失的内置模块放回自己的原始槽位。
        /// 是否接受由后端 TryRestoreBuiltinModule 最终裁决，前端不预先修改卡面。
        /// </summary>
        public bool CanAcceptModuleDrop(int slotIndex, Template module, EditorProgramDrag.DragSource source)
        {
            if (!IsSlotLocked(slotIndex)) return true;
            if (source != EditorProgramDrag.DragSource.Library || _editor == null || _selectedDefId < 0 || module == null)
                return false;
            return IsBuiltinRestoreCandidate(module, slotIndex);
        }

        bool IsBuiltinRestoreCandidate(Template module, int targetIndex)
        {
            if (EditConfig.IsHideMode) return false;
            var def = ConfigTable.Find<PieceDef>(_selectedDefId);
            if (def?.programSet == null || def.programSet.Count == 0) return false;
            var defaults = def.programSet[0].slots;
            if (targetIndex < 0 || targetIndex >= defaults.Count) return false;
            var original = defaults[targetIndex];
            if (!IsBuiltinModule(original) || original.GetType() != module.GetType() || original.id != module.id)
                return false;
            foreach (var current in _slotTemplates)
            {
                if (current != null && current.GetType() == module.GetType() && current.id > 0 && current.id == module.id)
                    return false;
            }
            return true;
        }

        static bool IsBuiltinModule(Template module)
        {
            switch (module)
            {
                case MoveTemplate move: return move.id > 0 && move.id <= 9;
                case AttackTemplate attack: return attack.id > 0 && attack.id <= 11;
                case EffectTemplate effect: return effect.id > 0 && effect.id <= 3;
                default: return false;
            }
        }

        /// <summary>刷新指定棋子卡的完整 DTO（有效类型、价值、当前程序）。</summary>
        void RefreshPieceCardBase(int defId)
        {
            RefreshPieceCard(defId);
        }

        /// <summary>刷新指定棋子卡的完整 DTO（有效类型、价值、当前程序）。</summary>
        void RefreshPieceCardProgram(int defId)
        {
            RefreshPieceCard(defId);
        }

        void RefreshPieceCard(int defId)
        {
            if (_pieceContent == null) return;
            var def = ConfigTable.Find<PieceDef>(defId);
            if (def == null) return;
            foreach (Transform card in _pieceContent)
            {
                if (card.name != $"PieceCard_{def.name}") continue;
                var view = card.GetComponent<PieceCardView>();
                if (view == null) view = card.gameObject.AddComponent<PieceCardView>();
                view.Bind(PiecePresentationMapper.ToPieceCard(
                    def,
                    GetEffectiveType(def),
                    GetEffectiveValue(def),
                    GetCurrentProgram(def)), _progTemplate);
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
            _programListGeneration++;
            // 程序库 Content/GridLayout 使用预制体既有排版；运行时只清理并追加 Program_Card 实例。
            foreach (Transform child in _programContent) Destroy(child.gameObject);
            // 程序库卡 = Program_Card 预制体（李毕编排：Img_ProgramType 类型图标 + Txt_ProgramCount + Txt_ProgramDesc）——异步加载后填充
            StartCoroutine(BuildProgramList());
        }

        System.Collections.IEnumerator BuildProgramList()
        {
            int generation = _programListGeneration;
            if (_programCardTemplate == null)
            {
                if (_loadingProgramCardTemplate)
                {
                    while (_loadingProgramCardTemplate) yield return null;
                }
                else
                {
                    _loadingProgramCardTemplate = true;
                    var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("Program_Card");
                    yield return handle;
                    _loadingProgramCardTemplate = false;
                    if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded)
                    {
                        _programCardTemplate = handle.Result;
                    }
                }
            }
            if (generation != _programListGeneration) yield break;
            if (_programCardTemplate == null)
            {
                Debug.LogWarning("[PieceEdit] Program_Card 加载失败——程序库为空");
                yield break;
            }
            foreach (var slot in _programLibrary)
            {
                var go = UIComponentFactory.CreateProgramCard(
                    _programCardTemplate,
                    _programContent,
                    PiecePresentationMapper.ToProgramCard(slot)).gameObject;
                go.name = $"Prog_{SlotDescTable.FeatureOf(slot)}";
                // 拖拽源：程序库块（Library 模式——复制放置，原卡不消耗）
                var drag = go.AddComponent<EditorProgramDrag>();
                drag.Init(this, slot, EditorProgramDrag.DragSource.Library, -1);
                drag.SetDraggable(CanEditSelected());
            }

        }

        private void RefreshProgramDragPermission()
        {
            if (_programContent == null) return;
            bool allowed = CanEditSelected();
            foreach (var drag in _programContent.GetComponentsInChildren<EditorProgramDrag>(true))
            {
                drag.SetDraggable(allowed);
            }
        }

        /// <summary>程序编辑落账（ProgramEdited 事件）→ 刷新有效卡面与详情。</summary>
        void OnProgramEdited(object data)
        {
            if (data is int editedDefId)
            {
                RefreshPieceCardBase(editedDefId);
                if (_selectedDefId == editedDefId)
                {
                    var def = ConfigTable.Find<PieceDef>(editedDefId);
                    if (def != null) FillPieceInfo(def);
                    BuildProgramLibrary();
                    RefreshProgramList(); // show/hide 语义变化后重新查询后端候选池
                }
            }
        }

        // ====== 选中棋子 ======
        void SelectPiece(int defId)
        {
            _selectedDefId = defId;
            var def = ConfigTable.Find<PieceDef>(defId);
            if (def == null) return;
            if (CanEditSelected())
            {
                _editor.BeginEdit(defId); // 编辑会话：记录初始快照
            }
            _slotTemplates = GetCurrentProgram(def);
            InitLockedFlags(def);
            FillPieceInfo(def);
            BuildProgramLibrary();          // 后端候选池按当前 defId 查询
            RefreshProgramList();           // 切换棋子后立即刷新候选区
            RefreshProgramDragPermission();
            RefreshEditorButtons(); // 新选中：完成与撤回均跟随当前可编辑资格
        }

        List<Template> GetCurrentProgram(PieceDef def)
        {
            if (_state.TryGetCurrentProgram(def.Id, out var edited)) return new List<Template>(edited);
            if (def.programSet != null && def.programSet.Count > 0) return new List<Template>(def.programSet[0].slots);
            return new List<Template>();
        }

        /// <summary>锁定标记（2026-08-24 定案合并）：移动/攻击默认槽全部可编辑（解除锁定）；效果槽锁定（「取消效果编辑」——默认内置效果保留原位、不可移除/替换，见 docs/后端待办.md）；回库=移动/攻击，由后端差集动态化负责。</summary>
        void InitLockedFlags(PieceDef def)
        {
            _slotLocked.Clear();
            for (int i = 0; i < _slotTemplates.Count; i++)
            {
                _slotLocked.Add(_slotTemplates[i] is EffectTemplate); // 仅效果槽锁定；移动/攻击全可编辑
            }
        }

        void FillPieceInfo(PieceDef def)
        {
            if (_pieceInfo == null || _slotImages == null || _slotDescs == null)
            {
                Debug.LogError("[PieceEdit] 右侧统一详情卡未正确初始化，无法刷新棋子信息");
                return;
            }

            var effectiveType = GetEffectiveType(def);
            var handCardView = _pieceInfo.GetComponent<HandCardView>();
            if (handCardView == null) handCardView = _pieceInfo.gameObject.AddComponent<HandCardView>();
            handCardView.Bind(PiecePresentationMapper.ToHandCard(
                def,
                effectiveType,
                GetEffectiveValue(def),
                _slotTemplates));

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
                    bool draggable = CanEditSelected() && has && !_slotLocked[i];
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
                    // 有图标只显图标（移动/攻击）；无图标（效果/未知）才写字——2026-08-26 图标接入防重叠
                    // （槽位 Image 恒有 Bg 背景兜底——判据用图标键，不能用 sprite 是否为空）
                    if (_slotTexts[i] != null)
                        _slotTexts[i].text = PiecePresentationMapper.ProgramIconKey(t) != null ? string.Empty : SlotTypeChar(t);
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
            _pieceInfo.gameObject.SetActive(true);
        }

        // ====== 程序编排（2026-08-24 起移动/攻击默认槽全可编辑：替换/插入/移除/重排；效果槽锁定——整组提交） ======

        /// <summary>程序槽位上限（当前方案固定 4——策划变更时改此处即可；ProgramDef.slots 本身是 List 无硬上限）。</summary>
        private const int MaxProgramSlots = 4;

        /// <summary>
        /// 拖入到槽 to（2026-08-11 需求对齐 v2）：
        /// - 目标锁定槽 → 拒绝（效果槽锁定——「取消效果编辑」定案；移动/攻击槽不受限）
        /// - 程序有空缺（Count &lt; MaxProgramSlots）→ **插入 to 位置**（原 to 及之后顺移，空位补齐——如 [锁a 锁b c 空] 拖 x 到 c → [锁a 锁b x c]）
        /// - 程序满 → 替换 to 槽（原块回程序库——无限复制语义下无额外动作）
        /// ⚠️ 2026-08-12：原 Clamp(to,0,4) 满槽时 to=4 会索引越界（UI 只传 0-3 未触发）——改用 Count 动态处理，不依赖硬编码 4。
        /// </summary>
        public bool InsertProgram(int to, Template template)
        {
            if (!CanEditSelected() || template == null) return false;

            // show 模式：候选区中的缺失内置模块只能回到默认原槽。
            // 后端先校验并落账；false 时保持当前 UI，不播放成功态。
            if (IsBuiltinRestoreCandidate(template, to))
            {
                bool ok = _editor.TryRestoreBuiltinModule(_selectedDefId, template, to);
                if (ok) UiSfx.Play(); // 内置模块回原槽碰撞音（2026-08-24 音频挂点方案）
                return ok;
            }

            if (_slotTemplates.Count >= MaxProgramSlots)
            {
                // 满槽：替换目标槽（索引必须在 0..Count-1 内）
                to = Mathf.Clamp(to, 0, _slotTemplates.Count - 1);
                if (to < _slotLocked.Count && _slotLocked[to]) return false; // 锁定槽拒绝
                _slotTemplates[to] = template; // 锁定标记不变——原块本就非锁定
            }
            else
            {
                // 有空缺：插入 to（0..Count——顺移，空缺补齐）
                to = Mathf.Clamp(to, 0, _slotTemplates.Count);
                if (to < _slotLocked.Count && _slotLocked[to]) return false; // 锁定槽拒绝
                _slotTemplates.Insert(to, template);
                _slotLocked.Insert(to, false); // 新块不锁定
            }
            CommitProgram();
            return true;
        }

        /// <summary>
        /// 槽间重排（2026-08-12 需求修正：用户实测发现插入语义方向不对称——紧邻上拖下=原位无变化）。
        /// 现语义：目标槽有块 → **交换（对调）**；目标空缺（末尾）→ 插入追加；锁定块不可拖出/不可作目标（2026-08-24 起移动/攻击槽可拖；效果槽仍锁定）。
        /// </summary>
        public bool MoveProgram(int from, int to)
        {
            if (!CanEditSelected() || from < 0 || from >= _slotTemplates.Count) return false;
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

        /// <summary>移除槽位块（拖出到空白）。锁定块不可移除（效果槽锁定——「取消效果编辑」）。</summary>
        public bool RemoveProgramAt(int index)
        {
            if (!CanEditSelected() || index < 0 || index >= _slotTemplates.Count) return false;
            if (_slotLocked[index]) return false;
            _slotTemplates.RemoveAt(index);
            _slotLocked.RemoveAt(index); // 锁定标记同步
            CommitProgram();
            return true;
        }

        void CommitProgram()
        {
            if (!CanEditSelected()) return;
            _editor.EditProgram(_selectedDefId, new List<Template>(_slotTemplates));
            UiSfx.Play(); // 编辑拖放落账（插入/重排/替换/移除）碰撞音（2026-08-24 音频挂点方案）
            RefreshUndoButton(); // 编辑后必有可撤销历史 → 亮
            var def = ConfigTable.Find<PieceDef>(_selectedDefId);
            if (def != null)
            {
                FillPieceInfo(def);
                RefreshPieceCardProgram(_selectedDefId); // 左列卡面缩略图同步
            }
        }

        // ====== 工具 ======
        int GetEffectiveValue(PieceDef def)
        {
            if (def == null) return 0;
            return _state != null ? _state.GetEffectiveValue(def.Id) : def.value;
        }

        PieceType GetEffectiveType(PieceDef def)
        {
            if (def == null) return PieceType.Initial;
            return _state != null ? _state.GetEffectiveType(def.Id) : def.pieceType;
        }

        static string PieceTypeChar(PieceType type)
        {
            return type == PieceType.Initial ? "始" : type == PieceType.Deployable ? "部" : "升";
        }

        static string SlotTypeChar(Template t)
        {
            switch (t)
            {
                case MoveTemplate: return "移";
                case AttackTemplate: return "攻";
                case EffectTemplate: return "效";
                case SkipTemplate: return "跳";
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
                case EffectTemplate: return "效：被动效果";
                case SkipTemplate: return "跳：跳过";
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
            // 防孤儿：新拖拽开始先清面板登记的残留幽灵 + 按名清理本 canvas 下未托管幽灵（既有脏数据也清）
            if (_panel != null) _panel.CleanupDragGhosts();
            foreach (var orphan in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
            {
                if ((orphan.name == "ProgDragGhost" || orphan.name == "SlotDragGhost")
                    && orphan.transform.IsChildOf(canvas.transform))
                {
                    Destroy(orphan);
                }
            }
            _ghost = Instantiate(ghostSource.gameObject, canvas.transform);
            _ghost.name = _source == DragSource.Library ? "ProgDragGhost" : "SlotDragGhost";
            if (_panel != null) _panel.RegisterDragGhost(_ghost);
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
            var g = _ghost;
            _ghost = null;
            if (_panel != null) _panel.UnregisterDragGhost(g);
            else if (g != null) Destroy(g);
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
                if (!_panel.CanAcceptModuleDrop(target.SlotIndex, _template, _source)) continue; // 锁定槽仅允许内置模块回原位
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
            var g = _ghost;
            _ghost = null;
            if (_panel != null) _panel.UnregisterDragGhost(g);
            else if (g != null) Destroy(g);
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
