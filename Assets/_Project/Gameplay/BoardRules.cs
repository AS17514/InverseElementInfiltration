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
            // 落点可通行 = 界内 + 无障碍物（含麻将墙——2026-08-20 统一 IsBlocked）+ 无棋子占用（落点不可重叠）
            return IsInsideBoard(cell) && !state.IsBlocked(cell) && !IsCellOccupied(state, cell);
        }

        /// <summary>路径格可通行 = 界内 + 无障碍物（棋子可穿过——路径经过棋子不阻挡；2026-08-20 统一 IsBlocked 含麻将墙）。</summary>
        public bool IsPathCellPassable(GameState state, Vector2Int cell)
        {
            return IsInsideBoard(cell) && !state.IsBlocked(cell);
        }

        public bool IsPathClear(GameState state, PieceInstance piece, Vector2Int to)
        {
            // 单步移动（maxSteps=1 为主）：目标格可通行即可；多步移动逐格检查由 GetLegalMoves 完成
            return IsCellPassable(state, to);
        }

        // ========== 移动 ==========

        /// <summary>
        /// 计算移动模板的合法落点（路径选项模型）。
        /// 每条路径独立计算（起点 = 棋子位置）：段序列顺序执行，段间从各终点继续；
        /// 段内 moves（方向→可选步数）选一个执行——段内多选项产生多个终点（分支）。
        /// 路径格检查：界内 + 非障碍（棋子可穿过）；落点 = 最后一段终点，额外检查非占用（不可重叠）。
        /// 【方向语义】MoveStep.direction = 相对棋子 facing（正前方）——解析时按 facing 旋转到世界方向。
        /// 解析时应用被动修正：步数 + MoveStep 修正（作用于每段每个选项）。
        /// </summary>
        public List<Vector2Int> GetLegalMoves(GameState state, PieceInstance piece, MoveTemplate template)
        {
            var reachable = new HashSet<Vector2Int>();
            foreach (var path in template.paths)
            {
                var frontier = new List<Vector2Int> { piece.position }; // 当前段起点（路径起点 = 棋子位置）
                for (int segIdx = 0; segIdx < path.segments.Count; segIdx++)
                {
                    var segment = path.segments[segIdx];
                    bool isLastSegment = segIdx == path.segments.Count - 1;
                    var next = new List<Vector2Int>();
                    foreach (var start in frontier)
                    {
                        foreach (var move in segment.moves)
                        {
                            int stepModifier = GetPassiveModifier(state, piece, PassiveTarget.MoveStep); // 被动修正（作用于每段每选项）
                            foreach (var k in move.steps)
                            {
                                int steps = k + stepModifier;
                                var dirVec = RotateVector(DirectionToVector(move.direction), piece.facing); // 相对 facing → 世界方向
                                var cursor = start;
                                bool blocked = false;
                                for (int i = 0; i < steps; i++)
                                {
                                    cursor += dirVec;
                                    if (!IsPathCellPassable(state, cursor))
                                    {
                                        blocked = true; // 路径中障碍（含终点格障碍）→ 该步数不可达；更远步数也不可达
                                        break;
                                    }
                                }
                                if (blocked)
                                {
                                    continue;
                                }
                                if (isLastSegment)
                                {
                                    if (!CanLandOnCell(state, piece, cursor))
                                    {
                                        continue; // 落点不可重叠（棋子）——「吃子」例外：玩家侧可踩敌方棋子格（移动后直接击败）
                                    }
                                    reachable.Add(cursor); // 落点
                                }
                                else
                                {
                                    next.Add(cursor); // 中转点（棋子可穿过——不检查占用）
                                }
                            }
                        }
                    }
                    frontier = next;
                    if (frontier.Count == 0)
                    {
                        break; // 该路径无路可走——提前结束
                    }
                }
            }
            // 跳跃落点（2026-08-16）：相对棋子位置偏移（绝对方向——与攻击 points 同语义，不随 facing 旋转）；
            // 与常规路径共存（落点并集）；跳跃只查落点合法性（界内 + 非占用 + 非障碍——吃子例外同 CanLandOnCell），不查中间路径
            foreach (var offset in template.jumpOffsets)
            {
                var cell = piece.position + offset;
                if (IsInsideBoard(cell) && !state.IsBlocked(cell) && CanLandOnCell(state, piece, cell))
                {
                    reachable.Add(cell);
                }
            }
            return new List<Vector2Int>(reachable);
        }

        /// <summary>落点可否降落（2026-08-24 能力「吃子」例外）：空格可落；占用格仅玩家侧「吃子」激活时可踩**敌方棋子**格（移动后直接击败）。</summary>
        private static bool CanLandOnCell(GameState state, PieceInstance piece, Vector2Int cell)
        {
            var occupant = state.GetPieceAt(cell);
            if (occupant == null) return true;
            return state.HasRelicEffect(RelicEffectType.Devour) && piece.side == Side.Player && occupant.side == Side.Enemy;
        }

        // ========== 攻击 ==========

        /// <summary>
        /// 计算攻击模板的可攻击格子（任意格子——含己方/空格：可空放/可打己方）。
        /// 分派：
        ///   抛射/法术（points 非空）→ 自由点选攻击点：锚点 + 偏移集合（无视障碍对点；出界过滤）
        ///   其他 → 统一遍历可选方向集（directions 位标志）每个方向沿直线 range 格：
        ///       直射     → 路径逐格：障碍物截断（不可达）；【第一个可攻击物（棋子）】截断（该格为目标）
        ///       近战     → 射程 1（相邻格，无阻挡概念）
        ///       近战群攻 → 范围内全部格子被攻击（玩家选一格仅作确认；无阻挡概念）
        /// 攻击时玩家从候选格集合中选一格（普通攻击打所选格；近战群攻打范围内全部）。
        /// 【方向语义】directions = 相对棋子 facing（正前方）——解析时按 facing 旋转到世界方向
        ///   （敌方 facing 已镜像——Up↔Down——方向自动朝向我方，无需额外按阵营翻转）
        /// 解析时应用被动修正：射程 + AttackRange 修正（对点模式不适用）。
        /// </summary>
        public List<Vector2Int> GetAttackableCells(GameState state, PieceInstance piece, AttackTemplate template)
        {
            // 抛射/法术：自由点选攻击点（相对棋子锚点偏移，无视障碍对点攻击）
            if ((template.mode == AttackMode.Arcing || template.mode == AttackMode.Spell) && template.points.Count > 0)
            {
                var pointCells = new List<Vector2Int>();
                foreach (var offset in template.points)
                {
                    var cell = piece.position + offset;
                    if (IsInsideBoard(cell))
                    {
                        pointCells.Add(cell);
                    }
                }
                return pointCells;
            }

            var result = new List<Vector2Int>();

            // 方案 B：方向→射程集合（2026-08-16）——每方向独立射程（如正前 3、两斜各 2）；
            // 近战/直射：逐格、首个棋子/障碍截断（近战 range>1 语义同直射——第一格有棋只能打第一格）；
            // 近战群攻：范围内全部不截断；被动射程修正逐方向作用
            if (template.rangeSteps.Count > 0)
            {
                int rangeMod = GetPassiveModifier(state, piece, PassiveTarget.AttackRange);
                foreach (var step in template.rangeSteps)
                {
                    int maxR = 0;
                    foreach (var r in step.ranges) maxR = Mathf.Max(maxR, r);
                    if (maxR <= 0) continue;
                    var dirVec = RotateVector(DirectionToVector(step.direction), piece.facing);
                    var cursor = piece.position;
                    for (int i = 1; i <= maxR + rangeMod; i++)
                    {
                        cursor += dirVec;
                        if (!IsInsideBoard(cursor)) break;
                        bool inRange = step.ranges.Contains(i - rangeMod);
                        if (template.mode == AttackMode.MeleeAOE)
                        {
                            if (inRange) result.Add(cursor); // 群攻：范围内全部被攻击，不截断
                            continue;
                        }
                        // 近战/直射：障碍截断 + 首个棋子截断（目标并截断；2026-08-20 统一 IsBlocked 含麻将墙）
                        if (state.IsBlocked(cursor)) break;
                        if (inRange) result.Add(cursor);
                        if (IsCellOccupied(state, cursor)) break;
                    }
                }
                return result;
            }

            int range = template.range + GetPassiveModifier(state, piece, PassiveTarget.AttackRange);
            for (int dir = 1; dir <= (int)Direction.DownRight; dir <<= 1)
            {
                if ((template.directions & (Direction)dir) == 0)
                {
                    continue;
                }
                var dirVec = RotateVector(DirectionToVector((Direction)dir), piece.facing); // 相对 facing → 世界方向
                var cursor = piece.position;
                for (int i = 0; i < range; i++)
                {
                    cursor += dirVec;
                    if (!IsInsideBoard(cursor))
                    {
                        break;
                    }
                    if (template.mode == AttackMode.DirectFire || template.mode == AttackMode.Melee)
                    {
                        // 直射/近战（2026-08-16：近战 range>1 语义同直射——逐格、首个棋子/障碍截断；range=1 无差别）：
                        // 障碍物格截断（不可达）；棋子格 = 目标并截断（第一个可攻击物阻挡；2026-08-20 统一 IsBlocked 含麻将墙）
                        if (state.IsBlocked(cursor))
                        {
                            break;
                        }
                        result.Add(cursor);
                        if (IsCellOccupied(state, cursor))
                        {
                            break;
                        }
                        continue;
                    }
                    // 近战群攻/抛射/法术（points 空回退）：无视障碍
                    result.Add(cursor);
                }
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

        /// <summary>
        /// 相对方向向量按棋子 facing 旋转到世界方向（方向语义：directions = 相对正前方）。
        /// 旋转表：Up 无旋转 / Down 180° / Left 90° / Right -90°（向量变换验证）。
        /// </summary>
        private Vector2Int RotateVector(Vector2Int v, Facing facing)
        {
            switch (facing)
            {
                case Facing.Down:
                    return new Vector2Int(-v.x, -v.y); // 180°
                case Facing.Left:
                    return new Vector2Int(-v.y, v.x);  // 相对Up(0,1)→世界Left(-1,0)
                case Facing.Right:
                    return new Vector2Int(v.y, -v.x);  // 相对Up(0,1)→世界Right(1,0)
                case Facing.Up:
                default:
                    return v; // 无旋转（默认正前方=世界上方）
            }
        }

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
