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
        private readonly Queue<Request> _enemyRequests = new Queue<Request>(); // 敌方回合请求队列（串行处理——2026-08-12：防 _ctx 覆盖）

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

        // ========== 开战 / 阶段 ==========

        public void StartBattle(FloorConfig floor, AIParams aiParams)
        {
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
            _enemyRequests.Clear(); // 敌方请求队列（新字段必须进重置清单——防跨局残留）
        }

        private void OnPlacementFinished(object data)
        {
            if (_state.Phase != BattlePhase.Placement)
            {
                return;
            }
            // 前置条件：手牌中不得还有初始棋子——必须摆完全部起始棋子才能结束摆放（防"跳过摆放"）
            foreach (var defId in _state.Hand)
            {
                var def = ConfigTable.Find<PieceDef>(defId);
                if (def != null && def.pieceType == PieceType.Initial)
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
        /// 敌方回合：AI 请求入队后逐个串行处理（2026-08-12 修复——原循环直接 ProcessRequest：
        /// 每个请求第一槽落账后 WaitPresentation 挂起返回，循环继续会覆盖 _ctx——
        /// 前序请求剩余槽位丢失且不扣 AP。串行化：当前请求完整执行完（FinishExecute）才处理下一个）。
        /// </summary>
        private void ResolveEnemyTurn()
        {
            _enemyRequests.Clear();
            foreach (var request in _enemyAI.DecideTurn(_state))
            {
                _enemyRequests.Enqueue(request);
            }
            TryProcessNextEnemyRequest();
        }

        /// <summary>
        /// 串行处理下一个敌方请求（FinishExecute 后调用——side==Enemy 时）。
        /// 队列空 → 敌方回合收尾（原 ResolveEnemyTurn 后半段）；战斗已结束 → 丢弃剩余请求。
        /// </summary>
        private void TryProcessNextEnemyRequest()
        {
            if (_state.Phase == BattlePhase.GameOver)
            {
                _enemyRequests.Clear(); // 战斗已结束——不再处理剩余请求（防御：防失败后误判胜利）
                return;
            }
            if (_enemyRequests.Count == 0)
            {
                EndEnemyTurn();
                return;
            }
            ProcessRequest(_enemyRequests.Dequeue(), Side.Enemy);
        }

        /// <summary>敌方回合收尾（全部请求处理完后）：AP 清零/回合计数/胜负判定/回合切换挂起（动画优先）。</summary>
        private void EndEnemyTurn()
        {
            _state.EnemyAP = 0;
            _state.TurnCount++;
            CheckVictory(false);
            if (_state.Phase != BattlePhase.GameOver)
            {
                // 动画优先：敌方回合展示到本阶段表现全部播完（含波次部署/AI 行动动画）再切回玩家回合
                _enemyTurnEndPending = true;
                _hadEnemyPresentation = _waitingPresentation || _deployedThisRound;
                TryEndEnemyTurn();
            }
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
                        bool promoteValid = piece != null && piece.side == side
                            && piece.def.pieceType != PieceType.Promoted
                            && promoteDef != null && promoteDef.pieceType == PieceType.Promoted
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
                        var aiAttackOptions = _intentResolver.GetAttackOptions(_state, piece, attack);
                        if (aiAttackOptions.Count == 0)
                        {
                            _resolver.Resolve(new SkipAction(piece.Id, SkipReason.NoTarget));
                            _ctx.slotIndex++;
                            AdvanceSlot();
                        }
                        else
                        {
                            var aiTarget = _intentResolver.PickTarget(_state, piece, aiAttackOptions, _aiParams.targetRule);
                            _resolver.Resolve(_intentResolver.ResolveAttack(_state, piece, aiTarget, attack));
                            _ctx.slotIndex++;
                            WaitPresentation();
                        }
                    }
                    break;
                case SkipTemplate:
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
                    if (!_boardRules.IsCellPassable(_state, cell))
                    {
                        _waitingCellSelect = true; // 非法落点——重新选
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
            // 敌方请求串行化（2026-08-12）：当前请求完整执行完（扣费后）→ 处理下一个；队列空 → 敌方回合收尾
            if (side == Side.Enemy)
            {
                TryProcessNextEnemyRequest();
            }
        }

        // ========== 表现等待（等"表现完成"事件——无限等 + 日志）==========

        private void WaitPresentation()
        {
            _waitingPresentation = true;
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
            // 升变预告倒计时（波次 N 开始预告波次 N+1；此处波次推进时处理）
            foreach (var ann in new List<PromoteAnnouncement>(_state.PromoteAnnouncements))
            {
                ann.countdown--;
                if (ann.countdown <= 0)
                {
                    _state.PromoteAnnouncements.Remove(ann);
                    var piece = _state.GetPiece(ann.pieceId);
                    if (piece != null && piece.side == Side.Enemy)
                    {
                        _resolver.Resolve(new PromoteAction(ann.pieceId, ann.newDefId));
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
                foreach (var defId in wave.pieceDefIds)
                {
                    var cell = FindDeployCell(Side.Enemy);
                    if (cell.x < 0)
                    {
                        break; // 部署区无空位
                    }
                    var deployAction = new DeployAction(defId, Side.Enemy, cell) { waveIndex = _deployedWaveIndex }; // 打波次标（每波得分）
                    _resolver.Resolve(deployAction);
                    deployedThisWave.Add(_state.GetPieceAt(cell));
                }
                _deployedThisRound = true;
                // 本波开始 → 预告下一波升变（配置的升变棋子）
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
                _state.WaveEndCountdown--;
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
                    if (!_state.Pieces.ContainsKey(cell))
                    {
                        return cell;
                    }
                }
            }
            return new Vector2Int(-1, -1); // 无空位
        }

        /// <summary>玩家部署是否允许：阶段限定种类（Placement=初始 / PlayerTurn=部署）+ 手牌持有（防重复部署）。</summary>
        private bool IsDeployAllowed(PieceDef def, BattlePhase phase)
        {
            if (phase == BattlePhase.Placement && def.pieceType != PieceType.Initial)
            {
                return false; // 摆放阶段只能放初始棋子
            }
            if (phase == BattlePhase.PlayerTurn && def.pieceType != PieceType.Deployable)
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
            return !_state.Pieces.ContainsKey(cell);
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
