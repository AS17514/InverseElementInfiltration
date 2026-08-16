using System.Collections.Generic;
using TheLaw.Data;
using UnityEngine;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 意图解析器：模板 → 行动（用棋盘规则判定）。
    /// 逐槽可续执行：BattleFlow 逐个槽位调用 GetMoveOptions/GetAttackOptions 让玩家选，
    /// 选定后 ResolveMove/ResolveAttack 生成行动 → Resolver 落账（翻译-暂停-落账-续译）。
    /// </summary>
    public class IntentResolver
    {
        private readonly BoardRules _boardRules;

        public IntentResolver(BoardRules boardRules)
        {
            _boardRules = boardRules;
        }

        // ========== 选项（供玩家选择/AI 决策）==========

        /// <summary>移动落点候选（已含被动修正）。</summary>
        public List<Vector2Int> GetMoveOptions(GameState state, PieceInstance piece, MoveTemplate template)
        {
            return _boardRules.GetLegalMoves(state, piece, template);
        }

        /// <summary>攻击目标候选（任意格子——可空放/打己方）。</summary>
        public List<Vector2Int> GetAttackOptions(GameState state, PieceInstance piece, AttackTemplate template)
        {
            return _boardRules.GetAttackableCells(state, piece, template);
        }

        // ========== 生成行动（选定后）==========

        public MoveAction ResolveMove(PieceInstance piece, Vector2Int to)
        {
            return new MoveAction(piece.Id, piece.position, to);
        }

        public AttackAction ResolveAttack(GameState state, PieceInstance piece, Vector2Int targetCell, AttackTemplate template)
        {
            return new AttackAction(piece.Id, targetCell, template);
        }

        // ========== 目标选择（AI 用；玩家目标手动指定）==========

        /// <summary>按规则从候选中选目标（Nearest/LowestHP/HighestValue）。</summary>
        public Vector2Int PickTarget(GameState state, PieceInstance piece, List<Vector2Int> options, TargetRule rule)
        {
            AssertTargetOptions(options);
            if (options.Count == 0)
            {
                return piece.position; // 无候选：返回自身（调用方应处理为 Skip）
            }
            Vector2Int best = options[0];
            int bestScore = int.MinValue;
            foreach (var cell in options)
            {
                int score = ScoreCell(state, cell, rule, piece);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = cell;
                }
            }
            return best;
        }

        /// <summary>最近的敌方棋子（短视吃子的移动目标）。</summary>
        public Vector2Int PickClosestToEnemy(GameState state, PieceInstance piece, List<Vector2Int> options)
        {
            AssertTargetOptions(options);
            Vector2Int best = options[0];
            int bestDist = int.MaxValue;
            foreach (var cell in options)
            {
                foreach (var other in state.Pieces.Values)
                {
                    if (other.side == piece.side)
                    {
                        continue;
                    }
                    int dist = Mathf.Abs(cell.x - other.position.x) + Mathf.Abs(cell.y - other.position.y);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = cell;
                    }
                }
            }
            return best;
        }

        private void AssertTargetOptions(List<Vector2Int> options)
        {
            Core.Assert.IsNotNull(options, "PickTarget: options 为 null");
        }

        private int ScoreCell(GameState state, Vector2Int cell, TargetRule rule, PieceInstance attacker)
        {
            var target = state.GetPieceAt(cell);
            switch (rule)
            {
                case TargetRule.HighestValue:
                    // ⚠️ 2026-08-15：价值走推导（生效程序槽位总和——编辑后目标价值随之变化）
                    return target != null ? PieceValue.SumValue(target.GetProgram(state)) : 0;
                case TargetRule.LowestHP:
                    return target != null ? -target.durability : 0;
                case TargetRule.Nearest:
                    // ⚠️ 2026-08-12：原实现返回 0（全部候选同分→永远选第一个）——补距离评分：
                    // 候选格离攻击者越近分越高（曼哈顿距离取负——与 LowestHP 同方向，bestScore 取最大）
                    return attacker != null
                        ? -(Mathf.Abs(cell.x - attacker.position.x) + Mathf.Abs(cell.y - attacker.position.y))
                        : 0;
                default:
                    return 0; // 未知规则兜底（防枚举扩展漏分支）
            }
        }
    }
}
