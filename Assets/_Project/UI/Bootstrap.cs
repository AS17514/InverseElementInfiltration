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
        [SerializeField] private List<RelicDef> _relicConfigs = new List<RelicDef>();
        [SerializeField] private List<TemplateDef> _templateConfigs = new List<TemplateDef>(); // 程序块模板库（编辑界面候选池）

        [Header("测试开关")]
        [Tooltip("true=启动直进战斗（跳过主菜单），false=正常主菜单流程")]
        [SerializeField] private bool _directToBattle = false; // 默认主菜单（对接期测试直进可临时开）

        [Header("程序块描述表（结构特征码→描述；未命中回退代码生成）")]
        [SerializeField] private TextAsset _slotDescriptions;

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
            RegisterAll(_relicConfigs);
            // 程序块模板库（独立注册表——按"种类+编号"查询，编辑界面候选池）
            foreach (var def in _templateConfigs)
            {
                if (def != null)
                {
                    TemplateLibrary.Register(def);
                }
            }
            Debug.Log($"[Bootstrap] 配置注册完成：棋子 {_pieceConfigs.Count} / 能力 {_abilityConfigs.Count} / 层 {_floorConfigs.Count} / 地图 {_mapConfigs.Count} / AI {_aiParamConfigs.Count} / 事件池 {_eventPoolConfigs.Count} / 遗物 {_relicConfigs.Count} / 模板 {_templateConfigs.Count}");
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
            // TODO: 进层/开战存档（SaveManager.SaveAll 触发时机——关键事件存档）
        }

        private void OnEventOpened(object data)
        {
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
            _uiManager.ShowPanel("EventPanel");
            Debug.Log("[Bootstrap] 事件面板已显示");
        }

        private void OnStateChanged(object data)
        {
            if (_gameState.Phase != BattlePhase.GameOver) return; // 其他 StateChanged（落账等）不处理
            if (!(data is Side winner)) return;
            // 战斗结束直接回主菜单（结算面板后置安排——胜利/失败即回主界面）
            Debug.Log($"[Bootstrap] 战斗结束（{(winner == Side.Player ? "胜利" : "失败")}）→ 返回主菜单");
            BackToMainMenu();
        }

        /// <summary>回主菜单：重置状态 + 清档 + 显示主菜单（面板实例常驻，HidePanel 过直接恢复）。</summary>
        private void BackToMainMenu()
        {
            DestroyBattleController();
            _gameState.ResetForNewRun();
            SaveManager.Instance.SaveAll();
            _uiManager.ShowPanel("MainMenu");
            Debug.Log("[Bootstrap] 返回主菜单");
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
            SaveManager.Instance.SaveAll();            // 失败/通关 → 清档
            _gameState.ResetForNewRun();               // 单局制：回塔底
            // TODO: UIManager 切主菜单 + 重建 TowerFlow（新局从 EnterFloor(0) 开始）
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

        /// <summary>新局：重置状态（基础牌组填手牌）→ 进入棋子编辑界面（编辑完成 → 直进第 1 层战斗）。
        /// 事件/地图 UI 后补——届时 StartBattleAfterEdit 换 TowerFlow.EnterFloor(0) 走完整节点序列。</summary>
        private void StartNewGame()
        {
            DestroyBattleController(); // 清理旧战斗会话（重开/结算重开路径）
            _gameState.ResetForNewRun(); // 基础牌组填手牌（协作者实现）；敌方由波次调度产出（数据集 floor1 回合 1/4/7）
            OpenPieceEditor();
        }

        /// <summary>打开棋子编辑面板（测试前后端对接：程序编排 → 战斗）。</summary>
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
            panel.OnNextClicked += StartBattleAfterEdit; // 编辑完成 → 进战斗
            _uiManager.ShowPanel("PieceEdit");
            Debug.Log("[Bootstrap] 棋子编辑界面已显示");
        }

        /// <summary>编辑完成：直进第 1 层战斗（数据集的 Floor_floor1）。</summary>
        private void StartBattleAfterEdit()
        {
            _uiManager.HidePanel("PieceEdit");
            var map = GetMapConfig();
            if (map == null || map.floors.Count == 0)
            {
                Debug.LogError("[Bootstrap] 无地图配置——无法开始战斗");
                return;
            }
            _battleFlow.StartBattle(map.floors[0], GetDefaultAIParams());
            CreateBattleController();
            Debug.Log($"[Bootstrap] 编辑完成进战斗：层 {_gameState.CurrentFloor}，阶段={_gameState.Phase}，手牌 {_gameState.Hand.Count}");
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
