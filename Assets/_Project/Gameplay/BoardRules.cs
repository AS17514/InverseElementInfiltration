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
            // 移动可通行 = 界内 + 无障碍物 + 无棋子占用
            return IsInsideBoard(cell) && !state.Obstacles.Contains(cell) && !IsCellOccupied(state, cell);
        }

        public bool IsPathClear(GameState state, PieceInstance piece, Vector2Int to)
        {
            // 单步移动（maxSteps=1 为主）：目标格可通行即可；多步移动逐格检查由 GetLegalMoves 完成
            return IsCellPassable(state, to);
        }

        // ========== 移动 ==========

        /// <summary>
        /// 计算移动模板的合法落点（多段路径：段 1 走完 → 从各到达点开始段 2，分支累加）。
        /// 每段沿方向集每个方向走 steps 步（出界/障碍物/占用逐格检查）；
        /// 解析时应用被动修正：步数 + MoveStep 修正（作用于每段）。
        /// </summary>
        public List<Vector2Int> GetLegalMoves(GameState state, PieceInstance piece, MoveTemplate template)
        {
            var reachable = new HashSet<Vector2Int>();
            var frontier = new List<Vector2Int> { piece.position }; // 当前段起点（初始 = 棋子位置）
            foreach (var segment in template.segments)
            {
                int steps = segment.steps + GetPassiveModifier(state, piece, PassiveTarget.MoveStep);
                var next = new List<Vector2Int>();
                foreach (var start in frontier)
                {
                    for (int dir = 1; dir <= (int)Direction.DownRight; dir <<= 1)
                    {
                        if ((segment.directions & (Direction)dir) == 0)
                        {
                            continue;
                        }
                        var dirVec = DirectionToVector((Direction)dir);
                        var cursor = start;
                        for (int i = 0; i < steps; i++)
                        {
                            cursor += dirVec;
                            if (!IsCellPassable(state, cursor))
                            {
                                break; // 出界/障碍/占用——该方向停止
                            }
                            if (reachable.Add(cursor))
                            {
                                next.Add(cursor); // 新到达点（作为下一段起点）
                            }
                        }
                    }
                }
                frontier = next;
                if (frontier.Count == 0)
                {
                    break; // 无路可走——提前结束
                }
            }
            return new List<Vector2Int>(reachable);
        }

        // ========== 攻击 ==========

        /// <summary>
        /// 计算攻击模板的可攻击格子（任意格子——含己方/空格：可空放/可打己方）。
        /// 按攻击方式分派：
        ///   近战群攻 → 以自身为中心的范围形状（Cross 十字 / Surround 周围 8 格），无方向
        ///   其他     → 遍历可选方向集（directions 位标志）每个方向沿直线 range 格：
        ///       直射     → 路径逐格检查障碍（被挡即止）
        ///       抛射/法术 → 无视障碍（射程内直线格直接可达）
        ///       近战     → 射程 1（相邻格，无阻挡概念）
        /// 攻击时玩家从候选格集合中选一格（射程内任意格均为候选）。
        /// 解析时应用被动修正：射程 + AttackRange 修正。
        /// </summary>
        public List<Vector2Int> GetAttackableCells(GameState state, PieceInstance piece, AttackTemplate template)
        {
            if (template.mode == AttackMode.MeleeAOE)
            {
                return GetShapeCells(state, piece, template.shape);
            }

            var result = new List<Vector2Int>();
            int range = template.range + GetPassiveModifier(state, piece, PassiveTarget.AttackRange);
            for (int dir = 1; dir <= (int)Direction.DownRight; dir <<= 1)
            {
                if ((template.directions & (Direction)dir) == 0)
                {
                    continue;
                }
                var dirVec = DirectionToVector((Direction)dir);
                var cursor = piece.position;
                for (int i = 0; i < range; i++)
                {
                    cursor += dirVec;
                    if (!IsInsideBoard(cursor))
                    {
                        break;
                    }
                    // 直射：路径逐格检查障碍（目标格有障碍也算被挡）；抛射/法术/近战无视障碍
                    if (template.mode == AttackMode.DirectFire && state.Obstacles.Contains(cursor))
                    {
                        break;
                    }
                    result.Add(cursor);
                }
            }
            return result;
        }

        /// <summary>以攻击者为中心的范围形状（近战群攻）。</summary>
        private List<Vector2Int> GetShapeCells(GameState state, PieceInstance piece, AttackShape shape)
        {
            var result = new List<Vector2Int>();
            if (shape == AttackShape.Cross)
            {
                result.Add(piece.position + Vector2Int.up);
                result.Add(piece.position + Vector2Int.down);
                result.Add(piece.position + Vector2Int.left);
                result.Add(piece.position + Vector2Int.right);
            }
            else if (shape == AttackShape.Surround)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        result.Add(piece.position + new Vector2Int(dx, dy));
                    }
                }
            }
            else
            {
                result.Add(piece.position + Vector2Int.up); // Single 默认前 1 格（近战单体）
            }
            result.RemoveAll(c => !IsInsideBoard(c));
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
