using System;
using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace TheLaw.UI
{
    /// <summary>
    /// 启动装配器（入口，不算层）：创建管理器 + 注册快照 + 事件接线 + 进主菜单（TODO）。
    /// 唯一纪律：只管创建 + 接线，不暴露实例查询（非 ServiceLocator）。
    /// 代码放 UI 程序集（零新增依赖——UI 引用全部层）；挂在启动场景根节点。
    /// </summary>
    public class Bootstrap : MonoBehaviour
    {
        // ========== 配置资产引用（方案 A：编辑器拖拽——Awake 时注册进 ConfigTable）==========
        [Header("配置资产（编辑器拖拽）")]
        [SerializeField] private List<PieceDef> _pieceConfigs = new List<PieceDef>();
        [SerializeField] private List<SpecialAbilityDef> _abilityConfigs = new List<SpecialAbilityDef>();
        [SerializeField] private List<FloorConfig> _floorConfigs = new List<FloorConfig>();
        [SerializeField] private List<MapConfig> _mapConfigs = new List<MapConfig>();
        [SerializeField] private List<AIParams> _aiParamConfigs = new List<AIParams>();
        [SerializeField] private List<EventPool> _eventPoolConfigs = new List<EventPool>();
        [SerializeField] private List<EventDefinition> _eventConfigs = new List<EventDefinition>(); // 事件定义（EventOpened → FindByName 查询——须注册）
        [SerializeField] private List<RelicDef> _relicConfigs = new List<RelicDef>();
        [SerializeField] private List<TemplateDef> _templateConfigs = new List<TemplateDef>(); // 程序块模板库（编辑界面候选池）

        [Header("测试开关")]
        [Tooltip("true=启动直进战斗（跳过主菜单），false=正常主菜单流程")]
        [SerializeField] private bool _directToBattle = false; // 默认主菜单（对接期测试直进可临时开）

        [Header("程序块描述表（结构特征码→描述；未命中回退代码生成）")]
        [SerializeField] private TextAsset _slotDescriptions;

        [Header("buff 描述表（key→名称/描述；护盾/免费行动/临时能力）")]
        [SerializeField] private TextAsset _buffDescriptions;

        [Header("槽位价值表（模板→价值——棋子价值/类型推导，2026-08-15 策划新案）")]
        [SerializeField] private TextAsset _slotValues;

        // 编辑规则配置（edit-config.json——Resources 自动加载，无需拖拽字段；见 EditConfig.AutoLoad）

        // 普通类实例（去单例化后由 Bootstrap 创建并持有；规则层行为类显式传递避免网状耦合）
        private static Bootstrap _instance; // 静态实例标记：防重（双实例并存时先到者存活，后到者自毁）
        private UIManager _uiManager;
        private TutorialSystem _tutorialSystem;
        private ProgressSystem _progressSystem;
        private GameState _gameState;
        private BoardRules _boardRules;
        private IntentResolver _intentResolver;
        private Resolver _resolver;
        private RelicSystem _relicSystem;
        private EnemyAI _enemyAI;
        // BattleFlow 不再常驻（2026-08-13 战斗级"进入创建、离开销毁"——经 TowerFlow 创建/持有/销毁）
        private Func<BattleFlow> _battleFlowFactory; // 战斗级工厂（每场战斗创建新 BattleFlow——无状态可常驻）
        private EditorSession _editorSession;   // 整局级"进入创建、离开销毁"（2026-08-13——每局创建/销毁，防跨局残留）
        private PieceEditPanel _pieceEditPanel; // 棋子编辑面板（新局入口——编辑完成进战斗）
        private EditCandidatePanel _editCandidatePanel; // 编辑事件三选一面板（局内）
        private EventPanel _eventPanel;         // 事件关面板（EventOpened 显示）
        private DeckBuildPanel _deckBuildPanel; // 牌组构筑面板（StateChanged("deck") 显示）
        private EventNodeSystem _eventNodeSystem;
        private TowerFlow _towerFlow;
        private BattleResultPanel _battleResultPanel; // 结算面板（战斗结束 overlay——常驻，自身监听 StateChanged）

        private void Awake()
        {
            // DOTween 容量：默认 200/50 快速操作会扩容警告，起步调大
            DG.Tweening.DOTween.SetTweensCapacity(500, 125);
            // 日志 Default：safe mode 只报汇总，具体 tween 错误需 Default 才能看到明细（排障用）
            DG.Tweening.DOTween.logBehaviour = DG.Tweening.LogBehaviour.Default;
            // 防重：静态实例标记（先到者存活，后到者自毁——Destroy 延迟到帧末，双实例并存时双方都看到对方会双双销毁）
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject); // 常驻（存档/管理器生命周期跟随进程）
            // ① 按依赖顺序创建常驻管理器
            CreateManagers();
            // ② 加载配置（TODO: Addressables）
            LoadConfigs();
            // ②a 预加载棋子美术立绘（Addressables——缺资源回退占位）
            PieceViewFactory.PreloadPortraits();
            // ②b 程序块描述表（数据驱动——UI 槽位描述）
            SlotDescTable.Load(_slotDescriptions);
            // ②c buff 描述表（数据驱动——UI buff 区显示名）
            BuffDescTable.Load(_buffDescriptions);
            // ②d 槽位价值表（数据驱动——棋子价值/类型推导；未加载 = 推导按 0 降级 + 警告）
            PieceValue.Load(_slotValues);
            // ②e 编辑规则配置（被替换模块展示策略两方案切换——Resources 自动加载，免拖拽；未找到 = show 模式）
            EditConfig.AutoLoad();
            // ③ 创建规则层（依赖注入）
            CreateGameplay();
            // ④ 注册存档快照
            RegisterSnapshots();
            SaveManager.Instance.LoadAll(); // 启动读档：音量/显示等设置跨启动生效（SettingsChanged 触发 AudioManager/ScreenSettingsApplier）
            // ⑤ 事件接线（进层/开战存档、RunEnded）
            WireEvents();
            // ⑤a 结算面板常驻创建（战斗结束 overlay——自身监听 StateChanged + PushPanel；须在战斗前就绪）
            StartCoroutine(CreateBattleResultPanel());
            // ⑤a2 确认面板常驻创建（通用确认 overlay——撤回全部/退出/重开等场景复用；IsPausing 暂停型）
            StartCoroutine(CreateConfirmPanel());
            // ⑤a3 获取物品弹窗常驻创建（RelicObtained 统一提示——2026-08-14：取代事件面板描述区追加）
            StartCoroutine(CreateItemGettingPanel());
            // ⑤a4 设置面板常驻创建（overlay——主菜单/战斗中入口复用；IsPausing 暂停型）
            StartCoroutine(CreateSettingsPanel());
            // ⑤b 开局初始化（新局状态——含基础牌组手牌填充，ResetForNewRun 内完成）
            _gameState.ResetForNewRun();
            // ⑥ 进主菜单（TODO: UI 层面板）
            EnterMainMenu();
        }

        // ========== ① 创建管理器 ==========

        private void CreateManagers()
        {
            // 单例类（首次访问自动创建）：EventCenter / SaveManager / RandomManager / SettingsSystem / AudioManager / GameState
            _gameState = GameState.Instance;
            _ = AudioManager.Instance; // 实例化音频管理器（监听音量设置——不实例化则音量永远不生效）
            var applierGo = new GameObject("ScreenSettingsApplier");
            applierGo.AddComponent<ScreenSettingsApplier>(); // 显示设置（全屏/分辨率）全局应用器
            DontDestroyOnLoad(applierGo); // 常驻（跟随进程；设置跨启动生效）——MonoBehaviour 直接调用（2026-08-19：原 Object. 与 using System 歧义）
            // 普通类（去单例化——显式创建）
            _uiManager = new UIManager();
            _tutorialSystem = new TutorialSystem();
            _progressSystem = new ProgressSystem();
        }

        // ========== ② 加载配置（方案 A：Inspector 拖拽引用 → 注册进 ConfigTable）==========

        private void LoadConfigs()
        {
            RegisterAll(_pieceConfigs);
            RegisterAll(_abilityConfigs);
            RegisterAll(_floorConfigs);
            RegisterAll(_mapConfigs);
            RegisterAll(_aiParamConfigs);
            RegisterAll(_eventPoolConfigs);
            RegisterAll(_eventConfigs);
            RegisterAll(_relicConfigs);
            // 程序块模板库（独立注册表——按"种类+编号"查询，编辑界面候选池）
            foreach (var def in _templateConfigs)
            {
                if (def != null)
                {
                    TemplateLibrary.Register(def);
                }
            }
            Debug.Log($"[Bootstrap] 配置注册完成：棋子 {_pieceConfigs.Count} / 能力 {_abilityConfigs.Count} / 层 {_floorConfigs.Count} / 地图 {_mapConfigs.Count} / AI {_aiParamConfigs.Count} / 事件池 {_eventPoolConfigs.Count} / 事件 {_eventConfigs.Count} / 遗物 {_relicConfigs.Count} / 模板 {_templateConfigs.Count}");
        }

        /// <summary>批量注册配置（重复 Id 由 ConfigTable.Register 断言拦截）。</summary>
        private static void RegisterAll<T>(List<T> configs) where T : GameConfigBase
        {
            foreach (var config in configs)
            {
                if (config != null)
                {
                    ConfigTable.Register(config);
                }
            }
        }

        // ========== ③ 创建规则层（依赖注入顺序）==========

        private void CreateGameplay()
        {
            _boardRules = new BoardRules();
            _intentResolver = new IntentResolver(_boardRules);
            _resolver = new Resolver(_gameState, _boardRules);
            _relicSystem = new RelicSystem(_gameState, _resolver);
            _enemyAI = new EnemyAI(_intentResolver, GetDefaultAIParams());
            // 战斗级"进入创建、离开销毁"（2026-08-13）：BattleFlow 不再常驻——工厂注入 TowerFlow，每场战斗创建新实例
            _battleFlowFactory = () => new BattleFlow(_gameState, _boardRules, _intentResolver, _resolver, _enemyAI, _relicSystem);
            // 整局级"进入创建、离开销毁"（2026-08-13）：EditorSession/EventNodeSystem/TowerFlow 每局创建
            // （StartNewGame 时 CreateSessionFlow）——瞬态随实例归零，跨局残留从结构消除
        }

        /// <summary>
        /// 整局级"进入创建"（2026-08-13）：每局创建 EditorSession/EventNodeSystem/TowerFlow——
        /// 快照/撤销栈/消费守卫/监听随实例归零（不再依赖 ResetSession 手清清单）。
        /// ⚠️ 已加载面板持有的规则层引用必须刷新到新实例（面板局内复用——否则旧引用跨局残留）。
        /// </summary>
        private void CreateSessionFlow()
        {
            _editorSession = new EditorSession(_gameState, _resolver);
            _eventNodeSystem = new EventNodeSystem(_gameState, _resolver);
            _towerFlow = new TowerFlow(_gameState, _eventNodeSystem, _battleFlowFactory, GetMapConfig());
            RefreshSessionPanelRefs();
        }

        /// <summary>整局级"离开销毁"（2026-08-13）：注销监听 + 置空——新局懒加载自动重建。</summary>
        private void DisposeSessionFlow()
        {
            _towerFlow?.Dispose(); // 注销 EventCompleted 监听 + 销毁当前战斗（防幽灵回调）
            _towerFlow = null;
            _eventNodeSystem = null;
            _editorSession = null;
        }

        /// <summary>刷新已加载面板的规则层引用（规则层每局重建后必须重新 Init——否则面板持旧实例）。</summary>
        private void RefreshSessionPanelRefs()
        {
            if (_eventPanel != null) _eventPanel.Init(_eventNodeSystem);
            if (_editCandidatePanel != null) _editCandidatePanel.Init(_gameState);
            if (_pieceEditPanel != null) _pieceEditPanel.Init(_editorSession, _gameState);
            if (_deckBuildPanel != null) _deckBuildPanel.Init(_resolver, _gameState);
        }

        private AIParams GetDefaultAIParams()
        {
            foreach (var ai in ConfigTable.All<AIParams>())
            {
                return ai;
            }
            return ScriptableObject.CreateInstance<AIParams>(); // 无配置时默认值兜底（SO 不能用 new——CreateInstance）
        }

        private MapConfig GetMapConfig()
        {
            foreach (var map in ConfigTable.All<MapConfig>())
            {
                return map;
            }
            return null; // 配置未加载（TODO）——TowerFlow 需在配置就绪后使用
        }

        // ========== ④ 注册快照 ==========

        private void RegisterSnapshots()
        {
            var saveManager = SaveManager.Instance;
            saveManager.RegisterSnapshot(_gameState);
            saveManager.RegisterSnapshot(RandomManager.Instance);
            saveManager.RegisterSnapshot(SettingsSystem.Instance);
            saveManager.RegisterSnapshot(_tutorialSystem);
            saveManager.RegisterSnapshot(_progressSystem);
        }

        // ========== ⑤ 事件接线 ==========

        private void WireEvents()
        {
            // 整局结束：清档 → 回主菜单
            EventCenter.Instance.AddEventListener(GameEvent.RunEnded, OnRunEnded);
            // 战斗结算：EndBattle 发 StateChanged(winner)——GameOver 阶段才处理
            EventCenter.Instance.AddEventListener(GameEvent.StateChanged, OnStateChanged);
            // 事件关：EventOpened → 显示事件面板（选项交互 → EventCompleted 推进）
            EventCenter.Instance.AddEventListener(GameEvent.EventOpened, OnEventOpened);
            // 编辑事件：后端抽取三选一候选 → 显示候选面板（替代旧 StateChanged("edit")）
            EventCenter.Instance.AddEventListener(GameEvent.EditCandidatesDrawn, OnEditCandidatesDrawn);
            // 战斗开始：TowerFlow 开战（Phase→Placement）→ 创建战斗控制器
            EventCenter.Instance.AddEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
            // TODO: 进层/开战存档（SaveManager.SaveAll 触发时机——关键事件存档）
        }

        void OnDestroy()
        {
            // 大审查 O1：订阅/退订对称（常驻对象仅退出时触发——防御性）
            if (EventCenter.Instance == null) return;
            EventCenter.Instance.RemoveEventListener(GameEvent.RunEnded, OnRunEnded);
            EventCenter.Instance.RemoveEventListener(GameEvent.StateChanged, OnStateChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.EventOpened, OnEventOpened);
            EventCenter.Instance.RemoveEventListener(GameEvent.EditCandidatesDrawn, OnEditCandidatesDrawn);
            EventCenter.Instance.RemoveEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
        }

        private void OnPhaseChanged(object data)
        {
            // 战斗开始（TowerFlow.StartBattle → Placement）→ 创建战斗控制器（幂等）
            if (_gameState.Phase == BattlePhase.Placement)
            {
                if (GameObject.Find("BattleController") == null)
                {
                    CreateBattleController();
                    AudioManager.Instance.PlayBGM(TheLaw.Core.AudioRefs.BgmBattle); // 战斗 BGM（占位；切曲走交叉淡化）
                }
            }
        }

        /// <summary>当前战斗实例（BattleFlow 每场创建——经 TowerFlow 取；非战斗中为 null）。</summary>
        private BattleFlow CurrentBattleFlow => _towerFlow != null ? _towerFlow.CurrentBattleFlow : null;

        private string _pendingEventId; // 缓存当前事件 id（懒加载完成后主动推给面板——防首次事件丢失）

        private void OnEventOpened(object data)
        {
            _pendingEventId = data as string;
            OpenEventPanel();
        }

        private void OnEditCandidatesDrawn(object data)
        {
            if (_gameState == null || _gameState.EditCandidates == null || _gameState.EditCandidates.Count == 0)
            {
                Debug.LogWarning("[Bootstrap] 收到 EditCandidatesDrawn 但候选为空");
                return;
            }
            OpenEditCandidatePanel();
        }

        /// <summary>
        /// 公共懒加载：CreateAsync → WaitUntil → onReady（大审查 R2：5 个面板加载协程合并——模式统一、防漂移）。
        /// ⚠️ 2026-08-12 in-flight 锁：加载期间字段仍为 null——第二次请求会再启动加载（双实例双监听）；
        /// 按类型记录"加载中"，重复请求直接忽略（5 个面板统一受益）。
        /// </summary>
        private readonly HashSet<string> _loadingPanels = new HashSet<string>(); // 加载中的面板（防重入）

        private System.Collections.IEnumerator LoadPanelAsync<T>(System.Action<T> onReady) where T : PanelBase
        {
            string key = typeof(T).Name;
            if (!_loadingPanels.Add(key))
            {
                yield break; // 已在加载中——忽略重复请求
            }
            bool done = false;
            T panel = null;
            PanelBase.CreateAsync<T>(p => { panel = p; done = true; });
            yield return new WaitUntil(() => done);
            _loadingPanels.Remove(key);
            if (onReady != null) onReady(panel);
        }

        private void OpenEventPanel()
        {
            if (_eventPanel == null)
            {
                StartCoroutine(LoadEventPanel());
            }
            else
            {
                // 面板已存在：其自身监听 EventOpened 处理（Bootstrap 不再主动推——防双消费双推进）
                _uiManager.ShowPanel("EventPanel");
            }
        }

        private System.Collections.IEnumerator LoadEventPanel()
        {
            yield return LoadPanelAsync<EventPanel>(panel =>
            {
                _eventPanel = panel;
                _uiManager.RegisterPanel(panel);
                panel.Init(_eventNodeSystem);
                panel.OnSettingsClicked += () => _uiManager.PushOverlay("Settings"); // 事件关设置入口
                panel.ShowEvent(_pendingEventId); // 主动推首次事件数据（面板注册晚于事件广播——否则显示预制文本/选项无响应）
                _uiManager.ShowPanel("EventPanel");
                Debug.Log($"[Bootstrap] 事件面板已显示（event={_pendingEventId}）");
            });
        }

        private void OnStateChanged(object data)
        {
            // 字符串信号：摆放未完成（BattleController 处理）/ 编辑（打开编辑面板）/ 构筑（一版跳过）
            if (data is string s)
            {
                if (s == "edit")
                {
                    OpenPieceEditor(); // 事件关：编辑棋子行为（固定链第 1 步）
                }
                else if (s == "deck")
                {
                    OpenDeckBuild(); // 事件关：牌组构筑（固定链第 2 步）
                }
                return;
            }
            // 战斗结算：GameOver 携带胜方 → TowerFlow 收尾（胜利推进/失败结束——RunEnded 驱动回主菜单）
            if (_gameState.Phase != BattlePhase.GameOver) return;
            if (!(data is Side winner)) return;
            Debug.Log($"[Bootstrap] 战斗结束（{(winner == Side.Player ? "胜利" : "失败")}）→ TowerFlow 收尾");
            _towerFlow?.OnBattleEnded(winner); // null 防御（整局级销毁后异常路径——正常局内必非空）
        }

        /// <summary>
        /// 回主菜单（收尾链）：延后一帧执行——Reset 移出事件回调栈（收尾是"流程结束后的事"，
        /// 不在 EndBattle/RunEnded 的同步回调栈内执行——防御永不被打断，_battleEnded 补丁已退休）。
        /// 顺序：Destroy 战斗会话 → Reset（清档）→ SaveAll（存空=清档）→ 显示主菜单。
        /// </summary>
        private void BackToMainMenu()
        {
            StartCoroutine(FinalizeRun());
        }

        private bool _finalizing; // 收尾防重：执行中不再排队（完成后复位——下一局收尾可用）

        private System.Collections.IEnumerator FinalizeRun()
        {
            if (_finalizing)
            {
                yield break; // 收尾进行中——防重复执行（UI 审阅②：完成后复位）
            }
            _finalizing = true;
            yield return null; // 等一帧：当前事件回调栈必然已退出（Unity 单线程，帧末栈空）
            DestroyBattleController();
            DisposeSessionFlow(); // 整局级"离开销毁"（2026-08-13：注销监听 + 置空——含战斗实例销毁）
            SaveManager.Instance.ArchiveHistory(); // 局终归档（2026-08-13：Reset 前——存局终完整状态含回放，排查可回溯；保留 N 份超量清理）
            _gameState.ResetForNewRun();
            SaveManager.Instance.SaveAll();
            // 2026-08-13：局结束销毁会话面板（P4 断链补全——替代隐藏；新局懒加载重建，防跨局残留）
            DestroySessionPanels();
            // 结算面板不在 UIManager 栈（BattlePanel/EventPanel 直接 Show）——ShowPanel 不影响它；
            // 失败路径：MainMenu 显示在结算面板下层，玩家确认后结算关闭露出 MainMenu
            _uiManager.ShowPanel("MainMenu");
            _finalizing = false; // 复位——下一局收尾可用
            Debug.Log("[Bootstrap] 返回主菜单（收尾链完成）");
        }

        /// <summary>
        /// 销毁战斗会话（BattleController 连带销毁战斗面板——防多局累积实例）。
        /// ⚠️ 用 DestroyImmediate：Destroy 延迟到帧末——重开新局时旧 BattleController 的 OnDestroy（事件反注册）
        /// 尚未执行，旧监听仍会收到 PhaseChanged（对象已伪 null → StartCoroutine 崩），且 GameObject.Find 还能找到
        /// 旧对象导致不创建新控制器（面板/滚动条沿用旧的）。立即销毁同步反注册，消除延迟窗口（2026-08-12）。
        /// </summary>
        private void DestroyBattleController()
        {
            var old = GameObject.Find("BattleController");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
        }

        private void OnRunEnded(object data)
        {
            bool victory = data is bool b && b;
            Debug.Log($"[Bootstrap] 整局结束 victory={victory}");
            // 2026-08-12：挂起收尾——结算面板确认前保持战斗场景（玩家看结算时下层仍是战斗界面）
            // 确认后（BattleResultPanel.OnConfirmed）才 BackToMainMenu：销毁战斗 → Reset → 主界面
            // ⚠️ 2026-08-13 防御：面板未就绪（极端时序）直接收尾——防 _pendingFinalize 永久挂起卡死
            // （正常路径面板必然就绪——创建于启动时、战斗数回合后才结束；未就绪=宁可跳过结算不卡死）
            if (_battleResultPanel != null)
            {
                _pendingFinalize = true;
            }
            else
            {
                BackToMainMenu();
            }
        }

        private bool _pendingFinalize; // 结算确认后待执行的收尾（失败/通关——确认前保持战斗场景）

        // ========== ⑥ 主菜单 / 测试直进战斗 ==========

        private void EnterMainMenu()
        {
            if (_directToBattle)
            {
                // 测试模式：主场景直进战斗（测试流程已迁移进 Main 场景，不再跨场景加载）
                StartCoroutine(EnterBattleTest());
            }
            else
            {
                AudioManager.Instance.PlayBGM(TheLaw.Core.AudioRefs.BgmMenu); // 主菜单 BGM（占位）
                StartCoroutine(LoadMainMenu());
            }
        }

        /// <summary>测试模式：直进战斗（跳过主菜单——与正式新局共用 StartNewGame 流程）。</summary>
        private System.Collections.IEnumerator EnterBattleTest()
        {
            yield return Addressables.InitializeAsync();
            StartNewGame();
        }

        /// <summary>
        /// 新局：重置状态（基础牌组填手牌）→ 直接进入爬塔节点序列（事件→编辑事件→构筑事件→战斗——固定链）。
        /// ⚠️ 2026-08-12：先强制隐藏会话面板——UIManager.ShowPanel 幂等早退（_current==key && IsVisible）
        /// 会跳过 OnShow → 上局停留在编辑/构筑界面时状态残留（选中/槽位/列表）。隐藏后 _current 置空，
        /// 新局 ShowPanel 必然走完整路径触发 OnShow 重置。
        /// </summary>
        private void StartNewGame()
        {
            DestroyBattleController(); // 清理旧战斗会话（重开/结算重开路径）
            DisposeSessionFlow(); // 整局级"离开销毁"（2026-08-13：重开路径清理——旧局规则层实例注销监听）
            DestroySessionPanels(); // 局结束销毁会话面板（P4 断链补全——替代隐藏，防跨局残留）
            _gameState.ResetForNewRun(); // 基础牌组填手牌（协作者实现）；敌方由波次调度产出（数据集 floor1 回合 1/4/7）
            CreateSessionFlow(); // 整局级"进入创建"（2026-08-13：每局新建 EditorSession/EventNodeSystem/TowerFlow）
            EnterTower();
        }

        /// <summary>
        /// 销毁全部会话面板（编辑/构筑/事件/战斗）——局结束销毁（P4 断链补全，2026-08-13）：
        /// 替代隐藏——面板是局内对象，局的边界就是销毁边界（新实例天然干净，防跨局残留）；
        /// 引用置空 → 新局懒加载自动重建。⚠️ BattleResultPanel 是常驻 overlay，不在此范围。
        /// </summary>
        private void DestroySessionPanels()
        {
            if (_uiManager == null) return; // 防御：编译重载中间态
            _uiManager.HidePanel("PieceEdit");
            _uiManager.HidePanel("EditCandidatePanel");
            _uiManager.HidePanel("DeckBuild");
            _uiManager.HidePanel("EventPanel");
            _uiManager.HidePanel("Battle");
            if (_pieceEditPanel != null) { DestroyImmediate(_pieceEditPanel.gameObject); _pieceEditPanel = null; }
            if (_editCandidatePanel != null)
            {
                _editCandidatePanel.OnCandidateConfirmed -= OnEditCandidateConfirmed;
                DestroyImmediate(_editCandidatePanel.gameObject);
                _editCandidatePanel = null;
            }
            if (_deckBuildPanel != null) { DestroyImmediate(_deckBuildPanel.gameObject); _deckBuildPanel = null; }
            if (_eventPanel != null) { DestroyImmediate(_eventPanel.gameObject); _eventPanel = null; }
            if (_battlePanel != null) { DestroyImmediate(_battlePanel.gameObject); _battlePanel = null; }
        }

        /// <summary>进入爬塔：TowerFlow 节点序列驱动（事件关/编辑/战斗）。</summary>
        private void EnterTower()
        {
            var map = GetMapConfig();
            if (map == null || map.floors.Count == 0)
            {
                Debug.LogError("[Bootstrap] 无地图配置——无法进入爬塔");
                return;
            }
            _towerFlow.EnterFloor(0);
            Debug.Log($"[Bootstrap] 进入爬塔：层 {_gameState.CurrentFloor}，节点 {_gameState.CurrentNodeIndex}/{_gameState.NodeStates.Count}");
        }

        private void OpenEditCandidatePanel()
        {
            if (_editCandidatePanel == null)
            {
                StartCoroutine(LoadEditCandidatePanel());
            }
            else
            {
                _uiManager.ShowPanel("EditCandidatePanel");
            }
        }

        private System.Collections.IEnumerator LoadEditCandidatePanel()
        {
            yield return LoadPanelAsync<EditCandidatePanel>(panel =>
            {
                _editCandidatePanel = panel;
                _uiManager.RegisterPanel(panel);
                panel.Init(_gameState);
                panel.OnCandidateConfirmed += OnEditCandidateConfirmed;
                _uiManager.ShowPanel("EditCandidatePanel");
                Debug.Log("[Bootstrap] 编辑候选面板已显示");
            });
        }

        private void OnEditCandidateConfirmed(int defId)
        {
            if (_editorSession == null || !_editorSession.ConfirmEditPiece(defId))
            {
                Debug.LogWarning($"[Bootstrap] 编辑候选确认失败：defId={defId}");
                _editCandidatePanel?.Show();
                return;
            }
            _uiManager.HidePanel("EditCandidatePanel");
            OpenPieceEditor(defId);
        }

        /// <summary>打开棋子编辑面板（事件关模式——候选卡点击确认后进入）。</summary>
        private void OpenPieceEditor(int editableDefId = -1)
        {
            if (_pieceEditPanel == null)
            {
                StartCoroutine(LoadPieceEditPanel(editableDefId));
            }
            else
            {
                _pieceEditPanel.SetEditableDefId(editableDefId);
                _uiManager.ShowPanel("PieceEdit");
            }
        }

        private System.Collections.IEnumerator LoadPieceEditPanel(int editableDefId)
        {
            yield return LoadPanelAsync<PieceEditPanel>(panel =>
            {
                _pieceEditPanel = panel;
                _uiManager.RegisterPanel(panel);
                panel.Init(_editorSession, _gameState);
                panel.SetEditableDefId(editableDefId);
                _uiManager.ShowPanel("PieceEdit");
                Debug.Log("[Bootstrap] 棋子编辑界面已显示（事件模式）");
            });
        }

        /// <summary>打开牌组构筑面板（事件关模式——StateChanged("deck") 驱动；Btn_Next 经 Resolver.BuildDeck 落账后发 EventCompleted 推进）。</summary>
        private void OpenDeckBuild()
        {
            if (_deckBuildPanel == null)
            {
                StartCoroutine(LoadDeckBuildPanel());
            }
            else
            {
                _uiManager.ShowPanel("DeckBuild");
            }
        }

        private System.Collections.IEnumerator LoadDeckBuildPanel()
        {
            yield return LoadPanelAsync<DeckBuildPanel>(panel =>
            {
                _deckBuildPanel = panel;
                _uiManager.RegisterPanel(panel);
                panel.Init(_resolver, _gameState);
                _uiManager.ShowPanel("DeckBuild");
                Debug.Log("[Bootstrap] 牌组构筑界面已显示（事件模式）");
            });
        }

        private BattlePanel _battlePanel; // 局内缓存战斗面板（UI 架构重构 §五——一局一栋房，每场换管家）

        private void CreateBattleController()
        {
            var flow = CurrentBattleFlow;
            if (flow == null)
            {
                // 防御（2026-08-13 战斗级改造后）：战斗实例应已由 TowerFlow 创建——null 说明时序异常，跳过创建防 NRE
                Debug.LogWarning("[Bootstrap] PhaseChanged(Placement) 但无当前战斗实例——跳过控制器创建");
                return;
            }
            if (_battlePanel == null)
            {
                StartCoroutine(LoadBattlePanelAndController(flow)); // 局内首次：先创建面板（Addressables）再建控制器
            }
            else
            {
                CreateBattleControllerWith(flow, _battlePanel); // 面板复用——直接绑定
            }
        }

        /// <summary>局内首次：创建战斗面板 → 创建战斗控制器绑定（面板生命周期归 Bootstrap——UI 架构重构 §五）。</summary>
        private System.Collections.IEnumerator LoadBattlePanelAndController(BattleFlow flow)
        {
            yield return LoadPanelAsync<BattlePanel>(panel =>
            {
                _battlePanel = panel;
                _uiManager.RegisterPanel(panel);
                panel.OnSettingsClicked += () => _uiManager.PushOverlay("Settings"); // 战斗内设置入口
                CreateBattleControllerWith(flow, panel);
            });
        }

        private void CreateBattleControllerWith(BattleFlow flow, BattlePanel panel)
        {
            var battleGo = new GameObject("BattleController");
            var controller = battleGo.AddComponent<BattleController>();
            controller.Init(flow, _gameState, _uiManager, panel); // 绑定面板（不创建——面板局内复用）
            controller.OnExitRequested += BackToMainMenu; // 战斗面板退出按钮 → 回主菜单
        }

        /// <summary>获取物品弹窗常驻创建（RelicObtained → PushOverlay 提示；仅确认关闭）。</summary>
        private System.Collections.IEnumerator CreateItemGettingPanel()
        {
            yield return LoadPanelAsync<ItemGettingPanel>(panel =>
            {
                _uiManager.RegisterPanel(panel);
                panel.Init(_uiManager);
                panel.gameObject.SetActive(false);
            });
        }

        /// <summary>设置面板常驻创建（overlay——主菜单入口；IsPausing 暂停型冻结后台）。</summary>
        private System.Collections.IEnumerator CreateSettingsPanel()
        {
            yield return LoadPanelAsync<SettingsPanel>(panel =>
            {
                _uiManager.RegisterPanel(panel);
                panel.Init(_uiManager);
                panel.gameObject.SetActive(false); // 常驻隐藏（主菜单按钮 PushOverlay 显示）
            });
        }

        /// <summary>确认面板常驻创建（通用确认 overlay——2026-08-13：编辑撤回全部等场景；IsPausing 暂停型）。</summary>
        private System.Collections.IEnumerator CreateConfirmPanel()
        {
            yield return LoadPanelAsync<ConfirmPanel>(panel =>
            {
                _uiManager.RegisterPanel(panel);
                panel.Init(_uiManager);
                panel.gameObject.SetActive(false); // 常驻隐藏（ShowConfirm 时 PushOverlay）
            });
        }

        /// <summary>结算面板常驻创建（战斗结束 overlay——自身监听 StateChanged + PushPanel/PopPanel；须在战斗前就绪）。</summary>
        private System.Collections.IEnumerator CreateBattleResultPanel()
        {
            yield return LoadPanelAsync<BattleResultPanel>(panel =>
            {
            _battleResultPanel = panel;
            _uiManager.RegisterPanel(panel);
            panel.Init(_gameState, _uiManager);
            // 结算确认 → 若收尾挂起（失败/通关）则执行：销毁战斗 → Reset → 主界面（确认前保持战斗场景）
            panel.OnConfirmed += () =>
            {
                if (_pendingFinalize)
                {
                    _pendingFinalize = false;
                    BackToMainMenu();
                }
            };
            panel.gameObject.SetActive(false); // prefab 根 active——必须显式隐藏（常驻但不可见；首次 StateChanged 才 PushOverlay）
            Debug.Log("[Bootstrap] 结算面板已就绪（常驻）");
            });
        }

        private System.Collections.IEnumerator LoadMainMenu()
        {
            yield return Addressables.InitializeAsync();
            yield return LoadPanelAsync<MainMenuPanel>(panel =>
            {
            _uiManager.RegisterPanel(panel);
            // 按钮事件接线（面板只转发输入，流程响应在此）
            panel.OnNewGameClicked += () => { _uiManager.HidePanel("MainMenu"); StartNewGame(); };
            panel.OnContinueClicked += () => Debug.Log("[Bootstrap] 继续游戏（存档读取未接线 TODO）");
            panel.OnSettingsClicked += () => { _uiManager.PushOverlay("Settings"); }; // 设置面板 overlay（主菜单之上，暂停型）
            panel.OnQuitClicked += Application.Quit;
            _uiManager.ShowPanel("MainMenu");
            Debug.Log("[Bootstrap] 主菜单已显示");
            });
        }

        // ========== 生命周期（存档时机）==========

        private void OnApplicationPause(bool pause)
        {
            if (pause)
            {
                SaveManager.Instance.SaveAll(); // 切后台立即存（防丢进度四层之一）
            }
        }

        private void OnApplicationQuit()
        {
            SaveManager.Instance.SaveAll();
        }
    }
}
