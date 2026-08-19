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
        // _battleEnded 已删除（2026-08-11 收尾链）：Reset 移出事件回调栈后
        // Phase==GameOver 防御不会被破坏——补丁退休
        private bool _enemyTurnEndPending;   // 敌方回合结束待定——本阶段表现全部播完才切回玩家回合（动画优先）
        private bool _hadEnemyPresentation;  // 本轮敌方回合是否有表现（有→表现完即切；无→等阶段展示信号）
        private bool _deployedThisRound;     // 本轮波次是否部署（部署动画挂起点）
        private bool _pendingAutoPromote;    // 自动预告挂起（2026-08-19：本波第 1 回合结束后预告离中心最近 2 棋子，第 3 回合随机升变）
        private int _enemyBudget; // 敌方回合行动次数预算（逐步决策——每步一个行动；2026-08-13 替代请求队列）
        private readonly HashSet<int> _actedEnemyPieces = new HashSet<int>(); // 本回合已行动的敌方棋子（① 排除——防 requests[0] 固定重复执行）

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
        }

        // ========== 开战 / 阶段 ==========

        public void StartBattle(FloorConfig floor, AIParams aiParams)
        {
            _state.ResetForBattle(); // 战斗态重置（2026-08-13：跨战斗残留——TurnCount/棋盘/波次分每场战斗重来）
            ResetState(); // 新局统一清瞬态执行状态（防跨局残留——后端待办 #5：多次重开卡死根因）
            _floor = floor;
            _aiParams = aiParams;
            _floorRules = FloorRulesFactory.Create(floor.Id);
            _deployedWaveIndex = 0;
            _waveEnded = false;
            _state.EnemyAPMax = floor.enemyMaxAP;
            _state.WaveEndCountdown = -1;
            _floorRules.OnBattleStart(_state, _resolver);
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
            _pendingAutoPromote = false; // 新字段必须进重置清单——防跨局残留（预告挂起未消费）
            _enemyBudget = 0; // 敌方行动预算（新字段必须进重置清单——防跨局残留）
            _actedEnemyPieces.Clear(); // 已行动棋子集合（新字段必须进重置清单——防跨局残留）
        }

        private void OnPlacementFinished(object data)
        {
            if (_state.Phase != BattlePhase.Placement)
            {
                return;
            }
            // 前置条件：手牌中不得还有初始棋子——必须摆完全部起始棋子才能结束摆放（防"跳过摆放"）
            // ⚠️ 2026-08-15：类型 = 价值档位推导（初始 = 0-3 档；编辑跨档后种类随价值变化）
            foreach (var defId in _state.Hand)
            {
                if (_state.GetEffectiveType(defId) == PieceType.Initial)
                {
                    EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "placement-incomplete"); // 通知 UI 继续摆放
                    return;
                }
            }
            StartPlayerTurn();
        }

        public void StartPlayerTurn()
        {
            _state.PlayerAP = _state.PlayerAPMax;
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
            _state.PlayerAP = 0; // 回合末清零
            _floorRules.OnTurnEnd(_state, _resolver);
            StartEnemyTurn();
        }

        private void StartEnemyTurn()
        {
            _state.EnemyAP = _state.EnemyAPMax;
            ChangePhase(BattlePhase.EnemyTurn);
            HandleWaveAndPromotions(); // 波次调度 + 升变预告（本波开始预告下一波）
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
            _state.TurnCount++;
            CheckVictory(false);
            if (_state.Phase != BattlePhase.GameOver)
            {
                // 自动预告（2026-08-19）：本波第 1 回合结束后（敌方执行完行动）——离中心最近 2 个敌方棋子获升变预告
                if (_pendingAutoPromote)
                {
                    _pendingAutoPromote = false;
                    AnnounceAutoPromotions();
                }
                // 动画优先：敌方回合展示到本阶段表现全部播完（含波次部署/AI 行动动画）再切回玩家回合
                _enemyTurnEndPending = true;
                // ⚠️ 2026-08-12：_hadEnemyPresentation 不在此采样（串行化后采样时表现已完成、_waitingPresentation 已清，
                // 且 PhaseDisplayed 在回合开始 1 帧后发出早已被丢弃——无波次回合两条收尾路径均不满足软锁）
                // 改为 WaitPresentation 表现发生时锁存（有表现即标记，不依赖采样时机）
                TryEndEnemyTurn();
            }
        }

        /// <summary>
        /// 自动预告（2026-08-19）：敌方场上离棋盘中心 (3.5,3.5) 最近的两个棋子获升变预告——
        /// countdown=2（第 2 回合递减→1、第 3 回合递减→0 升变）；newDefId=0 表示升变时从升变类棋子随机（RandomManager）。
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
                    countdown = 2,  // 本波第 3 回合开始升变
                };
                _state.PromoteAnnouncements.Add(ann);
                EventCenter.Instance.EventTrigger(GameEvent.PromoteAnnounced, ann);
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
                            _resolver.Resolve(new DeployAction(deploy.pieceDefId, side, deploy.cell));
                            DeductActionPoint(request.free, side);
                        }
                        break;
                    case PromoteRequest promote:
                        var piece = _state.GetPiece(promote.pieceId);
                        var promoteDef = ConfigTable.Find<PieceDef>(promote.newDefId);
                        // 升变规则（放宽）：任意【非升变】棋子 + 手牌有【升变牌】→ 可升变（无映射限制）
                        // ⚠️ 2026-08-15：类型 = 价值档位推导（升变 = 7+ 档；编辑跨档后判定随之变化）
                        bool promoteValid = piece != null && piece.side == side
                            && _state.GetEffectiveType(piece.DefId) != PieceType.Promoted
                            && promoteDef != null && _state.GetEffectiveType(promoteDef.Id) == PieceType.Promoted
                            && _state.Hand.Contains(promote.newDefId);
                        if (promoteValid)
                        {
                            _resolver.Resolve(new PromoteAction(promote.pieceId, promote.newDefId));
                            DeductActionPoint(request.free, side);
                        }
                        break;
                    case ExecuteRequest execute:
                        if (_state.GetPiece(execute.pieceId)?.side == side)
                        {
                            // 免费执行资格（额外行动——方案 B）：有资格 → 本次免费 + 资格用掉（保留到使用为止，有效期待策划拍板）
                            bool free = request.free || _state.FreeExecutes.Contains(execute.pieceId);
                            if (free && _state.FreeExecutes.Remove(execute.pieceId))
                            {
                                EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, execute.pieceId);
                            }
                            ExecutePiece(execute.pieceId, free, side); // 玩家逐槽选择 / AI 自动选（内部按 side 分流）
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
                OnPlayerEndTurn(); // 行动点用完 → 轮到对方
            }
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
            }
            else
            {
                _state.EnemyAP--;
            }
            EventCenter.Instance.EventTrigger(GameEvent.ActionPointChanged, new ApInfo { Side = side, Current = side == Side.Player ? _state.PlayerAP : _state.EnemyAP, Max = side == Side.Player ? _state.PlayerAPMax : _state.EnemyAPMax });
        }

        // ========== 逐槽可续执行 ==========

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
                            _resolver.Resolve(new SkipAction(piece.Id, SkipReason.NoTarget));
                            _ctx.slotIndex++;
                            AdvanceSlot();
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
            // 执行扣费是异步的（表现完成后），此处补触发 AP 耗尽检查（回合自动移交）
            if (side == Side.Player)
            {
                CheckActionPoints();
            }
            // 敌方逐步决策（2026-08-13）：当前行动完整执行完（扣费后）→ 基于最新状态决策下一步；预算 0/无行动 → 收尾
            if (side == Side.Enemy)
            {
                TryNextEnemyDecision();
            }
        }

        // ========== 表现等待（等"表现完成"事件——无限等 + 日志）==========

        private void WaitPresentation()
        {
            _waitingPresentation = true;
            // 敌方回合表现发生时即锁存"本回合有表现"（2026-08-12：供 EndEnemyTurn 判定动画路径——
            // 串行化后收尾采样时机已过（表现完成、PhaseDisplayed 丢弃），锁存不依赖采样时机；玩家回合不受影响）
            if (_state.Phase == BattlePhase.EnemyTurn)
            {
                _hadEnemyPresentation = true;
            }
            // 等待日志（卡住时定位：谁在等、等哪个表现组）
            Debug.Log($"[BattleFlow] 表现等待开始 piece={_ctx?.pieceId} slot={_ctx?.slotIndex}（等'表现完成'事件）");
        }

        private void OnPresentationFinished(object data)
        {
            if (_waitingPresentation)
            {
                _waitingPresentation = false;
                if (_ctx != null)
                {
                    AdvanceSlot();
                }
            }
            TryEndEnemyTurn(); // 动画播完（一轮表现队列排干）——检查敌方回合能否收尾
        }

        // ========== 波次调度 + 敌方升变预告 ==========

        private void HandleWaveAndPromotions()
        {
            // 升变预告倒计时（波次 N 开始预告波次 N+1）。
            // 【语义确认 2026-08-13】按"每次 HandleWaveAndPromotions（每敌方回合）"递减——countdown=1 的预告
            // 在下一敌方回合即升变（比"下波部署时升变"提前数回合）。该语义已确认保持现状（按实现），暂不改。
            foreach (var ann in new List<PromoteAnnouncement>(_state.PromoteAnnouncements))
            {
                ann.countdown--;
                if (ann.countdown <= 0)
                {
                    _state.PromoteAnnouncements.Remove(ann);
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
                // 阵容：固定列表 或 随机池（2026-08-19——从初始/部署类棋子随机抽 count 个，可重复；RandomManager 可复现）
                var defIds = wave.pieceDefIds;
                if (wave.randomPool)
                {
                    var candidates = new List<int>();
                    foreach (var def in ConfigTable.All<PieceDef>())
                    {
                        if (_state.GetEffectiveType(def.Id) == wave.poolType)
                        {
                            candidates.Add(def.Id);
                        }
                    }
                    if (candidates.Count == 0)
                    {
                        Debug.LogWarning($"[BattleFlow] 波次随机池为空（{wave.poolType}）——本波不部署");
                        defIds = new List<int>();
                    }
                    else
                    {
                        defIds = new List<int>();
                        for (int i = 0; i < Mathf.Max(0, wave.count); i++)
                        {
                            defIds.Add(candidates[RandomManager.Instance.Range(0, candidates.Count)]);
                        }
                    }
                }
                int slot = 0;
                foreach (var defId in defIds)
                {
                    // 固定站位（与阵容顺序对应）；空 = 部署区自动找位；固定位被占用（前一波残留）→ 跳过该棋子
                    var cell = wave.positions.Count > 0
                        ? (slot < wave.positions.Count ? wave.positions[slot] : new Vector2Int(-1, -1))
                        : FindDeployCell(Side.Enemy);
                    slot++;
                    if (cell.x < 0)
                    {
                        break; // 固定站位耗尽 / 部署区无空位
                    }
                    if (wave.positions.Count > 0 && (_state.Pieces.ContainsKey(cell) || _state.Obstacles.Contains(cell)))
                    {
                        continue; // 固定站位被占用——跳过（前一波棋子未清理）
                    }
                    var deployAction = new DeployAction(defId, Side.Enemy, cell) { waveIndex = _deployedWaveIndex }; // 打波次标（每波得分）
                    _resolver.Resolve(deployAction);
                    deployedThisWave.Add(_state.GetPieceAt(cell));
                    anyDeployed = true;
                }
                if (!anyDeployed)
                {
                    // ⚠️ 2026-08-13：部署区满零落地——原实现仍推进波次索引（该波永久丢失）+
                    // 置 _deployedThisRound 等待表现（无 PieceDeployed 事件 → AI 无表现时软锁）。
                    // 改为：本波不推进、不等待（下回合波次判定仍成立会重试）。
                    break;
                }
                _deployedThisRound = true;
                // 自动预告模式（2026-08-19）：本波第 1 回合结束后预告离中心最近 2 个敌方棋子（EndEnemyTurn 触发）——此处仅挂起
                if (wave.autoPromote)
                {
                    _pendingAutoPromote = true;
                }
                // 本波开始 → 预告下一波升变（配置的升变棋子——旧机制；autoPromote 自动预告模式时互斥跳过）
                if (!wave.autoPromote)
                {
                    foreach (var promo in wave.promotions)
                    {
                        if (promo.pieceIndexInWave >= 0 && promo.pieceIndexInWave < deployedThisWave.Count)
                        {
                            var target = deployedThisWave[promo.pieceIndexInWave];
                            if (target != null)
                            {
                                _state.PromoteAnnouncements.Add(new PromoteAnnouncement
                                {
                                    pieceId = target.Id,
                                    newDefId = promo.toDefId,
                                    countdown = 1, // 下一波次升变
                                });
                                EventCenter.Instance.EventTrigger(GameEvent.PromoteAnnounced, new PromoteAnnouncement { pieceId = target.Id, newDefId = promo.toDefId, countdown = 1 });
                            }
                        }
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

        private Vector2Int FindDeployCell(Side side)
        {
            // 玩家部署区 = 最下 2 行（y 0~1）；敌方 = 最上 2 行（y 6~7）
            int minY = side == Side.Player ? 0 : 6;
            for (int y = minY; y < minY + 2; y++)
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
                return false; // 部署阶段只能放部署棋子（升变棋子靠升变操作上场）
            }
            return _state.Hand.Contains(def.Id); // 手牌持有（防重复部署）
        }

        private bool IsValidDeployCell(Side side, Vector2Int cell)
        {
            if (cell.x < 0 || cell.x >= 8)
            {
                return false;
            }
            if (side == Side.Player && (cell.y < 0 || cell.y > 1))
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

        public void CheckVictory(bool force)
        {
            // 防御：GameOver 后不再判定（收尾链后 Reset 在栈外执行——Phase 防御不会被破坏）
            if (_state.Phase == BattlePhase.GameOver)
            {
                return;
            }
            // 玩家失败（无棋且无手牌——仅玩家侧）
            if (_state.IsPlayerDefeated())
            {
                EndBattle(Side.Enemy);
                return;
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

        /// <summary>第 3 关"每波得分均达标"（骨架：每波得分 &gt; 0 视为达标——达标线数值待策划回填）。</summary>
        private bool AllWavesScored()
        {
            for (int i = 0; i < _state.WaveScores.Count; i++)
            {
                if (_state.WaveScores[i] <= 0)
                {
                    return false;
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
            ChangePhase(BattlePhase.GameOver);
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
}
