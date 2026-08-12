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
        /// </summary>
        public List<Request> DecideTurn(GameState state)
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

                var program = piece.GetProgram(state);
                if (program == null || program.Count == 0)
                {
                    continue;
                }
                bool canAttack = CanAttackNow(state, piece, program);
                bool canMove = false;
                if (!canAttack)
                {
                    canMove = CanMoveNow(state, piece, program);
                }
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
