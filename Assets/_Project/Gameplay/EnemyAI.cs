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

        /// <summary>按 AP 预算产出请求：优先吃子（攻击范围内价值最高目标），否则靠近敌人。</summary>
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
                if (program == null)
                {
                    continue;
                }
                foreach (var slot in program)
                {
                    if (budget <= 0)
                    {
                        break;
                    }
                    if (slot is AttackTemplate attackTemplate)
                    {
                        var options = _intentResolver.GetAttackOptions(state, piece, attackTemplate);
                        if (HasEnemyTarget(state, options))
                        {
                            requests.Add(new ExecuteRequest(piece.Id));
                            budget--;
                        }
                    }
                    else if (slot is MoveTemplate moveTemplate && !CanAttackNow(state, piece, program))
                    {
                        var options = _intentResolver.GetMoveOptions(state, piece, moveTemplate);
                        if (options.Count > 0)
                        {
                            requests.Add(new ExecuteRequest(piece.Id));
                            budget--;
                        }
                    }
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
    }
}
