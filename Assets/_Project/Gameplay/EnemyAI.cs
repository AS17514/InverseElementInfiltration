using System.Collections.Generic;
using TheLaw.Data;
using UnityEngine;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 敌方 AI：短视吃子至上算法。按 AP 预算产出请求（执行/部署），走统一管线。
    /// 攻击目标选择在执行时由 BattleFlow 用 IntentResolver.PickTarget 完成（AI 自动选）。
    /// 波次部署不走 AI（BattleFlow 波次调度直接产出 DeployRequest）。
    /// </summary>
    public class EnemyAI
    {
        private readonly IntentResolver _intentResolver;
        private readonly AIParams _aiParams;

        public EnemyAI(IntentResolver intentResolver, AIParams aiParams)
        {
            _intentResolver = intentResolver;
            _aiParams = aiParams;
        }

        /// <summary>
        /// 按 AP 预算产出请求：每个棋子至多 1 个请求（2026-08-12 修复——原实现按"槽"产请求：
        /// 同棋子多攻击槽 → 多请求 → 每请求执行完整程序 = 行动放大；且单棋子可耗尽全部预算。
        /// 现语义与玩家一致：能攻击（任一攻击槽有目标）→ 执行一次棋子（完整程序内自动攻击）；
        /// 否则能移动（任一移动槽有候选）→ 执行一次（完整程序内自动移动；攻击槽无目标自动 Skip）。
        /// ⚠️ 2026-08-13 两项修复：
        ///   ① actedPieces 排除——逐步决策下每步调用，跳过本回合已行动的棋子（防 requests[0] 固定重复执行）
        ///   ② 攻击评估改"移动后位置"——决策与执行的攻击评估基准一致（执行时移动槽先移动、攻击槽按
        ///      移动后位置重算——决策也用移动后位置评估，消除"棋子内漂移"导致的空放/打己方）
        /// </summary>
        public List<Request> DecideTurn(GameState state, HashSet<int> actedPieces)
        {
            var requests = new List<Request>();
            int budget = state.EnemyAPMax;

            foreach (var piece in state.Pieces.Values)
            {
                if (budget <= 0)
                {
                    break;
                }
                if (piece.side != Side.Enemy)
                {
                    continue;
                }
                if (actedPieces != null && actedPieces.Contains(piece.Id))
                {
                    continue; // ① 已行动棋子——本回合不再选中
                }

                var program = piece.GetProgram(state);
                if (program == null || program.Count == 0)
                {
                    continue;
                }
                // ② 预测移动后位置（与执行时移动槽的 AI 选择一致——无移动槽/无候选 = 当前位置）
                var moveTarget = PredictMoveTarget(state, piece, program);
                // 决策模拟：临时移到"移动后位置"评估攻击（决策阶段单线程安全——执行前还原）
                var origPos = piece.position;
                piece.position = moveTarget;
                bool canAttack = CanAttackNow(state, piece, program);
                piece.position = origPos;
                bool canMove = moveTarget != origPos;
                // 2026-08-23 诊断（第二梯队）：AI 决策过程（移动预测/攻击评估——统一走 GameState.LogDiagnostic，开关判定在内部）
                state.LogDiagnostic($"AI决策: 棋子 id={piece.Id} def={piece.DefId} 移动预测=({moveTarget.x},{moveTarget.y}) canAttack={canAttack} canMove={canMove} AP预算={budget}");
                if (canAttack || canMove)
                {
                    // 免费资格消费（2026-08-12）：该棋子有免费资格 → 本次执行免费（不扣 AP）
                    // ⚠️ 只有"确有资格"才设 free=true——否则 ProcessRequest 的 free 分支会白嫖（无资格也免费）
                    bool free = state.FreeExecutes.Contains(piece.Id);
                    requests.Add(new ExecuteRequest(piece.Id) { free = free });
                    budget--; // 免费也占一次行动次数预算（enemyAPMax=行动次数上限）
                }
            }
            return requests;
        }

        /// <summary>
        /// ② 预测执行时移动槽的落点（与 BattleFlow 执行逻辑一致：PickClosestToEnemy 选最近玩家的落点）。
        /// 程序无移动槽/全部无候选 → 返回当前位置（移动不会发生——攻击按当前位置评估）。
        /// </summary>
        private Vector2Int PredictMoveTarget(GameState state, PieceInstance piece, List<Template> program)
        {
            foreach (var slot in program)
            {
                if (slot is MoveTemplate move)
                {
                    var options = _intentResolver.GetMoveOptions(state, piece, move);
                    if (options.Count > 0)
                    {
                        return _intentResolver.PickClosestToEnemy(state, piece, options);
                    }
                }
            }
            return piece.position;
        }

        private bool HasEnemyTarget(GameState state, List<Vector2Int> cells)
        {
            foreach (var cell in cells)
            {
                var target = state.GetPieceAt(cell);
                if (target != null && target.side == Side.Player)
                {
                    return true;
                }
            }
            return false;
        }

        private bool CanAttackNow(GameState state, PieceInstance piece, List<Template> program)
        {
            foreach (var slot in program)
            {
                if (slot is AttackTemplate attack)
                {
                    if (HasEnemyTarget(state, _intentResolver.GetAttackOptions(state, piece, attack)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool CanMoveNow(GameState state, PieceInstance piece, List<Template> program)
        {
            foreach (var slot in program)
            {
                if (slot is MoveTemplate move)
                {
                    if (_intentResolver.GetMoveOptions(state, piece, move).Count > 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
