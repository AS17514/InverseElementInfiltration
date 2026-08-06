using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 启动装配器（入口，不算层）：创建管理器 + 注册快照 + 事件接线 + 进主菜单（TODO）。
    /// 唯一纪律：只管创建 + 接线，不暴露实例查询（非 ServiceLocator）。
    /// 代码放 UI 程序集（零新增依赖——UI 引用全部层）；挂在启动场景根节点。
    /// </summary>
    public class Bootstrap : MonoBehaviour
    {
        // 普通类实例（去单例化后由 Bootstrap 创建并持有；规则层行为类显式传递避免网状耦合）
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
        private EventNodeSystem _eventNodeSystem;
        private TowerFlow _towerFlow;

        private void Awake()
        {
            // ① 按依赖顺序创建常驻管理器
            CreateManagers();
            // ② 加载配置（TODO: Addressables）
            LoadConfigs();
            // ③ 创建规则层（依赖注入）
            CreateGameplay();
            // ④ 注册存档快照
            RegisterSnapshots();
            // ⑤ 事件接线（进层/开战存档、RunEnded）
            WireEvents();
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

        // ========== ② 加载配置 ==========

        private void LoadConfigs()
        {
            // TODO: Addressables 加载 SO 资产（PieceDef / FloorConfig / MapConfig / AIParams /
            //       EventPool / RelicDef / SpecialAbilityDef / PromotionConfig）并 ConfigTable.Register 注册
            // 配置齐全前，运行以默认值兜底（GameState.ResetForNewRun 可正常调用）
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
            return new AIParams(); // 无配置时默认值兜底
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
            // 整局结束：清档 → 回主菜单（TODO: UI 切面板）
            EventCenter.Instance.AddEventListener(GameEvent.RunEnded, OnRunEnded);
            // TODO: 进层/开战存档（SaveManager.SaveAll 触发时机——关键事件存档）
        }

        private void OnRunEnded(object data)
        {
            bool victory = data is bool b && b;
            Debug.Log($"[Bootstrap] 整局结束 victory={victory}");
            SaveManager.Instance.SaveAll();            // 失败/通关 → 清档
            _gameState.ResetForNewRun();               // 单局制：回塔底
            // TODO: UIManager 切主菜单 + 重建 TowerFlow（新局从 EnterFloor(0) 开始）
        }

        // ========== ⑥ 主菜单 ==========

        private void EnterMainMenu()
        {
            // TODO: UI 层——UIManager.ShowPanel("MainMenu")
            // 面板实现 IPanel 主动注册进 _uiManager
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
