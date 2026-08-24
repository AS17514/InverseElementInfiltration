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
        private ConfirmPanel _confirmPanel; // 通用确认弹窗（常驻——退出确认等场景复用）
        private StoryPanel _storyPanel; // 开场剧情面板（新游戏先播；播完销毁进新局）
        private bool _storyPlaying; // 剧情播放中（防重）

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
            // ③a 行动经济 buff 前端同步桥（订阅现有回合/表现事件补发 BuffsChanged——不新增后端接口）
            ActionEconomyBuffSync.EnsureSubscribed(_gameState);
            // ④ 注册存档快照
            RegisterSnapshots();
            // ⚠️ 2026-08-23 修复：移除启动自动 LoadAll——它会使"开始新游戏"继承旧档未复位字段（示例：AP 上限、随机种子）
            // 设置已独立加载（settings.json——SettingsSystem.LoadSettings，见 L123）；游戏状态仅在玩家点"继续"（ContinueGame）时 LoadAll
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
            SettingsSystem.Instance.LoadSettings(); // 独立设置加载（settings.json，不随存档）
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
            if (_eventPanel != null) _eventPanel.Init(_eventNodeSystem, _gameState, _resolver);
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
            // 能力事件（2026-08-23）：不发 EventOpened——Bootstrap 负责首发唤醒（面板懒加载后主动回填候选）
            EventCenter.Instance.AddEventListener(GameEvent.AbilityCandidatesDrawn, OnAbilityCandidatesDrawn);
            // 战斗开始：TowerFlow 开战（Phase→Placement）→ 创建战斗控制器
            EventCenter.Instance.AddEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
            // 存档对接后端（2026-08-23）：关键节点落档——事件打开/能力候选/开战检查点（退出与 Continue 依赖）
            EventCenter.Instance.AddEventListener(GameEvent.EventOpened, OnEventOpenedForSave);
            EventCenter.Instance.AddEventListener(GameEvent.AbilityCandidatesDrawn, OnAbilityCandidatesForSave);
            EventCenter.Instance.AddEventListener(GameEvent.PhaseChanged, OnPhaseChangedForSave);
        }

        void OnDestroy()
        {
            // 大审查 O1：订阅/退订对称（常驻对象仅退出时触发——防御性）
            if (EventCenter.Instance == null) return;
            EventCenter.Instance.RemoveEventListener(GameEvent.RunEnded, OnRunEnded);
            EventCenter.Instance.RemoveEventListener(GameEvent.StateChanged, OnStateChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.EventOpened, OnEventOpened);
            EventCenter.Instance.RemoveEventListener(GameEvent.EditCandidatesDrawn, OnEditCandidatesDrawn);
            EventCenter.Instance.RemoveEventListener(GameEvent.AbilityCandidatesDrawn, OnAbilityCandidatesDrawn);
            EventCenter.Instance.RemoveEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.EventOpened, OnEventOpenedForSave);
            EventCenter.Instance.RemoveEventListener(GameEvent.AbilityCandidatesDrawn, OnAbilityCandidatesForSave);
            EventCenter.Instance.RemoveEventListener(GameEvent.PhaseChanged, OnPhaseChangedForSave);
            ActionEconomyBuffSync.Shutdown(); // 行动经济 buff 同步桥退订（与 EnsureSubscribed 对称）
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

        // ========== 存档对接后端（2026-08-23：关键节点落档——Continue 依赖存档恢复）==========

        void OnEventOpenedForSave(object data)
        {
            SaveManager.Instance.SaveAll(); // 事件打开 = 已推进到新节点——落档（含能力候选/编辑候选快照）
        }

        void OnAbilityCandidatesForSave(object data)
        {
            SaveManager.Instance.SaveAll(); // 能力候选刷新/抽取后落档（读档可直接回填候选）
        }

        void OnPhaseChangedForSave(object data)
        {
            if (_gameState != null && _gameState.Phase == BattlePhase.Placement)
            {
                SaveManager.Instance.SaveAll(); // 开战检查点（战斗开始即落档）
            }
        }

        private string _pendingEventId; // 缓存当前事件 id（懒加载完成后主动推给面板——防首次事件丢失）

        private void OnEventOpened(object data)
        {
            _pendingEventId = data as string;
            AudioManager.Instance.PlayBGM(TheLaw.Core.AudioRefs.BgmEvent);
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
        /// 能力事件候选广播（2026-08-23）：能力事件不发 EventOpened——此处理首发唤醒：
        /// 面板未创建 → 懒加载（LoadEventPanel 完成后 ShowAbilityEventFromState 主动回填）；
        /// 面板已存在 → ShowPanel + 面板自身监听刷新并重建选项区。
        /// </summary>
        private void OnAbilityCandidatesDrawn(object data)
        {
            if (_gameState == null) return;
            if (_gameState.AbilityCandidates == null || _gameState.AbilityCandidates.Count == 0)
            {
                Debug.LogWarning("[Bootstrap] 收到 AbilityCandidatesDrawn 但候选为空");
                return;
            }
            var ev = string.IsNullOrEmpty(_gameState.CurrentEventId)
                ? null
                : ConfigTable.FindByName<EventDefinition>(_gameState.CurrentEventId);
            if (ev == null || !ev.isAbilityPick)
            {
                Debug.LogWarning("[Bootstrap] 收到 AbilityCandidatesDrawn 但当前事件不是能力事件——忽略");
                return;
            }
            OpenEventPanel();
        }

        /// <summary>
        /// 公共懒加载：CreateAsync → WaitUntil → onReady（大审查 R2：5 个面板加载协程合并——模式统一、防漂移）。
        /// ⚠️ 2026-08-12 in-flight 锁：加载期间字段仍为 null——第二次请求会再启动加载（双实例双监听）；
        /// 按类型记录"加载中"，重复请求直接忽略（5 个面板统一受益）。
        /// </summary>
        private readonly HashSet<string> _loadingPanels = new HashSet<string>(); // 加载中的面板（防重入）
        private int _sessionGeneration; // 局内异步创建代际：重开/收尾后旧回调不得接入新局

        private System.Collections.IEnumerator LoadPanelAsync<T>(System.Action<T> onReady, bool sessionBound = false) where T : PanelBase
        {
            int generation = _sessionGeneration;
            string key = sessionBound ? $"{typeof(T).Name}:{generation}" : typeof(T).Name;
            if (!_loadingPanels.Add(key))
            {
                yield break; // 同一代际同类面板已在加载中
            }
            bool done = false;
            T panel = null;
            PanelBase.CreateAsync<T>(p => { panel = p; done = true; });
            yield return new WaitUntil(() => done);
            _loadingPanels.Remove(key);

            // Addressables 完成时局可能已结束或重开。旧实例绝不能注册、订阅或显示到新局。
            if (sessionBound && generation != _sessionGeneration)
            {
                if (panel != null) DestroyImmediate(panel.gameObject);
                yield break;
            }
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
                panel.Init(_eventNodeSystem, _gameState, _resolver);
                panel.OnSettingsClicked += () => _uiManager.PushOverlay("Settings"); // 事件关设置入口
                panel.OnExitClicked += ConfirmExitToMenu; // 事件关退出 → 确认弹窗（保存返回主菜单）
                // 主动推首次事件数据（面板注册晚于事件广播——否则显示预制文本/选项无响应）
                // 能力事件优先：首发/读档时 AbilityCandidatesDrawn 已错过——从 GameState 回填（不能依赖历史广播）
                if (!panel.ShowAbilityEventFromState())
                {
                    panel.ShowEvent(_pendingEventId); // 普通事件路径
                }
                _uiManager.ShowPanel("EventPanel");
                Debug.Log($"[Bootstrap] 事件面板已显示（event={_pendingEventId ?? _gameState?.CurrentEventId}）");
            }, sessionBound: true);
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
            _sessionGeneration++; // 立即废弃局内异步面板回调
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
            AudioManager.Instance.PlayBGM(TheLaw.Core.AudioRefs.BgmMenu);
            _finalizing = false; // 复位——下一局收尾可用
            Debug.Log("[Bootstrap] 返回主菜单（收尾链完成）");
        }

        // ========== 退出确认（2026-08-23：保存进度返回主菜单，不清档——Continue 可继续）==========

        private const string ExitConfirmMessage = "确认退出？\n本局进度将会保存";

        /// <summary>退出确认：弹通用确认窗（PushOverlay）——确认=保存并回主菜单；取消=无改动关弹窗。</summary>
        private void ConfirmExitToMenu()
        {
            if (_confirmPanel == null)
            {
                ExitToMainMenuKeepSave(); // 防御：确认面板未就绪直接保存退出（不卡流程）
                return;
            }
            _confirmPanel.ShowConfirm(ExitConfirmMessage, ExitToMainMenuKeepSave, ReenableBattleExitButton);
        }

        /// <summary>取消退出：恢复战斗面板退出按钮（BattleController 点退出时置灰过——防 1 帧内重复触发）。</summary>
        private void ReenableBattleExitButton()
        {
            if (_battlePanel != null && _battlePanel.ExitButton != null)
            {
                _battlePanel.ExitButton.interactable = true;
            }
        }

        /// <summary>保存当前进度并返回主界面（2026-08-23：退出确认后走这里——保留存档供 Continue 恢复，不清档）。</summary>
        private void ExitToMainMenuKeepSave()
        {
            StartCoroutine(FinalizeRunKeepSave());
        }

        private System.Collections.IEnumerator FinalizeRunKeepSave()
        {
            if (_finalizing)
            {
                yield break; // 收尾/退出进行中——防重
            }
            _finalizing = true;
            _sessionGeneration++; // 立即废弃局内异步面板回调
            yield return null; // 等一帧：当前事件回调栈必然已退出
            DestroyBattleController();
            DisposeSessionFlow(); // 整局级"离开销毁"（注销监听 + 置空——含战斗实例销毁）
            SaveManager.Instance.SaveAll(); // 保存当前进度（不清档——保留给 Continue）
            DestroySessionPanels();
            _uiManager.ShowPanel("MainMenu");
            AudioManager.Instance.PlayBGM(TheLaw.Core.AudioRefs.BgmMenu);
            _finalizing = false;
            Debug.Log("[Bootstrap] 已保存进度并返回主界面（可继续游戏）");
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
            AudioManager.Instance.PlayBGM(TheLaw.Core.AudioRefs.BgmResult);
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
        /// <summary>
        /// 继续游戏（2026-08-23）：读档恢复整局状态 → 重建会话（EditorSession/EventNodeSystem/TowerFlow）→ 回到存档点。
        /// 支持：事件中断（普通/能力）、编辑中断（重开编辑面板）。战斗中断待后端 ResumeBattle API（见 docs/后端待办.md）。
        /// </summary>
        private void ContinueGame()
        {
            if (!SaveManager.Instance.HasSave)
            {
                Debug.LogWarning("[Bootstrap] 无存档——无法继续游戏");
                return;
            }
            _sessionGeneration++; // 新会话边界：旧局异步面板不得接入
            DestroyBattleController();
            DisposeSessionFlow();
            DestroySessionPanels(keepBattlePanel: true); // 2026-08-24：读档=同局续玩——保留战斗面板（销毁→异步重建→首波部署事件丢失→表现等待超时）
            SaveManager.Instance.LoadAll(); // 恢复 GameState/RandomManager/Tutorial/Progress
            CreateSessionFlow();            // 重建整局级会话（绑定已恢复的 GameState）
            if (TryResumeSavedState())
            {
                // 2026-08-23：恢复后预加载 BattlePanel——后续推进到战斗时控制器可同步创建
                // （否则 _battlePanel 已被 DestroySessionPanels 销毁 → 异步创建 → 首波部署事件丢失 → 表现回执超时）
                StartCoroutine(PreloadBattlePanelAfterContinue());
                return;
            }
            // 战斗/其他阶段：后端暂无续玩 API——留在主菜单，存档保留
            _uiManager.ShowPanel("MainMenu");
            Debug.LogWarning("[Bootstrap] 存档处于战斗等阶段——后端暂不支持该阶段续玩（待办：BattleFlow.ResumeBattle），已保留存档");
        }

        /// <summary>按存档状态回到可恢复的界面（事件/编辑）；返回 true=已恢复，false=无法恢复（战斗等阶段）。</summary>
        private bool TryResumeSavedState()
        {
            // 战斗阶段：棋盘有棋子（或 GameOver）→ 战斗中。⚠️ CurrentEventId 在战斗开始后仍残留上一事件 id——
            // 必须先判战斗再判事件，否则会误把战斗档恢复成事件界面。
            // ⚠️ 2026-08-24 战斗中续玩（临时方案——回战斗开始，SL 重打语义）：加载 SL 槽（状态+RNG 回开战前）→ StartBattle 重开；
            // 前端控制器由 PhaseChanged(Placement) 自动创建（OnPhaseChanged）；GameOver 档（防御）不可读——清档回主菜单
            if ((_gameState.PiecesById != null && _gameState.PiecesById.Count > 0)
                || _gameState.Phase == BattlePhase.GameOver)
            {
                if (_gameState.Phase == BattlePhase.GameOver)
                {
                    // 终局档（防御——存档恰落 GameOver→收尾窗口）：不可读档——清档回主菜单
                    _gameState.ResetForNewRun();
                    SaveManager.Instance.SaveAll();
                    _uiManager.ShowPanel("MainMenu");
                    return true;
                }
                SaveManager.Instance.LoadBattleStart(); // SL 槽缺失 → 保持主档（主档可能即开战检查点状态——仍可 StartBattle 重开）
                _towerFlow.StartBattleAtCurrentFloor(); // 创建 BattleFlow + StartBattle（内部 ResetForBattle + 重存 SL）
                _uiManager.HidePanel("MainMenu");
                return true;
            }
            // 编辑中断：EditingDefs 非空 → 直接重开编辑面板（EditorSession 重建；Undo 历史随实例丢失——可接受）
            if (_gameState.EditingDefs != null && _gameState.EditingDefs.Count > 0)
            {
                int defId = -1;
                foreach (var d in _gameState.EditingDefs)
                {
                    defId = d;
                    break;
                }
                _uiManager.HidePanel("MainMenu");
                OpenPieceEditor(defId);
                return true;
            }
            var evId = _gameState.CurrentEventId;
            if (string.IsNullOrEmpty(evId))
            {
                return false; // 无进行中事件（战斗中/未开始）
            }
            var ev = ConfigTable.FindByName<EventDefinition>(evId);
            if (ev != null && ev.isAbilityPick)
            {
                // 能力事件：候选已入档——懒加载完成后主动回填（不依赖历史广播）
                _pendingEventId = null;
                AudioManager.Instance.PlayBGM(TheLaw.Core.AudioRefs.BgmEvent);
                OpenEventPanel();
            }
            else
            {
                // 普通事件：复用 EventOpened 路径（Bootstrap 缓存 id + 懒加载面板主动推数据）
                EventCenter.Instance.EventTrigger(GameEvent.EventOpened, evId);
            }
            _uiManager.HidePanel("MainMenu");
            return true;
        }

        /// <summary>主菜单"新游戏"：先播开场剧情，播完（含长按跳过）回调继续原新局流程。</summary>
        private void StartNewGameWithOpeningStory()
        {
            if (_storyPlaying) return; // 防重（连点）
            _uiManager.HidePanel("MainMenu");
            StartCoroutine(PlayOpeningStoryThenStartNewGame());
        }

        /// <summary>
        /// 开场剧情流程：加载 StoryPanel（Addressables）→ PlayOpening（读 story_opening.json）→
        /// 剧情播完/跳过 → OnOpeningStoryFinished → StartNewGame（进入第一个事件）。
        /// 配置缺失：StoryPanel 内 LogWarning——直接跳过剧情进流程（不卡新局）。
        /// </summary>
        private System.Collections.IEnumerator PlayOpeningStoryThenStartNewGame()
        {
            yield return LoadPanelAsync<StoryPanel>(p =>
            {
                _storyPanel = p;
                _uiManager.RegisterPanel(p);
            }, sessionBound: false); // 不绑定局代际——剧情在新局边界之前
            if (_storyPanel == null || !_storyPanel.PlayOpening())
            {
                _storyPanel = null;
                StartNewGame(); // 无配置/面板创建失败：跳过剧情直接进流程
                yield break;
            }
            _storyPanel.Finished += OnOpeningStoryFinished;
            _storyPlaying = true;
            _uiManager.ShowPanel("StoryPanel");
            Debug.Log("[Bootstrap] 开场剧情播放开始");
        }

        /// <summary>剧情结束（播完/长按跳过）：隐藏并销毁面板 → 继续原 StartNewGame 流程。</summary>
        private void OnOpeningStoryFinished()
        {
            _storyPlaying = false;
            if (_storyPanel == null) return;
            _uiManager.HidePanel("StoryPanel");
            DestroyImmediate(_storyPanel.gameObject);
            _storyPanel = null;
            StartNewGame();
        }

        private void StartNewGame()
        {
            _sessionGeneration++; // 新局边界：旧局异步面板不得接线到当前会话
            DestroyBattleController(); // 清理旧战斗会话（重开/结算重开路径）
            DisposeSessionFlow(); // 整局级"离开销毁"（2026-08-13：重开路径清理——旧局规则层实例注销监听）
            DestroySessionPanels(); // 局结束销毁会话面板（P4 断链补全——替代隐藏，防跨局残留）
            _gameState.ResetForNewRun(); // 基础牌组填手牌（协作者实现）；敌方由波次调度产出（数据集 floor1 回合 1/4/7）
            CreateSessionFlow(); // 整局级"进入创建"（2026-08-13：每局新建 EditorSession/EventNodeSystem/TowerFlow）
            // 首波部署会在 StartBattle 的 Placement 事件后同步进入表现等待；先确保 BattlePanel 已加载，
            // 使 BattleController 能在同一调用栈内订阅部署事件，避免首组表现丢失后回执超时。
            StartCoroutine(PreloadBattlePanelThenEnterTower());
        }

        /// <summary>新局进入爬塔前预加载局内 BattlePanel；只缓存面板，不提前创建 BattleController 或显示面板。
        /// ⚠️ 2026-08-23：prefab 根 active=1——缓存后必须显式隐藏，否则会与主菜单/事件界面重叠（战斗开始 ShowPanel("Battle") 才显示）。</summary>
        private System.Collections.IEnumerator PreloadBattlePanelThenEnterTower()
        {
            int generation = _sessionGeneration;
            if (_battlePanel == null)
            {
                yield return LoadPanelAsync<BattlePanel>(panel =>
                {
                    _battlePanel = panel;
                    _uiManager.RegisterPanel(panel);
                    panel.OnSettingsClicked += () => _uiManager.PushOverlay("Settings");
                    panel.gameObject.SetActive(false); // 预加载隐藏（防与其他界面重叠）
                }, sessionBound: true);
            }
            // LoadPanelAsync 会在旧代际直接结束；陈旧协程不得继续推进当前新局。
            if (generation != _sessionGeneration) yield break;
            EnterTower();
        }

        /// <summary>Continue 恢复后预加载 BattlePanel（同新局预加载，防首波部署事件丢失导致回执超时）。
        /// 竞态兜底：预加载完成时战斗已开始且控制器未创建（PhaseChanged 的异步创建被本预加载去重跳过）→ 补创建控制器。</summary>
        private System.Collections.IEnumerator PreloadBattlePanelAfterContinue()
        {
            int generation = _sessionGeneration;
            if (_battlePanel == null)
            {
                yield return LoadPanelAsync<BattlePanel>(panel =>
                {
                    _battlePanel = panel;
                    _uiManager.RegisterPanel(panel);
                    panel.OnSettingsClicked += () => _uiManager.PushOverlay("Settings");
                    // 2026-08-23：prefab 根 active=1——预加载后先隐藏，防读档继续时战斗界面与事件/主菜单重叠；
                    // 若战斗已开始（竞态），EnsureBattleControllerIfNeeded → ShowPanel("Battle") 会重新显示
                    panel.gameObject.SetActive(false);
                    EnsureBattleControllerIfNeeded();
                }, sessionBound: true);
            }
            else
            {
                _battlePanel.gameObject.SetActive(false); // 已有缓存：同样先隐藏（战斗未开始则保持）
                EnsureBattleControllerIfNeeded();
            }
            if (generation != _sessionGeneration) yield break;
        }

        /// <summary>战斗已开始但控制器未创建（预加载/异步竞态）→ 补创建（防部署事件丢失）。</summary>
        private void EnsureBattleControllerIfNeeded()
        {
            if (_gameState != null && _gameState.Phase == BattlePhase.Placement
                && _towerFlow != null && _towerFlow.CurrentBattleFlow != null
                && GameObject.Find("BattleController") == null)
            {
                CreateBattleController();
            }
        }

        /// <summary>
        /// 销毁全部会话面板（编辑/构筑/事件/战斗）——局结束销毁（P4 断链补全，2026-08-13）：
        /// 替代隐藏——面板是局内对象，局的边界就是销毁边界（新实例天然干净，防跨局残留）；
        /// 引用置空 → 新局懒加载自动重建。⚠️ BattleResultPanel 是常驻 overlay，不在此范围。
        /// ⚠️ 2026-08-24：keepBattlePanel=true = 读档续玩路径（同局续玩——保留战斗面板复用；
        /// 否则面板销毁→异步重建→首波部署事件（开战瞬间同步发出）丢失→部署表现等待无回执→3s 超时降级）。
        /// </summary>
        private void DestroySessionPanels(bool keepBattlePanel = false)
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
            if (!keepBattlePanel && _battlePanel != null) { DestroyImmediate(_battlePanel.gameObject); _battlePanel = null; }
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
            }, sessionBound: true);
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
            }, sessionBound: true);
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
            }, sessionBound: true);
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
                // 同一局可连续多场战斗；首场面板慢加载完成时，flow 也可能已被后续战斗替换。
                if (flow == null || flow != CurrentBattleFlow)
                {
                    if (panel != null) DestroyImmediate(panel.gameObject);
                    return;
                }
                _battlePanel = panel;
                _uiManager.RegisterPanel(panel);
                panel.OnSettingsClicked += () => _uiManager.PushOverlay("Settings"); // 战斗内设置入口
                CreateBattleControllerWith(flow, panel);
            }, sessionBound: true);
        }

        private void CreateBattleControllerWith(BattleFlow flow, BattlePanel panel)
        {
            var battleGo = new GameObject("BattleController");
            var controller = battleGo.AddComponent<BattleController>();
            controller.Init(flow, _gameState, _uiManager, panel); // 绑定面板（不创建——面板局内复用）
            controller.OnExitRequested += ConfirmExitToMenu; // 战斗面板退出按钮 → 确认弹窗（保存返回主菜单）
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
                _confirmPanel = panel;
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
            panel.OnNewGameClicked += StartNewGameWithOpeningStory; // 2026-08-24：新游戏先播开场剧情，播完回调继续 StartNewGame 原流程
            panel.OnContinueClicked += ContinueGame; // 2026-08-23：对接存档继续（读档恢复会话/事件流程）
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
