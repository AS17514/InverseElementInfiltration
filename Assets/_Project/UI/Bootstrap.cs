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
        private BattleFlow _battleFlow;
        private EditorSession _editorSession;
        private PieceEditPanel _pieceEditPanel; // 棋子编辑面板（新局入口——编辑完成进战斗）
        private EventPanel _eventPanel;         // 事件关面板（EventOpened 显示）
        private DeckBuildPanel _deckBuildPanel; // 牌组构筑面板（StateChanged("deck") 显示）
        private EventNodeSystem _eventNodeSystem;
        private TowerFlow _towerFlow;

        private void Awake()
        {
            // DOTween 容量：默认 200/50 快速操作会扩容警告，起步调大
            DG.Tweening.DOTween.SetTweensCapacity(500, 125);
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
            // ②b 程序块描述表（数据驱动——UI 槽位描述）
            SlotDescTable.Load(_slotDescriptions);
            // ②c buff 描述表（数据驱动——UI buff 区显示名）
            BuffDescTable.Load(_buffDescriptions);
            // ③ 创建规则层（依赖注入）
            CreateGameplay();
            // ④ 注册存档快照
            RegisterSnapshots();
            // ⑤ 事件接线（进层/开战存档、RunEnded）
            WireEvents();
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
            _battleFlow = new BattleFlow(_gameState, _boardRules, _intentResolver, _resolver, _enemyAI, _relicSystem);
            _editorSession = new EditorSession(_gameState, _resolver);
            _eventNodeSystem = new EventNodeSystem(_gameState, _resolver);
            _towerFlow = new TowerFlow(_gameState, _eventNodeSystem, _battleFlow, GetMapConfig());
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
            // 战斗开始：TowerFlow 开战（Phase→Placement）→ 创建战斗控制器
            EventCenter.Instance.AddEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
            // TODO: 进层/开战存档（SaveManager.SaveAll 触发时机——关键事件存档）
        }

        private void OnPhaseChanged(object data)
        {
            // 战斗开始（TowerFlow.StartBattle → Placement）→ 创建战斗控制器（幂等）
            if (_gameState.Phase == BattlePhase.Placement)
            {
                if (GameObject.Find("BattleController") == null)
                {
                    CreateBattleController();
                }
            }
        }

        private string _pendingEventId; // 缓存当前事件 id（懒加载完成后主动推给面板——防首次事件丢失）

        private void OnEventOpened(object data)
        {
            _pendingEventId = data as string;
            OpenEventPanel();
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
            bool done = false;
            EventPanel panel = null;
            PanelBase.CreateAsync<EventPanel>(p => { panel = p; done = true; });
            yield return new WaitUntil(() => done);
            _eventPanel = panel;
            _uiManager.RegisterPanel(panel);
            panel.Init(_eventNodeSystem);
            panel.ShowEvent(_pendingEventId); // 主动推首次事件数据（面板注册晚于事件广播——否则显示预制文本/选项无响应）
            _uiManager.ShowPanel("EventPanel");
            Debug.Log($"[Bootstrap] 事件面板已显示（event={_pendingEventId}）");
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
            // 战斗结算：GameOver 携带胜方 → 快照结算数据 + TowerFlow 收尾（胜利推进/失败结束——RunEnded 驱动回主菜单）
            if (_gameState.Phase != BattlePhase.GameOver) return;
            if (!(data is Side winner)) return;
            Debug.Log($"[Bootstrap] 战斗结束（{(winner == Side.Player ? "胜利" : "失败")}）→ 结算 + TowerFlow 收尾");
            // 快照结算数据（同步——Reset 1 帧后清空 GameState，面板异步加载完成时再读会丢）
            _pendingResultVictory = winner == Side.Player;
            _pendingResultScore = _gameState.PlayerScore;
            _pendingResultTurn = _gameState.TurnCount;
            ShowBattleResult();
            _towerFlow.OnBattleEnded(winner);
        }

        // ====== 结算面板（overlay：前端展示，确认只关面板） ======
        private BattleResultPanel _battleResultPanel;
        private bool _pendingResultVictory;
        private int _pendingResultScore;
        private int _pendingResultTurn;

        private void ShowBattleResult()
        {
            if (_battleResultPanel == null)
            {
                StartCoroutine(LoadBattleResult());
            }
            else
            {
                _battleResultPanel.ShowResult(_pendingResultVictory, _pendingResultScore, _pendingResultTurn);
            }
        }

        private System.Collections.IEnumerator LoadBattleResult()
        {
            bool done = false;
            BattleResultPanel panel = null;
            PanelBase.CreateAsync<BattleResultPanel>(p => { panel = p; done = true; });
            yield return new WaitUntil(() => done);
            _battleResultPanel = panel;
            _uiManager.RegisterPanel(panel);
            panel.ShowResult(_pendingResultVictory, _pendingResultScore, _pendingResultTurn); // 用同步快照数据
            Debug.Log($"[Bootstrap] 结算面板已显示（{(_pendingResultVictory ? "胜利" : "失败")}）");
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
            _gameState.ResetForNewRun();
            SaveManager.Instance.SaveAll();
            _uiManager.ShowPanel("MainMenu");
            _finalizing = false; // 复位——下一局收尾可用
            Debug.Log("[Bootstrap] 返回主菜单（收尾链完成）");
        }

        /// <summary>销毁战斗会话（BattleController 连带销毁战斗面板——防多局累积实例）。</summary>
        private void DestroyBattleController()
        {
            var old = GameObject.Find("BattleController");
            if (old != null) UnityEngine.Object.Destroy(old);
        }

        private void OnRunEnded(object data)
        {
            bool victory = data is bool b && b;
            Debug.Log($"[Bootstrap] 整局结束 victory={victory}");
            // 失败/通关 → 清档 + 回主菜单（单局制）
            BackToMainMenu();
        }

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
                StartCoroutine(LoadMainMenu());
            }
        }

        /// <summary>测试模式：直进战斗（跳过主菜单——与正式新局共用 StartNewGame 流程）。</summary>
        private System.Collections.IEnumerator EnterBattleTest()
        {
            yield return Addressables.InitializeAsync();
            StartNewGame();
        }

        /// <summary>新局：重置状态（基础牌组填手牌）→ 直接进入爬塔节点序列（事件→编辑事件→构筑事件→战斗——固定链）。</summary>
        private void StartNewGame()
        {
            DestroyBattleController(); // 清理旧战斗会话（重开/结算重开路径）
            _gameState.ResetForNewRun(); // 基础牌组填手牌（协作者实现）；敌方由波次调度产出（数据集 floor1 回合 1/4/7）
            EnterTower();
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

        /// <summary>打开棋子编辑面板（事件关模式——StateChanged("edit") 驱动；Btn_Next 发 EventCompleted 推进）。</summary>
        private void OpenPieceEditor()
        {
            if (_pieceEditPanel == null)
            {
                StartCoroutine(LoadPieceEditPanel());
            }
            else
            {
                _uiManager.ShowPanel("PieceEdit");
            }
        }

        private System.Collections.IEnumerator LoadPieceEditPanel()
        {
            bool done = false;
            PieceEditPanel panel = null;
            PanelBase.CreateAsync<PieceEditPanel>(p => { panel = p; done = true; });
            yield return new WaitUntil(() => done);
            _pieceEditPanel = panel;
            _uiManager.RegisterPanel(panel);
            panel.Init(_editorSession, _gameState);
            _uiManager.ShowPanel("PieceEdit");
            Debug.Log("[Bootstrap] 棋子编辑界面已显示（事件模式）");
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
            bool done = false;
            DeckBuildPanel panel = null;
            PanelBase.CreateAsync<DeckBuildPanel>(p => { panel = p; done = true; });
            yield return new WaitUntil(() => done);
            _deckBuildPanel = panel;
            _uiManager.RegisterPanel(panel);
            panel.Init(_resolver, _gameState);
            _uiManager.ShowPanel("DeckBuild");
            Debug.Log("[Bootstrap] 牌组构筑界面已显示（事件模式）");
        }

        private void CreateBattleController()
        {
            var battleGo = new GameObject("BattleController");
            var controller = battleGo.AddComponent<BattleController>();
            controller.Init(_battleFlow, _gameState);
            controller.OnExitRequested += BackToMainMenu; // 战斗面板退出按钮 → 回主菜单
        }

        private System.Collections.IEnumerator LoadMainMenu()
        {
            yield return Addressables.InitializeAsync();
            bool done = false;
            MainMenuPanel panel = null;
            PanelBase.CreateAsync<MainMenuPanel>(p => { panel = p; done = true; });
            yield return new WaitUntil(() => done);
            _uiManager.RegisterPanel(panel);
            // 按钮事件接线（面板只转发输入，流程响应在此）
            panel.OnNewGameClicked += () => { _uiManager.HidePanel("MainMenu"); StartNewGame(); };
            panel.OnContinueClicked += () => Debug.Log("[Bootstrap] 继续游戏（存档读取未接线 TODO）");
            panel.OnSettingsClicked += () => Debug.Log("[Bootstrap] 设置（设置面板未接线 TODO）");
            panel.OnQuitClicked += Application.Quit;
            _uiManager.ShowPanel("MainMenu");
            Debug.Log("[Bootstrap] 主菜单已显示");
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
