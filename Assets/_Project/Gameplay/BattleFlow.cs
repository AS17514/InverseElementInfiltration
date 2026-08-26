using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;
using UnityEngine;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 战斗流程：回合/阶段状态机 + 统一执行管线（请求→守卫→翻译→落账→扣AP→检查胜负）。
    /// 逐槽可续执行（翻译-暂停-落账-续译）；表现等待（等"表现完成"事件——无限等+日志）；
    /// 波次调度 + 敌方升变预告；胜负非对称（玩家判负 + 关卡 victoryRule）。
    /// </summary>
    public class BattleFlow
    {
        private readonly GameState _state;
        private readonly BoardRules _boardRules;
        private readonly IntentResolver _intentResolver;
        private readonly Resolver _resolver;
        private readonly EnemyAI _enemyAI;
        private readonly RelicSystem _relicSystem;
        private readonly ReentrantGuard _guard = new ReentrantGuard();

        private FloorConfig _floor;
        private FloorRules _floorRules;
        private AIParams _aiParams;
        private int _deployedWaveIndex;
        private bool _waveEnded; // 末波已部署

        // 逐槽执行上下文（执行开始即程序定稿）
        private class ExecContext
        {
            public int pieceId;
            public Side side; // 发起方（扣费判定依据——不能用当前阶段，表现完成时阶段可能已切换）
            public List<Template> program;
            public int slotIndex;
            public bool free;
        }
        private ExecContext _ctx;
        private bool _waitingCellSelect;     // 等玩家选格（落点/目标）
        private bool _waitingPresentation;   // 表现等待（等 UI 播完发"表现完成"）
        // ⚠️ 2026-08-21 表现回执 token + 超时降级（D/C 方案）：
        private static int _nextSessionId = 1;      // 跨实例递增（每场战斗一个 session）
        private int _battleSessionId;                // 本场战斗会话 id（StartBattle 分配）
        private int _actionCounter;                  // 本场战斗等待动作计数（递增）
        private int _waitingActionId;                // 当前等待的动作 token（-1=未等待）
        private bool _timeoutFallbackActive;         // 当前等待已超时降级（防重复降级）
        private float _timeoutWaitStart;             // 当前等待开始时间（Time.time——超时计）
        /// <summary>表现等待超时阈值（秒）——0/负 = 完全禁用超时降级（回到无限等）；超时后降级放行 + LogError 预警（诊断入档）。</summary>
        public static float PresentationTimeoutSeconds = 3f;
        // _battleEnded 已删除（2026-08-11 收尾链）：Reset 移出事件回调栈后
        // Phase==GameOver 防御不会被破坏——补丁退休
        private bool _enemyTurnEndPending;   // 敌方回合结束待定——本阶段表现全部播完才切回玩家回合（动画优先）
        private bool _hadEnemyPresentation;  // 本轮敌方回合是否有表现（有→表现完即切；无→等阶段展示信号）
        private bool _deployedThisRound;     // 本轮波次是否部署（部署动画挂起点）
        // 旧 promotions 延迟挂载批次（2026-08-24 时序修复：预告周期锚定下一波部署回合 s——s-2 挂载、s-1 升变、s 部署）。
        // ⚠️ 瞬态（不入档）：当前无关卡使用旧机制；若在「部署后、预告前」存档窗口读档会丢失本批次——启用旧机制时需补存档字段。
        private class PendingPromoBatch
        {
            public int announceTurn; // 预告挂载回合（TurnCount）
            public List<PieceInstance> pieces = new List<PieceInstance>(); // 已部署棋子（与 promotions 下标对应）
            public List<WavePromotion> promos = new List<WavePromotion>();
        }
        private readonly List<PendingPromoBatch> _pendingPromoBatches = new List<PendingPromoBatch>();
        private int _enemyBudget; // 敌方回合行动次数预算（逐步决策——每步一个行动；2026-08-13 替代请求队列）
        private readonly HashSet<int> _actedEnemyPieces = new HashSet<int>(); // 本回合已行动的敌方棋子（① 排除——防 requests[0] 固定重复执行）

        private readonly System.Collections.Generic.Queue<int> _pendingImmediateExecutes = new System.Collections.Generic.Queue<int>(); // 2026-08-22 插入执行队列（免费行动"立即执行"——触发入队，空闲时强制该棋执行）

        public BattleFlow(GameState state, BoardRules boardRules, IntentResolver intentResolver,
            Resolver resolver, EnemyAI enemyAI, RelicSystem relicSystem)
        {
            _state = state;
            _boardRules = boardRules;
            _intentResolver = intentResolver;
            _resolver = resolver;
            _enemyAI = enemyAI;
            _relicSystem = relicSystem;

            EventCenter.Instance.AddEventListener(GameEvent.PresentationFinished, OnPresentationFinished);
            EventCenter.Instance.AddEventListener(GameEvent.PhaseDisplayed, OnPhaseDisplayed);
            EventCenter.Instance.AddEventListener(GameEvent.PlacementFinished, OnPlacementFinished);
            EventCenter.Instance.AddEventListener(GameEvent.ExtraActionGranted, OnExtraActionGranted); // 2026-08-22 插入执行（免费行动"获得即立即执行"）
        }

        /// <summary>
        /// 销毁钩子（2026-08-13 战斗级"进入创建、离开销毁"改造）：注销全部事件监听。
        /// ⚠️ 必须对称于构造注册——漏注销 = 旧实例幽灵回调 + 新实例双处理（表现完成双推进）。
        /// 销毁路径全枚举：胜利/失败（TowerFlow.OnBattleEnded）、战斗中退出/新游戏（Bootstrap 经 TowerFlow.DisposeCurrentBattle）。
        /// </summary>
        public void Dispose()
        {
            EventCenter.Instance.RemoveEventListener(GameEvent.PresentationFinished, OnPresentationFinished);
            EventCenter.Instance.RemoveEventListener(GameEvent.PhaseDisplayed, OnPhaseDisplayed);
            EventCenter.Instance.RemoveEventListener(GameEvent.PlacementFinished, OnPlacementFinished);
            EventCenter.Instance.RemoveEventListener(GameEvent.ExtraActionGranted, OnExtraActionGranted); // 2026-08-22 对称清理
            _waitingPresentation = false; // 2026-08-21：停止超时守望协程（防实例引用滞留——协程循环退出）
        }

        // ========== 开战 / 阶段 ==========

        public void StartBattle(FloorConfig floor, AIParams aiParams)
        {
            _state.ResetForBattle(); // 战斗态重置（2026-08-13：跨战斗残留——TurnCount/棋盘/波次分每场战斗重来）
            ResetState(); // 新局统一清瞬态执行状态（防跨局残留——后端待办 #5：多次重开卡死根因）
            _battleSessionId = _nextSessionId++; // 2026-08-21：本场战斗会话 id（表现回执 token——跨战斗隔离）
            _actionCounter = 0;
            _waitingActionId = -1;
            _floor = floor;
            _aiParams = aiParams;
            _floorRules = FloorRulesFactory.Create(floor.Id);
            _deployedWaveIndex = 0;
            _waveEnded = false;
            _state.EnemyAPMax = floor.enemyMaxAP;
            _state.WaveEndCountdown = -1;
            _floorRules.OnBattleStart(_state, _resolver);
            // 先分离非初始牌，再创建 Placement UI；避免构筑结果在准备阶段短暂显示为满手牌。
            // StartPlayerTurn 仍保留幂等防御调用，首回合只负责自动抽 4 张。
            _resolver.SetupDrawPile();
            _resolver.SpawnShockWalls(); // 2026-08-24 能力「震击」：游戏开始时非部署区随机生成 2 个不可破坏墙（持有能力时；内部判——摆位阶段可见）
            // ⚠️ 2026-08-24 战斗中续玩（临时方案）：开战快照 SL 槽——必须在**波次随机（HandleWaveAndPromotions）之前**保存
            // （GameState + RNG 独立槽位；Continue 战斗档加载后 StartBattle 重开 → 与首次完全一致[含首波随机阵容]）
            SaveManager.Instance.SaveBattleStart();
            ChangePhase(BattlePhase.Placement, force: true); // 强制：塔流程 Phase 可能已停在 Placement——必须发事件让 UI 创建战斗控制器
            // 开局部署首波（startTurn=1 的波——玩家摆位需要看到敌方位置参照）
            HandleWaveAndPromotions();
            // Placement：玩家布置 Hand 中 Initial 棋子（起始标记自由摆）→ UI 摆完发 PlacementFinished
        }

        /// <summary>
        /// 重置瞬态执行状态（新局必清——重置清单集中一处，防再漏）。
        /// 泄漏实例（后端待办 #5）：波次部署动画 WaitPresentation 在 _ctx==null 时置 _waitingPresentation=true，
        /// ChangePhase(GameOver) 清理分支要求 _ctx!=null 才清 → 漏清进新局 → 敌方回合 TryEndEnemyTurn 永远等表现卡死。
        /// </summary>
        private void ResetState()
        {
            _ctx = null;
            _waitingCellSelect = false;
            _waitingPresentation = false;
            _enemyTurnEndPending = false;
            _hadEnemyPresentation = false;
            _deployedThisRound = false;
            _pendingPromoBatches.Clear(); // 延迟挂载批次（新字段必须进重置清单——防跨局残留）
            _enemyBudget = 0; // 敌方行动预算（新字段必须进重置清单——防跨局残留）
            _actedEnemyPieces.Clear(); // 已行动棋子集合（新字段必须进重置清单——防跨局残留）
            _diceRigPending = false; // 2026-08-24 能力「出千」自选瞬态（新字段必须进重置清单）
            _pendingImmediateExecutes.Clear(); // AA4-07：插入执行队列（新字段必须进重置清单——防跨局残留）
            _pendingEnemyImmediateExecutes.Clear(); // AA4-07：敌方立即额外行动队列（新字段必须进重置清单——防跨局残留）
            _waitingDiceMovePieceId = -1; // AA4-07：骰子选方向等待（新字段必须进重置清单——防跨局残留）
            _waitingDiceMoveSide = default; // AA4-07：骰子选方向等待阵营（新字段必须进重置清单——防跨局残留）
        }

        private void OnPlacementFinished(object data)
        {
            if (_state.Phase != BattlePhase.Placement)
            {
                return;
            }
            // 前置条件：手牌中不得还有初始棋子——必须摆完全部起始棋子才能结束摆放（防"跳过摆放"）
            // ⚠️ 2026-08-15：类型 = 价值档位推导（初始 = 0-3 档；编辑跨档后种类随价值变化）
            // ⚠️ 2026-08-20 牌结构：仅棋子牌参与（麻将牌非棋子跳过）
            foreach (var card in _state.Hand)
            {
                if (card.IsPiece && _state.GetEffectiveType(card.defId) == PieceType.Initial)
                {
                    EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "placement-incomplete"); // 通知 UI 继续摆放
                    return;
                }
            }
            StartPlayerTurn();
        }

        public void StartPlayerTurn()
        {
            // ⚠️ 2026-08-19 抽牌堆机制（策划确认）：第一回合（TurnCount==0）开始——
            // ① 手牌中【部署/升变】种类转入抽牌堆（初始种类已全摆完——Placement 校验兜底）
            // ② 自动抽 4 张（抽牌堆 → 手牌）；此后靠"1 AP 抽 1"行动补充
            if (_state.TurnCount == 0)
            {
                _resolver.SetupDrawPile();
                for (int n = 0; n < 4 && _state.DrawPile.Count > 0; n++)
                {
                    _resolver.DrawCard();
                }
            }
            _state.PlayerAP = _state.PlayerAPMax;
            _state.ActionEconomyActed.Clear(); // 2026-08-22 行动经济：新回合重置已行动集（buff 回态 A）
            _state.GoDeployCount = 0;          // 2026-08-24 围棋：每回合限部署 1 次（回合开始重置）
            _state.GoExtraDeploys = 0;     // 2026-08-24 能力「买子」：购买次数当回合有效——回合开始清（作废未用次数）
            _state.DiceMovePending = false;    // 2026-08-24 骰子：点数移动 buff **不跨回合**（新回合清）
            _state.DiceMoveSteps = 0;
            _diceRigPending = false;           // 2026-08-24 能力「出千」：自选瞬态不跨回合（未选则作废）
            if (_state.IsStyleActive(StyleRegistry.Token))
            {
                _state.TokenCount += 1;        // 2026-08-24 代币：每回合开始 +1（初始 0；不跨战斗）
                EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "token");
            }
            _resolver.RefineHandElements();    // 2026-08-24 能力「提纯」：手牌属性回合开始变相生（激活时内部判）
            ChangePhase(BattlePhase.PlayerTurn);
            _floorRules.OnTurnStart(_state, _resolver);
            _relicSystem.OnTurnStart();
        }

        public void OnPlayerEndTurn()
        {
            if (_state.Phase != BattlePhase.PlayerTurn || _guard.IsLocked)
            {
                return;
            }
            // AA4-01：等待/执行中禁止结束回合（防执行免扣 AP + 半程序丢弃）
            if (_ctx != null || _waitingCellSelect || _waitingPresentation || _waitingDiceMovePieceId >= 0)
            {
                Debug.LogWarning("[BattleFlow] 选格/执行/表现/骰子选方向等待中禁止结束回合");
                return;
            }
            _state.PlayerAP = 0; // 回合末清零
            ClearEditedCardQualify(); // 2026-08-23 E5：资格不跨回合——结束回合即取消（高亮消失）
            _floorRules.OnTurnEnd(_state, _resolver);
            StartEnemyTurn();
        }

        private void StartEnemyTurn()
        {
            _state.EnemyAP = _state.EnemyAPMax;
            ChangePhase(BattlePhase.EnemyTurn);
            HandleWaveAndPromotions(); // 波次调度 + 升变预告（2026-08-24：预告周期锚定下一波部署回合 s——预告 s-2 / 升变 s-1 / 部署 s）
            ResolveEnemyTurn();
        }

        /// <summary>
        /// 敌方回合：逐步决策-执行循环（2026-08-13 改造——替代"批量入队"）。
        /// 每步基于【最新实时状态】决策一个行动 → 执行完（FinishExecute）→ 再基于最新状态决策下一步——
        /// 决策状态 = 执行状态（零间隔）→ 决策-执行漂移从结构上消除（击杀漂移/打己方/空挥不再可能）。
        /// 收尾条件：预算用完 / 无可行动棋子 / 战斗结束。
        /// </summary>
        private void ResolveEnemyTurn()
        {
            // ⚠️ 2026-08-13 回合级字段统一复位（此前 _actedEnemyPieces 只在战斗级 ResetState 清——
            // 跨回合残留导致敌方棋子只行动一次后永久站着；_hadEnemyPresentation 同样跨回合残留致无动画回合闪切）
            _enemyBudget = _state.EnemyAPMax; // 行动次数预算（逐步决策——每步一个行动）
            _actedEnemyPieces.Clear();        // 已行动棋子——每回合重置（本回合行动过的不带入下回合）
            _hadEnemyPresentation = false;    // 本回合是否有表现——每回合复位（表现发生时再锁存）
            TryNextEnemyDecision();
        }

        /// <summary>
        /// 逐步决策核心（FinishExecute 后调用——side==Enemy 时）：基于最新状态决策一个行动。
        /// 三出口：战斗结束→停；预算 0 / 无可行动棋子→敌方回合收尾；否则执行第一个请求。
        /// ⚠️ 2026-08-13 ①：决策排除本回合已行动的棋子（防同一棋子反复被选、其他棋子饿死）。
        /// </summary>
        private void TryNextEnemyDecision()
        {
            if (_ctx != null || _waitingPresentation)
            {
                return; // AA4-06：当前执行/表现未收尾——等 FinishExecute 链再触发（防额外行动被覆盖截断）
            }
            if (_state.Phase == BattlePhase.GameOver)
            {
                return; // 战斗已结束——不再决策（防御：防失败后误判胜利）
            }
            if (_enemyBudget <= 0)
            {
                EndEnemyTurn();
                return;
            }
            var requests = _enemyAI.DecideTurn(_state, _actedEnemyPieces); // 基于最新实时状态决策（每步重算；排除已行动）
            if (requests.Count == 0)
            {
                EndEnemyTurn();
                return;
            }
            _enemyBudget--;
            if (requests[0] is ExecuteRequest exec)
            {
                _actedEnemyPieces.Add(exec.pieceId); // ① 记录已行动棋子——后续步骤不再选中
            }
            ProcessRequest(requests[0], Side.Enemy); // 每步只执行第一个行动（决策时状态=执行时状态——无漂移）
        }

        /// <summary>敌方回合收尾（全部请求处理完后）：AP 清零/回合计数/胜负判定/回合切换挂起（动画优先）。</summary>
        private void EndEnemyTurn()
        {
            _state.EnemyAP = 0;
            // ⚠️ 计分：敌方回合收尾统一结算（2026-08-20——策划"每个回合结束时（对手的该回合结束）"；
            // 第 1 关（WipeOut）不结算；由 SettleTurnScore 统一处理，避免重复结算。
            _state.TurnCount++;
            SettleTurnScore(); // 策划契约：敌方回合结束统一结算本回合基础分
            CheckVictory(false);
            if (_state.Phase != BattlePhase.GameOver)
            {
                // 动画优先：敌方回合展示到本阶段表现全部播完（含波次部署/AI 行动动画）再切回玩家回合
                _enemyTurnEndPending = true;
                // ⚠️ 2026-08-12：_hadEnemyPresentation 不在此采样（串行化后采样时表现已完成、_waitingPresentation 已清，
                // 且 PhaseDisplayed 在回合开始 1 帧后发出早已被丢弃——无波次回合两条收尾路径均不满足软锁）
                // 改为 WaitPresentation 表现发生时锁存（有表现即标记，不依赖采样时机）
                TryEndEnemyTurn();
            }
        }

        /// <summary>
        /// 自动预告（2026-08-19 引入；2026-08-24 时序修复）：敌方场上离棋盘中心 (3.5,3.5) 最近的两个棋子获升变预告——
        /// countdown=1（下一敌方回合递减→0 升变）；预告在 s-2 回合挂载、s-1 回合升变（s = 本波 autoPromote 锚定的下一波部署回合，
        /// 由 HandleWaveAndPromotions 前瞻触发）。newDefId=0 表示升变时从升变类棋子随机（RandomManager）。
        /// </summary>
        private void AnnounceAutoPromotions()
        {
            var enemyPieces = new List<PieceInstance>();
            foreach (var piece in _state.Pieces.Values)
            {
                if (piece.side == Side.Enemy)
                {
                    enemyPieces.Add(piece);
                }
            }
            enemyPieces.Sort((a, b) => DistToCenter(a.position).CompareTo(DistToCenter(b.position)));
            int take = Mathf.Min(2, enemyPieces.Count);
            for (int i = 0; i < take; i++)
            {
                var piece = enemyPieces[i];
                var ann = new PromoteAnnouncement
                {
                    pieceId = piece.Id,
                    newDefId = 0,   // 升变时随机（RandomManager——种子相关可复现）
                    countdown = 1,  // 下一敌方回合（部署回合 s-1）升变
                };
                _state.PromoteAnnouncements.Add(ann);
                EventCenter.Instance.EventTrigger(GameEvent.PromoteAnnounced, ann);
                EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, piece.Id); // 升变预告挂载 → buff 变化（2026-08-23：预告走 buff 显示，玩家可在 buff 区查看）
            }
        }

        /// <summary>旧 promotions 挂载（2026-08-24）：对指定已部署棋子按 countdown 挂载升变预告并照发 PromoteAnnounced/BuffsChanged；目标已不在场（延迟窗口内阵亡/升变）则跳过。</summary>
        private void MountPromotions(List<PieceInstance> wavePieces, List<WavePromotion> promos, int countdown)
        {
            foreach (var promo in promos)
            {
                if (promo.pieceIndexInWave < 0 || promo.pieceIndexInWave >= wavePieces.Count)
                {
                    continue;
                }
                var target = wavePieces[promo.pieceIndexInWave];
                var alive = target != null ? _state.GetPiece(target.Id) : null;
                if (alive == null || alive.side != Side.Enemy)
                {
                    continue;
                }
                _state.PromoteAnnouncements.Add(new PromoteAnnouncement { pieceId = target.Id, newDefId = promo.toDefId, countdown = countdown });
                EventCenter.Instance.EventTrigger(GameEvent.PromoteAnnounced, new PromoteAnnouncement { pieceId = target.Id, newDefId = promo.toDefId, countdown = countdown });
                EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, target.Id); // 升变预告挂载 → buff 变化（2026-08-23 同口径）
            }
        }

        /// <summary>离棋盘中心 (3.5,3.5) 的曼哈顿距离。</summary>
        private static float DistToCenter(Vector2Int cell)
        {
            return Mathf.Abs(cell.x - 3.5f) + Mathf.Abs(cell.y - 3.5f);
        }

        /// <summary>从升变类棋子（价值档位 Promoted）随机抽一个（RandomManager.Range——可复现）；池空返回 0。</summary>
        private int PickRandomPromotedDef()
        {
            var candidates = new List<int>();
            foreach (var def in ConfigTable.All<PieceDef>())
            {
                if (_state.GetEffectiveType(def.Id) == PieceType.Promoted)
                {
                    candidates.Add(def.Id);
                }
            }
            if (candidates.Count == 0)
            {
                return 0;
            }
            return candidates[RandomManager.Instance.Range(0, candidates.Count)];
        }

        /// <summary>敌方回合收尾：表现全部完成才切回玩家回合——阶段切换留动画时间。</summary>
        private void TryEndEnemyTurn()
        {
            if (!_enemyTurnEndPending) return;
            if (_state.Phase == BattlePhase.GameOver)
            {
                _enemyTurnEndPending = false;
                return;
            }
            if (_waitingPresentation || _ctx != null) return; // 动画未播完/执行未结束——继续等
            if (_hadEnemyPresentation)
            {
                // 动画路径：表现已完成（PresentationFinished 驱动到达）——直接切回玩家回合
                _enemyTurnEndPending = false;
                StartPlayerTurn();
            }
            // 无动画路径：等 UI 阶段展示信号（PhaseDisplayed——阶段名至少展示一帧）
        }

        /// <summary>UI 阶段展示信号：无动画的敌方回合至少展示一帧后才切回玩家回合。</summary>
        private void OnPhaseDisplayed(object data)
        {
            if (!(data is BattlePhase phase) || phase != BattlePhase.EnemyTurn) return;
            if (_enemyTurnEndPending && !_waitingPresentation && _ctx == null)
            {
                _enemyTurnEndPending = false;
                StartPlayerTurn();
            }
        }

        /// <summary>阶段切换（唯一入口——内部校验转移合法性；副作用只在切换触发）。</summary>
        /// <param name="force">强制切换：阶段相同也发事件（StartBattle 用——塔流程 Phase 可能停在 Placement，幂等 return 会漏发事件导致 UI 无感知）。</param>
        public void ChangePhase(BattlePhase newPhase, bool force = false)
        {
            if (!force && _state.Phase == newPhase)
            {
                return;
            }
            // 阶段切换：清理挂起的执行上下文（表现/选格残留——防跨阶段串扰：
            // 表现播放中结束回合/敌方 ctx 悬垂进玩家回合，都会让 AdvanceSlot 在错误阶段继续推进）
            if (_ctx != null && (_waitingPresentation || _waitingCellSelect))
            {
                _ctx = null;
                _waitingPresentation = false;
                _waitingCellSelect = false;
            }
            if (newPhase == BattlePhase.GameOver)
            {
                _enemyTurnEndPending = false; // 终局：不再切回玩家回合
            }
            _state.Phase = newPhase;
            EventCenter.Instance.EventTrigger(GameEvent.PhaseChanged, newPhase);
        }

        // ========== 统一执行管线 ==========

        public void OnPlayerRequestDeploy(DeployRequest request) => ProcessRequest(request, Side.Player);
        public void OnPlayerRequestPromote(PromoteRequest request) => ProcessRequest(request, Side.Player);
        public void OnPlayerRequestExecute(ExecuteRequest request) => ProcessRequest(request, Side.Player);
        public void OnPlayerRequestDraw(DrawCardRequest request) => ProcessRequest(request, Side.Player);
        public void OnPlayerRequestPlayMahjong(PlayMahjongRequest request) => ProcessRequest(request, Side.Player);
        public void OnPlayerRequestMochi(MochiRequest request) => ProcessRequest(request, Side.Player);
        public void OnPlayerRequestHu(HuRequest request) => ProcessRequest(request, Side.Player);
        // ========== 2026-08-24 新玩法请求入口（骰子/围棋/代币）==========
        public void OnPlayerRequestRollDice(RollDiceRequest request) => ProcessRequest(request, Side.Player);
        public void OnPlayerRequestDiceMove(DiceMoveRequest request) => ProcessRequest(request, Side.Player);
        public void OnPlayerRequestDeployGo(DeployGoRequest request) => ProcessRequest(request, Side.Player);
        public void OnPlayerRequestBuyToken(BuyTokenRequest request) => ProcessRequest(request, Side.Player);
        public void OnPlayerRequestBuyGo(BuyGoRequest request) => ProcessRequest(request, Side.Player); // 2026-08-24 能力「买子」

        private void ProcessRequest(Request request, Side side)
        {
            if (!_guard.TryEnter())
            {
                return; // 防重入（执行中重复请求拒绝）
            }
            try
            {
                // 阶段校验（Placement 允许玩家部署初始棋子；PlayerTurn 允许玩家操作；EnemyTurn 允许 AI）
                if (side == Side.Player && _state.Phase != BattlePhase.PlayerTurn && _state.Phase != BattlePhase.Placement)
                {
                    return;
                }
                if (side == Side.Enemy && _state.Phase != BattlePhase.EnemyTurn)
                {
                    return;
                }

                // 2026-08-23 E5 资格（高亮资格式）：玩家行动统一入口判定——
                //   打出资格牌（部署/升变且 cardInstanceId==资格）→ 本次免费 + 落账后立即执行；
                //   其他任何玩家行动（抽牌/执行/麻将/部署其他牌等）→ 资格作废（清 + 提示取消高亮）
                bool qualifiedUse = false;
                if (side == Side.Player && _state.EditedCardQualifyId > 0)
                {
                    int qualifyCard = _state.EditedCardQualifyId;
                    if ((request is DeployRequest qualifiedDeploy && qualifiedDeploy.cardInstanceId == qualifyCard)
                        || (request is PromoteRequest qualifiedPromote && qualifiedPromote.cardInstanceId == qualifyCard))
                    {
                        qualifiedUse = true;
                    }
                    else
                    {
                        ClearEditedCardQualify();
                    }
                }

                // 2026-08-23 成本预检（单一入口覆盖全部玩家行动类型——决策记录_回合结束手动化与AP豁免集）：
                // AP≤0 时仅豁免"免费行动（request.free）"与"E5 资格牌打出（qualifiedUse）"。
                // ⚠️ 未来新增"免费/无需 AP"的行动类型 → 在此豁免集登记（IsExemptFromApCost 统一收口）
                if (side == Side.Player && _state.Phase == BattlePhase.PlayerTurn
                    && _state.PlayerAP <= 0 && !IsExemptFromApCost(request, request.free, qualifiedUse))
                {
                    // ⚠️ 2026-08-26 拒绝信号：AP 耗尽拒绝行动（点棋子执行最常触发）——前端须收尾执行等待态
                    // （防 _executing 悬挂 → 全场点击被吞/棋子点不了/玩法按钮置灰）
                    EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "request-rejected");
                    return; // AP 耗尽——拒绝普通行动，等待玩家手动"结束回合"
                }

                switch (request)
                {
                    case DeployRequest deploy:
                        // 部署合法性校验（请求校验清单）：
                        //   ① 格子合法（部署区/界内/不占用）
                        //   ② 玩家：阶段限定种类（Placement=Initial / PlayerTurn=Deployable）+ 手牌持有（防重复部署）
                        //   ③ 敌方（波次）：不受种类/手牌限制（波次部署直接 Resolve，不走此分支——此处仅防御）
                        var deployDef = ConfigTable.Find<PieceDef>(deploy.pieceDefId);
                        bool deployValid = IsValidDeployCell(side, deploy.cell)
                            && deployDef != null
                            && (side == Side.Enemy || IsDeployAllowed(deployDef, _state.Phase));
                        if (deployValid)
                        {
                            // ⚠️ 2026-08-26 语义扩展（用户定案）：初始棋子**任何途径**获得后战斗中可免费部署（延续起始摆位——
            // 原仅代币玩法 C5 特例；属性/事件等途径获得的初始牌不再卡手）
            bool freeDeploy = side == Side.Player && deployDef != null
                && _state.GetEffectiveType(deployDef.Id) == PieceType.Initial;
                            var deployAction = new DeployAction(deploy.pieceDefId, side, deploy.cell) { cardInstanceId = deploy.cardInstanceId }; // 2026-08-21：精确消费实例 id
                            _resolver.Resolve(deployAction);
                            DeductActionPoint(request.free || qualifiedUse || freeDeploy, side); // 2026-08-23 E5：打出资格牌免费（不扣 AP）
                            if (qualifiedUse)
                            {
                                var newPiece = _state.GetPieceAt(deploy.cell); // 部署落账后该格即新棋子
                                if (newPiece != null) EnqueueQualifiedExecute(newPiece.Id); // 立即执行一次（free——插入链）
                            }
                        }
                        break;
                    case PromoteRequest promote:
                        var piece = _state.GetPiece(promote.pieceId);
                        var promoteDef = ConfigTable.Find<PieceDef>(promote.newDefId);
                        // 升变规则（放宽）：任意【非升变】棋子 + 手牌有【升变牌】→ 可升变（无映射限制）
                        // ⚠️ 2026-08-15：类型 = 价值档位推导（升变 = 7+ 档；编辑跨档后判定随之变化）
                        // ⚠️ 2026-08-24：围棋棋子默认不可升变（IsGo 拒绝）；能力「假定」激活 → 放行（用手牌升变牌，升变为该牌棋子）
                        bool goPromoteAllowed = piece != null && piece.IsGo && _state.HasRelicEffect(RelicEffectType.GoPromote);
                        bool promoteValid = piece != null && (goPromoteAllowed || (!piece.IsGo
                            && _state.GetEffectiveType(piece.DefId) != PieceType.Promoted))
                            && piece.side == side
                            && promoteDef != null && _state.GetEffectiveType(promoteDef.Id) == PieceType.Promoted
                            && HasPieceInHand(promote.newDefId);
                        if (promoteValid)
                        {
                            var promoteAction = new PromoteAction(promote.pieceId, promote.newDefId) { cardInstanceId = promote.cardInstanceId }; // 2026-08-21：精确消费实例 id
                            _resolver.Resolve(promoteAction);
                            DeductActionPoint(request.free || qualifiedUse, side); // 2026-08-23 E5：打出资格牌免费（不扣 AP）
                            if (qualifiedUse)
                            {
                                var promoted = _state.GetPiece(promote.pieceId); // 升变后实例保持 Id
                                if (promoted != null) EnqueueQualifiedExecute(promoted.Id); // 立即执行一次（free——插入链）
                            }
                        }
                        break;
                    case ExecuteRequest execute:
                        // ⚠️ 2026-08-26 拒绝信号：执行请求棋子不存在/非本方 → 前端收尾执行等待态（防悬挂）
                        if (_state.GetPiece(execute.pieceId)?.side != side)
                        {
                            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "request-rejected");
                        }
                        if (_state.GetPiece(execute.pieceId)?.side == side)
                        {
                            if (_state.GetPiece(execute.pieceId).IsGo)
                            {
                                EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "request-rejected"); // 2026-08-26 拒绝信号：围棋不可行动（同悬挂风险）
                                break; // 2026-08-24 围棋不可行动（含骰子移动重定向——均拒绝）
                            }
                            // ⚠️ 2026-08-24 骰子玩法：全场"点数直线移动"buff——点某棋子执行时重定向
                            // （不进入普通执行/扣 AP 逻辑；执行点数步直线移动后取消全场 buff）
                            if (side == Side.Player && _state.IsStyleActive(StyleRegistry.Dice) && _state.DiceMovePending)
                            {
                                TryStartDiceMove(execute.pieceId, side);
                                break; // 骰子移动处理——不继续普通执行
                            }
                            // ⚠️ 2026-08-22 行动经济（ActionEconomy）：普通执行不耗 AP + 每棋子每回合一次；
                            // 免费行动/额外行动（request.free——击杀触发）为"额外行动"——穿透限制（不查已行动集）。
                            // ⚠️ 2026-08-24 敌我边界修正：行动经济**己方限定**（决策记录_能力事件——敌方不受此能力）
                            bool isExtra = request.free; // 额外行动（免费行动）——穿透
                            if (side == Side.Player && _state.ActionEconomyActive && !isExtra)
                            {
                                if (_state.ActionEconomyActed.Contains(execute.pieceId))
                                {
                                    Debug.LogWarning($"[BattleFlow] 行动经济：该棋子本回合已执行过行动——拒绝（piece={execute.pieceId}）");
                                    // ⚠️ 2026-08-26 拒绝信号：前端执行等待态悬挂（_executing 卡 true → 全场点击被吞、棋子点不了）——
                                    // 拒绝必须通知前端收尾（前端监听 StateChanged("request-rejected") → FinishExec 清执行态）
                                    EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "request-rejected");
                                    break; // 已行动过——拒绝本次普通执行
                                }
                            }
                            // 免费执行资格（额外行动——方案 B）：有资格 → 本次免费 + 资格用掉（保留到使用为止，有效期待策划拍板）
                            // ⚠️ 2026-08-20 统一入口：资格用掉经 Resolver（ConsumeFreeExecute）——BattleFlow 不再直写状态（回归落账纪律）
                            // ⚠️ 2026-08-24：免费/行动经济均**己方限定**（敌方 AI 有独立预算逻辑）
                            bool free = isExtra || (side == Side.Player
                                && (_state.ActionEconomyActive || _state.FreeExecutes.Contains(execute.pieceId)));
                            if (isExtra && _state.FreeExecutes.Contains(execute.pieceId))
                            {
                                _resolver.ConsumeFreeExecute(execute.pieceId);
                            }
                            ExecutePiece(execute.pieceId, free, side); // 玩家逐槽选择 / AI 自动选（内部按 side 分流）
                            if (side == Side.Player && _state.ActionEconomyActive)
                            {
                                _state.ActionEconomyActed.Add(execute.pieceId); // 标记本回合已行动（buff 切态 B）
                            }
                        }
                        break;
                    case DrawCardRequest:
                        // 抽牌行动（2026-08-19 策划确认）：1 AP 抽 1 张；抽牌堆空 → 拒绝（无操作不扣费）
                        // ⚠️ 2026-08-22 能力 DrawExtra：花费 AP 抽牌时额外抽一张
                        // ⚠️ 2026-08-23 E5：花费 AP 抽牌抽到被编辑过的棋子牌 → 自动部署/升变 + 立即执行一次（插入链）
                        if (side == Side.Player && _state.DrawPile != null && _state.DrawPile.Count > 0)
                        {
                            CheckEditedDrawQualify(_resolver.DrawCard()); // 主抽一张（E5 资格检测）
                            int extra = 0;
                            foreach (var relic in _state.Relics)
                            {
                                if (relic == null) continue;
                                foreach (var e in relic.effects)
                                {
                                    if (e != null && e.type == RelicEffectType.DrawExtra) extra += e.value;
                                }
                            }
                            for (int i = 0; i < extra && _state.DrawPile.Count > 0; i++)
                            {
                                CheckEditedDrawQualify(_resolver.DrawCard()); // 额外张同样检测（同属该次抽牌行动）
                            }
                            DeductActionPoint(request.free, side);
                        }
                        break;
                    case PlayMahjongRequest pm:
                        // 麻将·打出墙体（2026-08-20）：手牌有该麻将牌 + 墙体放置合法 → 1 AP
                        if (side == Side.Player && _state.IsStyleActive(Mahjong.StyleId)
                            && HasMahjongInHand(pm.mahjongValue))
                        {
                            // 从手牌取第一张该点数的麻将牌（Card 构造）
                            var mahjongCard = Card.Mahjong(pm.mahjongValue);
                            if (_resolver.PlayMahjongWall(mahjongCard, pm.cell))
                            {
                                DeductActionPoint(request.free, side);
                            }
                        }
                        break;
                    case MochiRequest mo:
                        // 麻将·摸切（2026-08-20）：手牌有该麻将牌 → 填牌山 + 抽一张；1 AP
                        if (side == Side.Player && _state.IsStyleActive(Mahjong.StyleId)
                            && HasMahjongInHand(mo.mahjongValue))
                        {
                            _resolver.MochiCut(Card.Mahjong(mo.mahjongValue));
                            DeductActionPoint(request.free, side);
                        }
                        break;
                    case HuRequest:
                        // 麻将·和牌（2026-08-20）：手牌有雀头（任意两牌价值相同）且番数 > 0 → 1 AP → 倍率+番数、番数清零
                        if (side == Side.Player && _state.IsStyleActive(Mahjong.StyleId)
                            && _state.FanCount > 0 && HasHuHeadInHand())
                        {
                            if (_resolver.Hu(_state.FanCount))
                            {
                                DeductActionPoint(request.free, side);
                            }
                        }
                        break;
                    // ========== 2026-08-24 新玩法（骰子/围棋/代币——设计定稿；仅玩家侧）==========
                    case RollDiceRequest:
                        // 骰子·投掷（执行类行动 1 AP）：随机 1~6 → 点数 + 基础分
                        // ⚠️ 2026-08-24 能力「出千」：投掷点数可自选——挂起自选瞬态（前端弹 1-6 选择 → OnDiceNumberSelected 落账；不跨回合）
                        if (side == Side.Player && _state.IsStyleActive(StyleRegistry.Dice))
                        {
                            if (_state.HasRelicEffect(RelicEffectType.DiceRig))
                            {
                                _diceRigPending = true;
                                EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "dice-rig-select");
                            }
                            else
                            {
                                _resolver.RollDice();
                            }
                            DeductActionPoint(request.free, side);
                        }
                        break;
                    case DiceMoveRequest:
                        // 骰子·点数直线移动启动（不耗 AP——消耗点数；挂全场 buff；AP=0 豁免见 IsExemptFromApCost）
                        if (side == Side.Player && _state.IsStyleActive(StyleRegistry.Dice))
                        {
                            _resolver.StartDiceMove(); // 点数>0 才成功（内部校验）
                        }
                        break;
                    case DeployGoRequest dg:
                        // 围棋·部署"棋子牌"（不耗 AP、每回合容量内[免费限次+买子]、任意**空**格[非占用/非障碍/非墙体]；AP=0 豁免）
                        if (side == Side.Player && _state.IsStyleActive(StyleRegistry.Go)
                            && _boardRules.IsInsideBoard(dg.cell)
                            && _state.GoDeployCount < _state.GoDeployCapacity()
                            && !_state.Pieces.ContainsKey(dg.cell) && !_state.IsBlocked(dg.cell))
                        {
                            _resolver.DeployGoPiece(dg.cell); // 内部含围杀检查
                        }
                        break;
                    case BuyTokenRequest bt:
                        // 代币·购买（不耗 AP——消耗代币；选弃牌区牌 → 复制入手牌；AP=0 豁免）
                        if (side == Side.Player && _state.IsStyleActive(StyleRegistry.Token))
                        {
                            _resolver.BuyFromDiscard(bt.discardIndex);
                        }
                        break;
                    case BuyGoRequest:
                        // 围棋·买子（2026-08-24 能力「买子」：固定费用代币 → 一次部署次数[当回合]；不耗 AP；费用/余额校验在 Resolver；AP=0 豁免）
                        if (side == Side.Player && _state.IsStyleActive(StyleRegistry.Go) && _state.IsStyleActive(StyleRegistry.Token))
                        {
                            _resolver.BuyGoDeploy();
                        }
                        break;
                }
            }
            finally
            {
                _guard.Exit();
            }
            CheckVictory(false);
            CheckActionPoints();
        }

        private void CheckActionPoints()
        {
            if (_state.Phase == BattlePhase.PlayerTurn && _state.PlayerAP <= 0)
            {
                // 2026-08-23 定案（决策记录_回合结束手动化与AP豁免集）：AP 用完**不自动结束回合**——
                // 由玩家手动"结束回合"（为免费行动 / E5 资格 等免费机制预留操作窗口）
                EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "ap-empty"); // 前端可选提示："行动点耗尽，请结束回合"
                return;
            }
            // 敌方回合照旧：ResolveEnemyTurn 收尾自动回玩家回合（AI 无手动操作）
        }

        /// <summary>成本预检豁免集（2026-08-23 决策记录_回合结束手动化与AP豁免集——单一收口）。
        /// ⚠️ 未来新增"免费/无需 AP"的行动类型时，在此登记（连同前端契约一起）——避免散落各处忘加。
        /// 2026-08-24 登记：围棋部署/代币购买/骰子移动启动/买子（新玩法免费类——不耗 AP）。</summary>
        private bool IsExemptFromApCost(Request request, bool requestFree, bool qualifiedUse)
        {
            if (requestFree || qualifiedUse) return true;
            // ⚠️ 2026-08-26 行动经济「高效指挥」：普通执行免 AP（每棋每回合一次）——AP=0 时仍可执行，
            // 不必结束回合（此前缺口：AP 预检拦死 → AP 耗尽只能结束回合，与"免费执行"语义矛盾）；
            // 该棋本回合已行动过 → 不豁免（预检拒绝 → request-rejected 信号 → 前端收尾，ExecutePiece 内已行动检查双兜底）
            if (request is ExecuteRequest execute && _state.ActionEconomyActive
                && !_state.ActionEconomyActed.Contains(execute.pieceId))
            {
                return true;
            }
            // ⚠️ 2026-08-26 初始棋子部署免费（延续起始摆位语义——任何途径获得的初始牌都可免费摆上场；含 AP=0 豁免）
            if (request is DeployRequest deployReq)
            {
                var deployDef = ConfigTable.Find<PieceDef>(deployReq.pieceDefId);
                if (deployDef != null && _state.GetEffectiveType(deployDef.Id) == PieceType.Initial)
                {
                    return true;
                }
            }
            return request is DeployGoRequest || request is BuyTokenRequest || request is DiceMoveRequest || request is BuyGoRequest; // 2026-08-24 买子
        }

        private void DeductActionPoint(bool free, Side side)
        {
            if (free)
            {
                return;
            }
            if (side == Side.Player)
            {
                _state.PlayerAP--;
                if (_state.PlayerAP < 0)
                {
                    // 2026-08-23 兜底钳制：防绕过成本预检的扣费路径（负值回收 + 预警暴露）
                    _state.PlayerAP = 0;
                    Debug.LogError("[BattleFlow] AP 扣成负数——存在未走 ProcessRequest 成本预检的扣费路径（免费豁免集未登记？）");
                }
            }
            else
            {
                _state.EnemyAP--;
                if (_state.EnemyAP < 0)
                {
                    _state.EnemyAP = 0;
                    Debug.LogWarning("[BattleFlow] 敌方 AP 扣成负数——敌方预算逻辑异常");
                }
            }
            EventCenter.Instance.EventTrigger(GameEvent.ActionPointChanged, new ApInfo { Side = side, Current = side == Side.Player ? _state.PlayerAP : _state.EnemyAP, Max = side == Side.Player ? _state.PlayerAPMax : _state.EnemyAPMax });
        }

        // ========== 逐槽可续执行 ==========

        // ⚠️ 2026-08-22 插入执行（免费行动"获得即立即执行"——复用免费行动逻辑/同执行链；E5 同链待接）：
        // 击杀授予免费行动 → ExtraActionGranted → 入队 → 当前请求收尾/表现排空后强制该棋执行（free=额外行动）。
        private readonly Queue<int> _pendingEnemyImmediateExecutes = new Queue<int>(); // 2026-08-24：敌方击杀触发额外行动（敌方回合内立即再执行一次——AI 自动；玩家队列见 L59）

        private void OnExtraActionGranted(object data)
        {
            if (!(data is int pieceId)) return;
            var piece = _state.GetPiece(pieceId);
            if (piece == null) return;
            if (piece.side == Side.Player)
            {
                _pendingImmediateExecutes.Enqueue(pieceId);
                TryFlushImmediateExecutes();
            }
            else
            {
                // ⚠️ 2026-08-24 敌我边界修正：敌方击杀触发 OnKill+ExtraAction → 敌方回合内立即额外行动执行
                //（不进入玩家免费资格机制——AI 自动选格，free=额外）
                _pendingEnemyImmediateExecutes.Enqueue(pieceId);
                TryFlushEnemyImmediateExecutes();
            }
        }

        /// <summary>敌方立即额外行动（2026-08-24：敌方回合内空闲时强制该棋再执行一次——与玩家 TryFlushImmediateExecutes 对称）。</summary>
        private void TryFlushEnemyImmediateExecutes()
        {
            while (_pendingEnemyImmediateExecutes.Count > 0)
            {
                if (_state.Phase != BattlePhase.EnemyTurn || _ctx != null || _waitingPresentation)
                {
                    return; // 非空闲（非敌方回合/执行中/表现中——收尾点再触发）
                }
                int pieceId = _pendingEnemyImmediateExecutes.Dequeue();
                var piece = _state.GetPiece(pieceId);
                if (piece == null || piece.side != Side.Enemy)
                {
                    continue; // 已不在/非敌方——跳过
                }
                ExecutePiece(pieceId, true, Side.Enemy); // 立即执行（额外行动——AI 自动选格）
                return; // 一次一个（执行收尾点再次触发）
            }
        }

        /// <summary>空闲时触发强制插入执行（玩家回合 + 非执行中 + 队非空 → 出队执行该棋——free=额外）。</summary>
        private void TryFlushImmediateExecutes()
        {
            while (_pendingImmediateExecutes.Count > 0)
            {
                if (_state.Phase != BattlePhase.PlayerTurn || _ctx != null || _waitingPresentation)
                {
                    return; // 非空闲（敌方回合/执行中/表现中——等收尾点再触发）
                }
                int pieceId = _pendingImmediateExecutes.Dequeue();
                var piece = _state.GetPiece(pieceId);
                if (piece == null || piece.side != Side.Player)
                {
                    continue; // 棋子已不在/非玩家——跳过
                }
                // 立即执行（额外行动——穿透行动经济限制；资格用掉经 Resolver）
                _resolver.ConsumeFreeExecute(pieceId);
                ExecutePiece(pieceId, true, Side.Player);
                return; // 一次一个（执行中的收尾点会再次触发）
            }
        }

        // ========== E5：抽到编辑牌 → 资格授予（2026-08-23 高亮资格式定案——玩家决策：打出=使用[免费+立即执行]，其他行动=作废，回合结束清）==========

        /// <summary>抽牌后的 E5 资格检测：持有能力 + 抽到棋子牌 + 该棋子被编辑过 → 授予资格（牌实例 id——显示真值）+ 发通用提示（前端手牌/能力面板高亮）。</summary>
        private void CheckEditedDrawQualify(Card? card)
        {
            if (card == null) return;
            var c = card.Value;
            if (!c.IsPiece) return; // 非棋子牌（麻将）不触发
            if (!_state.HasRelicEffect(RelicEffectType.DrawEditedImmediate)) return; // 未持有 E5 能力
            if (!_state.CurrentPrograms.ContainsKey(c.defId)) return; // 该棋子未被编辑过
            _state.EditedCardQualifyId = c.instanceId;
            EventCenter.Instance.EventTrigger(GameEvent.HintRequested, new HintPayload { kind = HintKind.CardQualify, targetId = c.instanceId });
        }

        /// <summary>E5 资格取消（清状态 + 发提示——高亮消失；其他行动作废/回合结束清共用）。</summary>
        private void ClearEditedCardQualify()
        {
            if (_state.EditedCardQualifyId == 0) return;
            _state.EditedCardQualifyId = 0;
            EventCenter.Instance.EventTrigger(GameEvent.HintRequested, new HintPayload { kind = HintKind.CardQualify, targetId = 0 });
        }

        /// <summary>E5 资格使用：打出资格牌（部署/升变）落账后 → 立即执行一次（free——插入执行链同免费行动）。</summary>
        private void EnqueueQualifiedExecute(int pieceId)
        {
            _pendingImmediateExecutes.Enqueue(pieceId);
            TryFlushImmediateExecutes();
        }

        private void ExecutePiece(int pieceId, bool free, Side side)
        {
            var piece = _state.GetPiece(pieceId);
            if (piece == null)
            {
                return;
            }
            var program = piece.GetProgram(_state);
            if (program == null || program.Count == 0)
            {
                return; // 无程序（Def 未配置）——防御
            }
            _ctx = new ExecContext
            {
                pieceId = pieceId,
                side = side,
                program = program, // 执行开始即程序定稿
                slotIndex = 0,
                free = free,
            };
            AdvanceSlot();
        }

        private void AdvanceSlot()
        {
            if (_ctx == null)
            {
                return;
            }
            var piece = _state.GetPiece(_ctx.pieceId);
            if (piece == null || _ctx.slotIndex >= _ctx.program.Count)
            {
                FinishExecute();
                return;
            }
            var slot = _ctx.program[_ctx.slotIndex];
            switch (slot)
            {
                case MoveTemplate move:
                    var moveOptions = _intentResolver.GetMoveOptions(_state, piece, move);
                    if (moveOptions.Count == 0)
                    {
                        _resolver.Resolve(new SkipAction(piece.Id, SkipReason.NoMove)); // 无表现，不等待
                        _ctx.slotIndex++;
                        AdvanceSlot();
                    }
                    else if (piece.side == Side.Player)
                    {
                        _waitingCellSelect = true; // 暂停等玩家选落点
                    }
                    else
                    {
                        // 敌方 AI 自动选（短视吃子：靠近敌人）→ 落账 → 表现等待
                        var aiTarget = _intentResolver.PickClosestToEnemy(_state, piece, moveOptions);
                        _resolver.Resolve(_intentResolver.ResolveMove(piece, aiTarget));
                        _ctx.slotIndex++;
                        WaitPresentation();
                    }
                    break;
                case AttackTemplate attack:
                    // ⚠️ 2026-08-24 能力「吃子」：玩家侧执行**跳过攻击槽**（攻击行动不生效——移动吃子代替；纯攻击槽程序 = 纯跳过——策划定案）
                    if (piece.side == Side.Player && _state.HasRelicEffect(RelicEffectType.Devour))
                    {
                        _resolver.Resolve(new SkipAction(piece.Id, SkipReason.NoAttack));
                        _ctx.slotIndex++;
                        AdvanceSlot();
                        break;
                    }
                    if (piece.side == Side.Player)
                    {
                        var playerAttackOptions = _intentResolver.GetAttackOptions(_state, piece, attack);
                        if (playerAttackOptions.Count == 0)
                        {
                            // 候选为空（被障碍包围/射程无格）：与 Move 槽一致走 Skip，防永久等待选格死锁
                            _resolver.Resolve(new SkipAction(piece.Id, SkipReason.NoTarget));
                            _ctx.slotIndex++;
                            AdvanceSlot();
                        }
                        else
                        {
                            _waitingCellSelect = true; // 暂停等玩家选目标格（可空放/打己方）
                        }
                    }
                    else
                    {
                        // 敌方 AI 自动选目标（HighestValue）→ 落账 → 表现等待
                        // ⚠️ 2026-08-13 ②：候选过滤为【玩家占位格】——无玩家目标 → Skip（不空放、不打己方——
                        // 与决策层"移动后位置评估"一致：决策判定能打到玩家才产请求，执行时打玩家目标；
                        // 空放成为玩家专属（A2b 玩家语义保持）；玩家侧路径不受影响）
                        var aiAttackOptions = _intentResolver.GetAttackOptions(_state, piece, attack);
                        var playerTargets = new List<Vector2Int>();
                        if (aiAttackOptions.Count > 0)
                        {
                            foreach (var cell in aiAttackOptions)
                            {
                                var t = _state.GetPieceAt(cell);
                                if (t != null && t.side == Side.Player)
                                {
                                    playerTargets.Add(cell);
                                }
                            }
                        }
                        if (playerTargets.Count == 0)
                        {
                            // ⚠️ 2026-08-24 围棋防堵路：无玩家目标但移动候选被红围棋（敌方围棋棋子）占据 → 清障攻击（防玩家用红围棋堵路）
                            var blockedGo = FindBlockedByGo(piece);
                            if (blockedGo != null && aiAttackOptions.Contains(blockedGo.Value))
                            {
                                playerTargets.Add(blockedGo.Value);
                            }
                        }
                        if (playerTargets.Count == 0)
                        {
                            // ✅ 2026-08-19 策划确认（"就是空放"）：敌方无玩家目标 → **空放**（攻击空格——挥空动画）。
                            // 选空格挥空（不打己方——候选可能含敌方棋子）；候选无空格（全是己方格）→ 仍 Skip
                            Vector2Int? emptyCell = null;
                            foreach (var cell in aiAttackOptions)
                            {
                                if (_state.GetPieceAt(cell) == null)
                                {
                                    emptyCell = cell;
                                    break;
                                }
                            }
                            if (emptyCell != null)
                            {
                                _resolver.Resolve(_intentResolver.ResolveAttack(_state, piece, emptyCell.Value, attack)); // 空格挥空（DamageDealt TargetId=-1——UI 挥空动画）
                                _ctx.slotIndex++;
                                WaitPresentation();
                            }
                            else
                            {
                                _resolver.Resolve(new SkipAction(piece.Id, SkipReason.NoTarget)); // 无空格可挥（候选全是己方格）
                                _ctx.slotIndex++;
                                AdvanceSlot();
                            }
                        }
                        else
                        {
                            var aiTarget = _intentResolver.PickTarget(_state, piece, playerTargets, _aiParams.targetRule);
                            _resolver.Resolve(_intentResolver.ResolveAttack(_state, piece, aiTarget, attack));
                            _ctx.slotIndex++;
                            WaitPresentation();
                        }
                    }
                    break;
                case SkipTemplate:
                    // ⚠️ 2026-08-15：新规则行动槽仅移动/攻击/效果（无跳过槽——不可编排）；
                    // 本分支为兼容保留（运行时自动跳过 NoMove/NoTarget 仍走 SkipAction——执行兜底不可删）
                    _resolver.Resolve(new SkipAction(piece.Id, SkipReason.NoMove));
                    _ctx.slotIndex++;
                    AdvanceSlot();
                    break;
                case EffectTemplate effect:
                    // ✅ 2026-08-19 效果模块执行语义（策划确认）：**不耗 AP、被动、有模块即生效**——
                    // 执行序列中不产生行动（跳过不落账不扣费）；能力生效 = PieceInstance.GetAllAbilities 动态并入（装配即生效）。
                    _ctx.slotIndex++;
                    AdvanceSlot();
                    break;
            }
        }

        /// <summary>玩家选格（UI 调用——移动落点/攻击目标）。</summary>
        public void OnPlayerCellSelected(Vector2Int cell)
        {
            if (!_waitingCellSelect || _ctx == null || _state.Phase != BattlePhase.PlayerTurn)
            {
                return;
            }
            _waitingCellSelect = false;
            var piece = _state.GetPiece(_ctx.pieceId);
            if (piece == null)
            {
                // ⚠️ 2026-08-12：选格时棋子死亡（防御分支）——原直接 return 残留 _ctx（不落账不扣 AP）；
                // 改 FinishExecute 完整结算（补扣 AP + 清 _ctx——发起过执行就该结账）
                FinishExecute();
                return;
            }
            if (_ctx.slotIndex >= _ctx.program.Count)
            {
                // 防御：程序已走完（镜像/表现竞态导致 UI 残留选格请求）——兜底结束执行
                FinishExecute();
                return;
            }
            var slot = _ctx.program[_ctx.slotIndex];
            switch (slot)
            {
                case MoveTemplate move:
                    // ⚠️ 2026-08-12：原只查 IsCellPassable（界内/非障碍/非占用）——不查移动候选内，
                    // UI 镜像分叉时棋子可瞬移到模板范围外；改候选内校验（与攻击槽对称——规则层不依赖 UI）
                    if (!_intentResolver.GetMoveOptions(_state, piece, move).Contains(cell))
                    {
                        _waitingCellSelect = true; // 不在移动候选内——重新选
                        return;
                    }
                    _resolver.Resolve(_intentResolver.ResolveMove(piece, cell));
                    break;
                case AttackTemplate attack:
                    var options = _intentResolver.GetAttackOptions(_state, piece, attack);
                    if (!options.Contains(cell))
                    {
                        _waitingCellSelect = true; // 不在攻击范围——重新选
                        return;
                    }
                    _resolver.Resolve(_intentResolver.ResolveAttack(_state, piece, cell, attack));
                    break;
            }
            _ctx.slotIndex++;
            WaitPresentation(); // 槽位落账后进入表现等待
        }

        // ========== 骰子·点数直线移动（2026-08-24 设计定稿——消耗点数启动 → 全场 buff → 点棋子执行重定向）==========

        private int _waitingDiceMovePieceId = -1; // 骰子移动"选方向"等待（-1=未等待）
        private Side _waitingDiceMoveSide;
        /// <summary>能力「出千」自选等待（2026-08-24：投掷请求挂起——弹 1-6 自选；不跨回合——回合开始清；瞬态不入档）。</summary>
        private bool _diceRigPending;

        /// <summary>出千自选结果（2026-08-24 前端回调：投掷自选面板选数后调用——校验 1-6 + 等待态 + 玩家回合；落账同普通投掷）。</summary>
        public void OnDiceNumberSelected(int value)
        {
            if (!_diceRigPending || _state.Phase != BattlePhase.PlayerTurn) return;
            _diceRigPending = false;
            _resolver.RollDiceChosen(value); // 内部校验 1-6 并落账
        }

        /// <summary>骰子移动启动（ExecuteRequest 重定向进入）：进入方向选择（上/下/左/右——点数步直线）。</summary>
        private void TryStartDiceMove(int pieceId, Side side)
        {
            var piece = _state.GetPiece(pieceId);
            if (piece == null || piece.side != side || _state.DiceMoveSteps <= 0) return;
            _waitingDiceMovePieceId = pieceId;
            _waitingDiceMoveSide = side;
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "dice-move-select"); // 前端进入方向选择（上下左右）
        }

        /// <summary>骰子移动方向选择（UI 调用——上下左右单方向；路径逐格界内非障碍、终点非占用；成功落账 + 清全场 buff；不扣 AP）。</summary>
        public void OnDiceDirectionSelected(Direction direction)
        {
            if (_waitingDiceMovePieceId < 0 || _state.Phase != BattlePhase.PlayerTurn) return;
            int pieceId = _waitingDiceMovePieceId;
            _waitingDiceMovePieceId = -1;
            var piece = _state.GetPiece(pieceId);
            if (piece == null || piece.side != _waitingDiceMoveSide) { _resolver.FinishDiceMove(); return; }
            int steps = _state.DiceMoveSteps;
            if (steps <= 0) { _resolver.FinishDiceMove(); return; } // 防御：无步数——清 buff
            var dirVec = DirectionToVector(direction);
            if (dirVec == Vector2Int.zero) { _waitingDiceMovePieceId = -1; return; } // 非法方向
            var cursor = piece.position;
            for (int i = 0; i < steps; i++)
            {
                cursor += dirVec;
                if (!_boardRules.IsInsideBoard(cursor) || _state.IsBlocked(cursor))
                {
                    _waitingDiceMovePieceId = pieceId; // 路径受阻——重选（保持等待）
                    EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "dice-move-select");
                    return;
                }
            }
            if (_state.Pieces.ContainsKey(cursor))
            {
                _waitingDiceMovePieceId = pieceId; // 终点占用——重选（"必须能够正好走到终点"）
                EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "dice-move-select");
                return;
            }
            _resolver.Resolve(new MoveAction(pieceId, piece.position, cursor)); // 点数步直线移动（普通移动落账）
            _resolver.FinishDiceMove(); // 执行完取消全场 buff
            WaitPresentation(); // 表现等待（与普通移动同链——前端播完回执）
        }

        private static Vector2Int DirectionToVector(Direction dir)
        {
            switch (dir)
            {
                case Direction.Up: return Vector2Int.up;
                case Direction.Down: return Vector2Int.down;
                case Direction.Left: return Vector2Int.left;
                case Direction.Right: return Vector2Int.right;
                default: return Vector2Int.zero;
            }
        }

        private void FinishExecute()
        {
            if (_ctx == null)
            {
                return;
            }
            var free = _ctx.free;
            var side = _ctx.side;
            _ctx = null;
            // 按发起方扣费（表现完成时阶段可能已切到对方回合，按当前阶段会扣错阵营）
            DeductActionPoint(free, side);
            // ⚠️ 2026-08-22 插入执行：当前整段执行完成后触发（空闲且玩家回合——强制立即执行该棋——free 额外；
            // 2026-08-23 E5 资格使用后同样经此链立即执行）
            TryFlushImmediateExecutes();
            // 执行扣费是异步的（表现完成后），此处补触发 AP 耗尽检查（回合自动移交）
            if (side == Side.Player)
            {
                CheckActionPoints();
            }
            // 敌方逐步决策（2026-08-13）：当前行动完整执行完（扣费后）→ 基于最新状态决策下一步；预算 0/无行动 → 收尾
            if (side == Side.Enemy)
            {
                TryFlushEnemyImmediateExecutes(); // 2026-08-24：敌方击杀触发额外行动——先于正常决策立即执行
                TryNextEnemyDecision();
            }
        }

        // ========== 表现等待（等"表现完成"事件——token 回执 + 超时降级；2026-08-21）==========

        private void WaitPresentation()
        {
            _waitingPresentation = true;
            _timeoutFallbackActive = false;
            _waitingActionId = ++_actionCounter; // 分配等待 token（战斗内递增）
            _timeoutWaitStart = UnityEngine.Time.time;
            if (PresentationTimeoutSeconds > 0)
            {
                CoroutineHost.Instance.Run(TimeoutWatch()); // 超时守望（主动计时——普通类无 Update）
            }
            // 敌方回合表现发生时即锁存"本回合有表现"（2026-08-12：供 EndEnemyTurn 判定动画路径——
            // 串行化后收尾采样时机已过（表现完成、PhaseDisplayed 丢弃），锁存不依赖采样时机；玩家回合不受影响）
            if (_state.Phase == BattlePhase.EnemyTurn)
            {
                _hadEnemyPresentation = true;
            }
            // 等待日志（卡住时定位：谁在等、等哪个表现组）
            Debug.Log($"[BattleFlow] 表现等待开始 piece={_ctx?.pieceId} slot={_ctx?.slotIndex} token=({_battleSessionId},{_waitingActionId})（等'表现完成'事件；超时 {PresentationTimeoutSeconds}s）");
        }

        /// <summary>当前表现等待 token（2026-08-21：前端播完动画后读取并原样带回回执；未等待 = actionId -1）。</summary>
        public (int sessionId, int actionId) CurrentPresentationToken
            => (_battleSessionId, _waitingPresentation ? _waitingActionId : -1);

        /// <summary>
        /// 超时守望协程（2026-08-21）：等待超过 PresentationTimeoutSeconds 未收到匹配回执 →
        /// 降级放行（推进）+ LogError 显著预警（防"降级掩盖问题"）+ 诊断入档（存档可查）。
        /// 禁用：将 PresentationTimeoutSeconds 置 0/负（回到无限等）。
        /// </summary>
        private System.Collections.IEnumerator TimeoutWatch()
        {
            while (_waitingPresentation && !_timeoutFallbackActive)
            {
                float waited = UnityEngine.Time.time - _timeoutWaitStart;
                if (waited >= PresentationTimeoutSeconds)
                {
                    _timeoutFallbackActive = true;
                    int waitMs = (int)(waited * 1000f);
                    // ⚠️ 显著预警：超时 = 前端表现疑似异常（不是静默容忍——请排查前端表现链路）
                    Debug.LogError($"[BattleFlow] 表现回执超时降级：session={_battleSessionId} action={_waitingActionId} 等待 {PresentationTimeoutSeconds}s 无回执——前端表现疑似异常（本次已放行，请排查）");
                    _state.AppendTimeoutRecord(_battleSessionId, _waitingActionId, waitMs, _state.Phase.ToString()); // 诊断入档（存档可查）
                    _waitingPresentation = false;
                    _timeoutWaitStart = 0f;
                    if (_ctx != null)
                    {
                        AdvanceSlot();
                    }
                    TryEndEnemyTurn();
                    yield break;
                }
                yield return null;
            }
        }

        private void OnPresentationFinished(object data)
        {
            // ⚠️ 2026-08-21：token 校验——新协议回执带 (sessionId, actionId)（PresentationInfo）；
            // 旧协议（无数据/宽松）兼容：不回执 token（null/actionId<=0）→ 视同当前等待（过渡期）。
            bool matched = true;
            if (data is PresentationInfo info)
            {
                matched = info.SessionId == _battleSessionId
                    && (info.ActionId <= 0 || info.ActionId == _waitingActionId);
                if (!matched)
                {
                    // 迟到/重复回执：不推进、不误认（日志留痕）
                    Debug.LogWarning($"[BattleFlow] 表现回执 token 不匹配——迟到/重复回执（收到 session={info.SessionId} action={info.ActionId}，当前等待 action={_waitingActionId}）——忽略");
                    return;
                }
            }
            if (_waitingPresentation && matched)
            {
                _waitingPresentation = false;
                _timeoutWaitStart = 0f;
                if (_ctx != null)
                {
                    AdvanceSlot();
                }
                else if (_state.Phase == BattlePhase.EnemyTurn)
                {
                    // AA4-08：波次部署表现（_ctx==null 的 WaitPresentation）完成时补触发敌方决策——
                    // StartEnemyTurn 里 TryNextEnemyDecision 被 _waitingPresentation 守卫拦住后，无人再触发会卡死。
                    TryNextEnemyDecision();
                }
            }
            TryFlushEnemyImmediateExecutes(); // 2026-08-24：敌方击杀额外行动（表现排空后收尾前补触发——被 _ctx/阶段挡则无害）
            TryEndEnemyTurn(); // 动画播完（一轮表现队列排干）——检查敌方回合能否收尾
        }

        // ========== 波次调度 + 敌方升变预告 ==========

        private void HandleWaveAndPromotions()
        {
            // 升变预告倒计时（每敌方回合递减；countdown=1 → 本回合挂载、下一敌方回合升变）。
            // ⚠️ 2026-08-24 时序修复（策划定稿：预告 s-2 / 升变 s-1 / 部署 s——相对部署回合 s）：
            // 旧 promotions 与本波 autoPromote 的预告周期统一锚定"下一波部署回合 s"：
            //   - 预告：s-2 回合挂载（PromoteAnnouncement 挂载 + PromoteAnnounced/BuffsChanged）——旧机制延迟挂载（挂起批次）、autoPromote 前瞻触发；
            //   - 升变：s-1 回合执行（countdown=1 下一敌方回合递减到 0）；
            //   - 部署：s 回合。
            // 末波（无后继波）：autoPromote 周期跳过；旧 promotions 保持原 countdown=1（部署当回合挂载、下一敌方回合升变）。
            foreach (var ann in new List<PromoteAnnouncement>(_state.PromoteAnnouncements))
            {
                ann.countdown--;
                if (ann.countdown <= 0)
                {
                    _state.PromoteAnnouncements.Remove(ann);
                    EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, ann.pieceId); // 预告结束（升变执行/移除）→ buff 消失（2026-08-23）
                    var piece = _state.GetPiece(ann.pieceId);
                    if (piece != null && piece.side == Side.Enemy)
                    {
                        // ⚠️ 2026-08-19：newDefId=0 → 自动预告模式——升变目标从升变类棋子随机（RandomManager，种子相关可复现）
                        int targetDefId = ann.newDefId;
                        if (targetDefId == 0)
                        {
                            targetDefId = PickRandomPromotedDef();
                        }
                        if (targetDefId != 0)
                        {
                            _resolver.Resolve(new PromoteAction(ann.pieceId, targetDefId));
                        }
                    }
                }
            }

            // 旧 promotions 延迟挂载批次（2026-08-24）：预告回合到点才挂载（目标棋子已上场）——countdown=1 → 下一敌方回合（s-1）升变
            for (int i = _pendingPromoBatches.Count - 1; i >= 0; i--)
            {
                var batch = _pendingPromoBatches[i];
                if (batch.announceTurn <= _state.TurnCount)
                {
                    _pendingPromoBatches.RemoveAt(i);
                    if (batch.announceTurn == _state.TurnCount)
                    {
                        MountPromotions(batch.pieces, batch.promos, 1);
                    }
                }
            }

            // autoPromote 前瞻（2026-08-24 时序修复）：autoPromote 波的预告周期锚定下一波部署回合 s——
            // s-2 回合（TurnCount == s-3）预告离中心最近 2 个敌方棋子（countdown=1 → s-1 升变）；末波无后继波 → 跳过
            for (int wi = 0; wi + 1 < _floor.waveDefs.Count; wi++)
            {
                if (_floor.waveDefs[wi].autoPromote
                    && _floor.waveDefs[wi + 1].startTurn - 3 == _state.TurnCount)
                {
                    AnnounceAutoPromotions();
                }
            }

            // 波次部署（按回合数触发；free=true 不走 AP——规则强制部分）
            _deployedThisRound = false;
            while (_deployedWaveIndex < _floor.waveDefs.Count)
            {
                var wave = _floor.waveDefs[_deployedWaveIndex];
                // 波 N 在第 N 回合开始时在场（TurnCount=0 开局部署首波——玩家摆位参照敌方位置）
                if (wave.startTurn - 1 > _state.TurnCount)
                {
                    break;
                }
                var deployedThisWave = new List<PieceInstance>();
                bool anyDeployed = false;
                // 波次阵容：多部署组（2026-08-26 策划第 2-4 关新规则——groups 非空走组：每组独立池/数量/区域[部署区/非部署区随机空格]；
                // 空 = 顶层单组兼容：随机池或固定阵容 + 固定站位/顺序找位——旧配置零改动）
                var groupDefs = BuildWaveGroups(wave);
                var usedCells = new HashSet<Vector2Int>(); // 本波已选格（随机空格防同波踩重）
                foreach (var g in groupDefs)
                {
                    var defIds = g.pieceDefIds;
                    if (g.randomPool)
                    {
                        var candidates = new List<int>();
                        foreach (var def in ConfigTable.All<PieceDef>())
                        {
                            if (_state.GetEffectiveType(def.Id) == g.poolType)
                            {
                                candidates.Add(def.Id);
                            }
                        }
                        if (candidates.Count == 0)
                        {
                            Debug.LogWarning($"[BattleFlow] 波次随机池为空（{g.poolType}）——本组不部署");
                            defIds = new List<int>();
                        }
                        else
                        {
                            defIds = new List<int>();
                            for (int i = 0; i < Mathf.Max(0, g.count); i++)
                            {
                                defIds.Add(candidates[RandomManager.Instance.Range(0, candidates.Count)]);
                            }
                        }
                    }
                    int slot = 0;
                    foreach (var defId in defIds)
                    {
                        var cell = ResolveWaveDeployCell(wave, g, slot, usedCells);
                        slot++;
                        if (cell.x < 0)
                        {
                            break; // 固定站位耗尽 / 区域无空位
                        }
                        var deployAction = new DeployAction(defId, Side.Enemy, cell) { waveIndex = _deployedWaveIndex }; // 打波次标（每波得分）
                        _resolver.Resolve(deployAction);
                        var piece = _state.GetPieceAt(cell);
                        if (wave.spawnShield > 0 && piece != null)
                        {
                            // 2026-08-26 关 4 波 3：部署棋子额外获得护盾——tempShield 持久（入档/升变保留）+ 同步当前护盾量（承伤抵挡立即生效）
                            piece.tempShield += wave.spawnShield;
                            piece.shieldCount = piece.GetShieldAmount();
                        }
                        deployedThisWave.Add(piece);
                        anyDeployed = true;
                    }
                }
                if (!anyDeployed)
                {
                    // ⚠️ 2026-08-13：部署区满零落地——原实现仍推进波次索引（该波永久丢失）+
                    // 置 _deployedThisRound 等待表现（无 PieceDeployed 事件 → AI 无表现时软锁）。
                    // 改为：本波不推进、不等待（下回合波次判定仍成立会重试）。
                    break;
                }
                _deployedThisRound = true;
                // 旧 promotions 机制（2026-08-24 时序修复）：预告周期锚定下一波部署回合 s——
                // 间隔≥3 → 挂起批次到 s-2 回合挂载（目标棋子已上场）；间隔=2 → 部署当回合即 s-2，countdown=1 直接命中；
                // 末波无后继波 → 保持原 countdown=1（部署当回合挂载、下一敌方回合升变）。autoPromote 模式互斥跳过。
                if (!wave.autoPromote && wave.promotions.Count > 0)
                {
                    int nextStartTurn = _deployedWaveIndex + 1 < _floor.waveDefs.Count
                        ? _floor.waveDefs[_deployedWaveIndex + 1].startTurn
                        : -1;
                    if (nextStartTurn > 0 && nextStartTurn - 3 > _state.TurnCount)
                    {
                        _pendingPromoBatches.Add(new PendingPromoBatch
                        {
                            announceTurn = nextStartTurn - 3,
                            pieces = new List<PieceInstance>(deployedThisWave),
                            promos = new List<WavePromotion>(wave.promotions),
                        });
                    }
                    else
                    {
                        int countdown = nextStartTurn > 0 ? Mathf.Max(1, nextStartTurn - wave.startTurn - 1) : 1;
                        MountPromotions(deployedThisWave, wave.promotions, countdown);
                    }
                }
                _state.WaveScores.Add(0);
                _deployedWaveIndex++;
                if (wave.isLastWave)
                {
                    _waveEnded = true;
                    _state.WaveEndCountdown = wave.endCountdown;
                }
            }
            // 部署动画优先：本回合有波次部署 → 挂起等部署表现播完（阶段切换留动画时间）
            if (_deployedThisRound)
            {
                WaitPresentation();
            }

            // 末波倒计时 → 强制结算
            if (_waveEnded && _state.WaveEndCountdown >= 0)
            {
                // ⚠️ 2026-08-13：末波部署当回合不递减——原"设置后同调用立即递减"少 1 回合
                // （配置 endCountdown=3 实际只有 2 回合多；=1 时部署当回合即强制判负）
                if (!_deployedThisRound)
                {
                    _state.WaveEndCountdown--;
                }
                if (_state.WaveEndCountdown <= 0)
                {
                    CheckVictory(true);
                }
            }
        }

        // ========== 2026-08-26 波次部署·多组/随机空格/区域（策划第 2-4 关新规则：部署区随机 N 空格/非部署区随机/护盾）==========

        /// <summary>波次部署组列表：wave.groups 非空 → 用组；空 → 顶层单组包装（随机池/固定阵容 + 敌方部署区）——旧配置零改动。</summary>
        private static List<WaveGroupDef> BuildWaveGroups(WaveDef wave)
        {
            if (wave.groups != null && wave.groups.Count > 0)
            {
                return wave.groups;
            }
            return new List<WaveGroupDef>
            {
                new WaveGroupDef
                {
                    randomPool = wave.randomPool,
                    poolType = wave.poolType,
                    count = wave.count,
                    pieceDefIds = wave.pieceDefIds,
                    deployArea = DeployArea.EnemyDeploy,
                }
            };
        }

        /// <summary>波次部署落点：单组兼容（顶层 positions 固定位 + 被占替代格——旧语义）；否则 randomCells → 区域内随机空格；否则区域内顺序找位。</summary>
        private Vector2Int ResolveWaveDeployCell(WaveDef wave, WaveGroupDef g, int slot, HashSet<Vector2Int> usedCells)
        {
            bool singleLegacy = wave.groups == null || wave.groups.Count == 0;
            if (singleLegacy && wave.positions.Count > 0)
            {
                var cell = slot < wave.positions.Count ? wave.positions[slot] : new Vector2Int(-1, -1);
                if (cell.x >= 0 && (_state.Pieces.ContainsKey(cell) || _state.Obstacles.Contains(cell)))
                {
                    // ⚠️ 2026-08-24 策划新语义：该出棋子的位置被占 → 敌方部署区选别的空格；部署区满 → 部署区外一排找，以此类推
                    cell = FindAlternateDeployCell(Side.Enemy);
                }
                return cell;
            }
            if (wave.randomCells)
            {
                return FindRandomDeployCell(g.deployArea, usedCells);
            }
            return FindDeployCellArea(g.deployArea);
        }

        /// <summary>区域内随机空格（2026-08-26：收集区域空格 → RandomManager 随机抽——种子可复现；排除棋盘占用/障碍/本波已选）。</summary>
        private Vector2Int FindRandomDeployCell(DeployArea area, HashSet<Vector2Int> usedCells)
        {
            var free = new List<Vector2Int>();
            for (int y = AreaMinY(area); y <= AreaMaxY(area); y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    var cell = new Vector2Int(x, y);
                    if (!_state.Pieces.ContainsKey(cell) && !_state.Obstacles.Contains(cell) && !usedCells.Contains(cell))
                    {
                        free.Add(cell);
                    }
                }
            }
            if (free.Count == 0)
            {
                return new Vector2Int(-1, -1);
            }
            var pick = free[RandomManager.Instance.Range(0, free.Count)];
            usedCells.Add(pick);
            return pick;
        }

        /// <summary>区域内顺序找位（非随机——randomCells=false；EnemyDeploy 保持 y6-7 顺序，与旧 FindDeployCell 一致）。</summary>
        private Vector2Int FindDeployCellArea(DeployArea area)
        {
            for (int y = AreaMinY(area); y <= AreaMaxY(area); y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    var cell = new Vector2Int(x, y);
                    if (!_state.Pieces.ContainsKey(cell) && !_state.Obstacles.Contains(cell))
                    {
                        return cell;
                    }
                }
            }
            return new Vector2Int(-1, -1); // 区域无空位
        }

        private static int AreaMinY(DeployArea area) => area == DeployArea.Midfield ? 2 : 6;

        private static int AreaMaxY(DeployArea area) => area == DeployArea.Midfield ? 5 : 7;

        private Vector2Int FindDeployCell(Side side)
        {
            // 玩家部署区 = 最下 2 行（y 0~1）；敌方 = 最上 2 行（y 6~7）
            // ⚠️ 2026-08-22 能力 DeployRow：己方部署区 +N 行（敌方不变）
            int minY = side == Side.Player ? 0 : 6;
            int rows = 2;
            if (side == Side.Player)
            {
                rows += _state.DeployRowBonus;
            }
            for (int y = minY; y < minY + rows; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    var cell = new Vector2Int(x, y);
                    // ⚠️ 2026-08-13：+障碍物检查（原只查占用——部署区配障碍物时棋子会部署到障碍物格上；
                    // 与移动落点 IsCellPassable 三条件对称：界内+非占用+非障碍）
                    if (!_state.Pieces.ContainsKey(cell) && !_state.Obstacles.Contains(cell))
                    {
                        return cell;
                    }
                }
            }
            return new Vector2Int(-1, -1); // 无空位
        }

        /// <summary>波次部署替代格（2026-08-24 策划新语义）：固定站位被占用 → 敌方部署区选别的空格；部署区满 → 部署区外一排找，以此类推（从敌方侧向棋盘下方逐排）。</summary>
        private Vector2Int FindAlternateDeployCell(Side side)
        {
            if (side != Side.Enemy)
            {
                return FindDeployCell(side); // 玩家侧维持原逻辑（无此需求）
            }
            for (int y = 7; y >= 0; y--) // 敌方部署区（y6-7）优先 → 外扩（y5→0）逐排
            {
                for (int x = 0; x < 8; x++)
                {
                    var cell = new Vector2Int(x, y);
                    if (!_state.Pieces.ContainsKey(cell) && !_state.Obstacles.Contains(cell))
                    {
                        return cell;
                    }
                }
            }
            return new Vector2Int(-1, -1); // 全棋盘无空位
        }

        /// <summary>玩家部署是否允许：阶段限定种类（Placement=初始 / PlayerTurn=部署）+ 手牌持有（防重复部署）。
        /// ⚠️ 2026-08-15：种类 = 价值档位推导（初始 0-3 / 部署 4-6——编辑跨档后判定随之变化）。</summary>
        private bool IsDeployAllowed(PieceDef def, BattlePhase phase)
        {
            if (phase == BattlePhase.Placement && _state.GetEffectiveType(def.Id) != PieceType.Initial)
            {
                return false; // 摆放阶段只能放初始棋子
            }
            if (phase == BattlePhase.PlayerTurn && _state.GetEffectiveType(def.Id) != PieceType.Deployable)
            {
                // ⚠️ 2026-08-26 语义扩展（用户定案）：初始棋子战斗中可部署（延续起始摆位——属性/代币/事件等
                // 途径获得的初始牌不再卡手；原仅代币玩法 C5 特例，现推广到全部来源）
                if (_state.GetEffectiveType(def.Id) != PieceType.Initial)
                {
                    return false; // 部署阶段只能放部署棋子（升变棋子靠升变操作上场）
                }
            }
            return HasPieceInHand(def.Id); // 手牌持有（防重复部署）
        }

        /// <summary>手牌是否持有该棋子的牌（2026-08-20 牌结构：仅棋子牌——麻将牌不算持有棋子）。</summary>
        private bool HasPieceInHand(int defId)
        {
            foreach (var card in _state.Hand)
            {
                if (card.IsPiece && card.defId == defId) return true;
            }
            return false;
        }

        /// <summary>手牌是否有该点数的麻将牌（2026-08-20 麻将：打出/摸切用）。</summary>
        private bool HasMahjongInHand(int value)
        {
            foreach (var card in _state.Hand)
            {
                if (card.IsMahjong && card.value == value) return true;
            }
            return false;
        }

        /// <summary>
        /// 手牌是否存在雀头（2026-08-20 麻将和牌条件：**任意两牌价值相同**——不限麻将牌：
        /// 麻将牌价值 = 点数；棋子牌价值 = 棋子价值（GetEffectiveValue）。
        /// </summary>
        private bool HasHuHeadInHand()
        {
            for (int i = 0; i < _state.Hand.Count; i++)
            {
                for (int j = i + 1; j < _state.Hand.Count; j++)
                {
                    if (CardValue(_state.Hand[i]) == CardValue(_state.Hand[j]))
                    {
                        return true; // 两张价值相同 → 雀头
                    }
                }
            }
            return false;
        }

        /// <summary>牌的价值（雀头判定用）：麻将牌 = 点数；棋子牌 = 棋子价值（生效程序推导）。</summary>
        private int CardValue(Card card)
        {
            if (card.IsMahjong) return card.value;
            if (card.IsPiece) return _state.GetEffectiveValue(card.defId);
            return 0;
        }

        /// <summary>围棋防堵路检测（2026-08-24 设计定稿）：该敌方棋子移动候选被**红围棋**（敌方围棋棋子）占据的格子——清障攻击目标；无则 null。
        /// 触发：无玩家目标可打时，打掉堵路的红围棋（防玩家用红围棋封死敌方行动路径）；玩家目标优先。</summary>
        private Vector2Int? FindBlockedByGo(PieceInstance piece)
        {
            if (!_state.IsStyleActive(StyleRegistry.Go)) return null;
            var program = piece.GetProgram(_state);
            if (program == null) return null;
            foreach (var slot in program)
            {
                if (slot is MoveTemplate move)
                {
                    var options = _intentResolver.GetMoveOptions(_state, piece, move);
                    foreach (var cell in options)
                    {
                        var other = _state.GetPieceAt(cell);
                        if (other != null && other.IsGo && other.side == Side.Enemy)
                        {
                            return cell; // 本可走的格子被红围棋堵住 → 清障目标
                        }
                    }
                }
            }
            return null;
        }

        private bool IsValidDeployCell(Side side, Vector2Int cell)
        {
            if (cell.x < 0 || cell.x >= 8)
            {
                return false;
            }
            if (side == Side.Player && (cell.y < 0 || cell.y > 1 + _state.DeployRowBonus)) // 2026-08-22 能力 DeployRow：己方部署区 +N 行
            {
                return false;
            }
            if (side == Side.Enemy && (cell.y < 6 || cell.y > 7))
            {
                return false;
            }
            // ⚠️ 2026-08-13：+障碍物检查（与 FindDeployCell/IsCellPassable 三条件对称——部署区配障碍物时不可部署到障碍物格）
            return !_state.Pieces.ContainsKey(cell) && !_state.Obstacles.Contains(cell);
        }

        // ========== 胜负（非对称：玩家判负 + 关卡 victoryRule）==========

        /// <summary>
        /// 常规关卡的回合结算入口。WipeOut 为纯战斗关，不产生分数。
        /// 仅在敌方回合收尾或终局兜底调用，避免玩家每次请求后清空 BaseScore。
        /// </summary>
        private void SettleTurnScore()
        {
            if (_floor != null && _floor.victoryRule != VictoryRule.WipeOut)
            {
                _resolver.SettleScore(_state.WaveScores.Count - 1);
            }
        }

        public void CheckVictory(bool force)
        {
            // 防御：GameOver 后不再判定（收尾链后 Reset 在栈外执行——Phase 防御不会被破坏）
            if (_state.Phase == BattlePhase.GameOver)
            {
                return;
            }
            // ⚠️ 不在每次请求后结算；主结算在 EndEnemyTurn，终局分支在 EndBattle 前兜底。
            // 玩家失败：终局兜底结算，防本回合已累计基础分在失败结算面板中丢失。
            if (_state.IsPlayerDefeated())
            {
                SettleTurnScore();
                EndBattle(Side.Enemy);
                return;
            }

            // 敌方全灭且所有波次均已部署时，本场已到终局；先结算最后击杀所得，
            // 再按含分数的胜利规则判断。force 同样是末波倒计时的终局兜底。
            bool terminalWipe = AllWavesDeployed() && _boardRules.IsEnemyWiped(_state);
            if (force || terminalWipe)
            {
                SettleTurnScore();
            }

            // 玩家胜利（按关卡规则）
            bool playerWins = false;
            switch (_floor.victoryRule)
            {
                case VictoryRule.WipeOut:
                    playerWins = AllWavesDeployed() && _boardRules.IsEnemyWiped(_state);
                    break;
                case VictoryRule.ScoreTarget:
                    playerWins = (AllWavesDeployed() && _boardRules.IsEnemyWiped(_state)) || _boardRules.IsScoreTargetReached(_state, _floor);
                    break;
                case VictoryRule.Both:
                    playerWins = AllWavesDeployed() && _boardRules.IsEnemyWiped(_state) && _boardRules.IsScoreTargetReached(_state, _floor);
                    break;
                case VictoryRule.PerWaveScore:
                    playerWins = _waveEnded && _boardRules.IsEnemyWiped(_state) &&
                                 (_boardRules.IsScoreTargetReached(_state, _floor) || AllWavesScored());
                    break;
            }
            if (playerWins)
            {
                EndBattle(Side.Player);
                return;
            }
            if (force)
            {
                EndBattle(Side.Enemy); // 末波强制结算：胜利条件未达成 → 失败
            }
        }

        /// <summary>全部波次已部署（未出完波前的空棋盘不算全灭胜利——防开局误判）。</summary>
        private bool AllWavesDeployed()
        {
            return _deployedWaveIndex >= _floor.waveDefs.Count;
        }

        /// <summary>
        /// 第 3 关"每波得分均达标"（2026-08-19：按 WaveDef.waveScoreTarget 判断——0 = 未配置，旧骨架每波 &gt; 0；
        /// 达标线数值待策划回填）。
        /// </summary>
        private bool AllWavesScored()
        {
            for (int i = 0; i < _state.WaveScores.Count; i++)
            {
                int target = i < _floor.waveDefs.Count ? _floor.waveDefs[i].waveScoreTarget : 0;
                if (target > 0)
                {
                    if (_state.WaveScores[i] < target) return false;
                }
                else if (_state.WaveScores[i] <= 0)
                {
                    return false; // 未配置达标线 → 旧骨架（每波 > 0 视为达标）
                }
            }
            return true;
        }

        public void EndBattle(Side winner)
        {
            // 幂等：已 GameOver 不再发（收尾链后 Reset 栈外执行——防御不会被破坏）
            if (_state.Phase == BattlePhase.GameOver)
            {
                return;
            }
            // ⚠️ 计分终局兜底（2026-08-20）：战斗结束前补一次结算——玩家回合内全灭/判负/末波强制等
            // 非"EndEnemyTurn 正常收尾"路径——防战斗结束丢最后未结算的基础分；WipeOut（第 1 关）不结算；幂等
            if (_floor.victoryRule != VictoryRule.WipeOut)
            {
                _resolver.SettleScore(_state.WaveScores.Count - 1);
            }
            ChangePhase(BattlePhase.GameOver);
            _waitingPresentation = false; // 2026-08-21：终局停止表现等待（防超时协程 GameOver 后误报/滞留）
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, winner);
        }
    }

    /// <summary>行动点变化事件数据。</summary>
    public class ApInfo
    {
        public Side Side;
        public int Current;
        public int Max;
    }

    /// <summary>
    /// 表现回执数据（2026-08-21——前端播完表现后带回 token：sessionId + actionId——
    /// 从 BattleFlow.CurrentPresentationToken 读取；后端校验匹配才推进（迟到/重复忽略）。
    /// 旧前端可不带（null/0——宽松兼容过渡期）。
    /// </summary>
    [System.Serializable] // 全限定——BattleFlow 无 using System（避免头部引入潜在符号冲突）
    public class PresentationInfo
    {
        public int SessionId;
        public int ActionId;
    }
}
