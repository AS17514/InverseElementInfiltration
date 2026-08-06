using System.Collections.Generic;
using TheLaw.Data;
using UnityEngine;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 棋盘规则判定（纯逻辑，只读 GameState）。
    /// 判定唯一一份：沿方向集走步数（出界/障碍/占用逐格检查）。
    /// </summary>
    public class BoardRules
    {
        private const int BoardSize = 8;

        // ========== 基础判定 ==========

        public bool IsInsideBoard(Vector2Int cell)
        {
            return cell.x >= 0 && cell.x < BoardSize && cell.y >= 0 && cell.y < BoardSize;
        }

        public bool IsCellOccupied(GameState state, Vector2Int cell)
        {
            return state.Pieces.ContainsKey(cell);
        }

        public bool IsCellPassable(GameState state, Vector2Int cell)
        {
            // 地形未设计（第 1 关无）——暂无障碍；预留扩展点
            return IsInsideBoard(cell) && !IsCellOccupied(state, cell);
        }

        public bool IsPathClear(GameState state, PieceInstance piece, Vector2Int to)
        {
            // 单步移动（maxSteps=1 为主）：目标格可通行即可；多步移动逐格检查由 GetLegalMoves 完成
            return IsCellPassable(state, to);
        }

        // ========== 移动 ==========

        /// <summary>
        /// 计算移动模板的合法落点（解析时应用被动修正：步数 + MoveStep 修正）。
        /// 沿方向集每个方向走 maxSteps 步（出界/障碍/占用逐格检查）。
        /// </summary>
        public List<Vector2Int> GetLegalMoves(GameState state, PieceInstance piece, MoveTemplate template)
        {
            var result = new List<Vector2Int>();
            var pattern = template.pattern;
            int steps = pattern.maxSteps + GetPassiveModifier(state, piece, PassiveTarget.MoveStep);
            for (int dir = 1; dir <= (int)Direction.DownRight; dir <<= 1)
            {
                if ((pattern.directions & (Direction)dir) == 0)
                {
                    continue;
                }
                var dirVec = DirectionToVector((Direction)dir);
                var cursor = piece.position;
                for (int i = 0; i < steps; i++)
                {
                    cursor += dirVec;
                    if (!IsCellPassable(state, cursor))
                    {
                        break; // 出界/障碍/占用——该方向停止
                    }
                    result.Add(cursor);
                }
            }
            return result;
        }

        // ========== 攻击 ==========

        /// <summary>
        /// 计算攻击模板的可攻击格子（任意格子——含己方/空格：可空放/可打己方）。
        /// 范围 = 沿攻击方向 range 格（解析时应用被动修正：射程 + AttackRange 修正）。
        /// </summary>
        public List<Vector2Int> GetAttackableCells(GameState state, PieceInstance piece, AttackTemplate template)
        {
            var result = new List<Vector2Int>();
            int range = template.range + GetPassiveModifier(state, piece, PassiveTarget.AttackRange);
            var dirVec = DirectionToVector(template.direction);
            var cursor = piece.position;
            for (int i = 0; i < range; i++)
            {
                cursor += dirVec;
                if (!IsInsideBoard(cursor))
                {
                    break;
                }
                result.Add(cursor);
            }
            return result;
        }

        /// <summary>攻击伤害（模板伤害 + AttackDamage 被动修正）。</summary>
        public int GetAttackDamage(GameState state, PieceInstance piece, AttackTemplate template)
        {
            return template.damage + GetPassiveModifier(state, piece, PassiveTarget.AttackDamage);
        }

        // ========== 修正查询（被动修正汇总：棋子固有+临时+遗物）==========

        /// <summary>汇总指定被动修正值（可叠加，直接累加）。</summary>
        public int GetPassiveModifier(GameState state, PieceInstance piece, PassiveTarget target)
        {
            int sum = 0;
            foreach (var ability in piece.GetAllAbilities())
            {
                if (ability.type == SpecialAbilityType.Passive && ability.passiveTarget == target)
                {
                    sum += ability.passiveValue;
                }
            }
            foreach (var relic in state.Relics)
            {
                foreach (var ability in relic.abilities)
                {
                    if (ability.type == SpecialAbilityType.Passive && ability.passiveTarget == target)
                    {
                        sum += ability.passiveValue;
                    }
                }
            }
            return sum;
        }

        // ========== 升变 / 胜负 ==========

        /// <summary>升变零门槛：该棋子定义了升变映射即可（无位置要求）。</summary>
        public bool IsPromotionValid(PieceInstance piece)
        {
            return piece.def != null && piece.def.promotionConfigId != 0;
        }

        public int CountAlivePieces(GameState state, Side side)
        {
            int count = 0;
            foreach (var piece in state.Pieces.Values)
            {
                if (piece.side == side)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>敌方全灭（场上无敌方棋子）。</summary>
        public bool IsEnemyWiped(GameState state)
        {
            return CountAlivePieces(state, Side.Enemy) == 0;
        }

        /// <summary>目标分数达成。</summary>
        public bool IsScoreTargetReached(GameState state, FloorConfig floor)
        {
            return state.PlayerScore >= floor.targetScore;
        }

        /// <summary>僵局（骨架：暂不实现——双方都无合法移动时平局，先返回 false）。</summary>
        public bool IsStalemate(GameState state)
        {
            return false;
        }

        // ========== 工具 ==========

        private Vector2Int DirectionToVector(Direction dir)
        {
            switch (dir)
            {
                case Direction.Up: return Vector2Int.up;
                case Direction.Down: return Vector2Int.down;
                case Direction.Left: return Vector2Int.left;
                case Direction.Right: return Vector2Int.right;
                case Direction.UpLeft: return new Vector2Int(-1, 1);
                case Direction.UpRight: return new Vector2Int(1, 1);
                case Direction.DownLeft: return new Vector2Int(-1, -1);
                case Direction.DownRight: return new Vector2Int(1, -1);
                default: return Vector2Int.zero;
            }
        }
    }
}
