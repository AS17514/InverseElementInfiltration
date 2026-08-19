using System;
using System.Collections.Generic;
using UnityEngine;

namespace TheLaw.Data
{
    // ========== 移动/目标参数 ==========

    /// <summary>移动步（方向 → 可选步数集合；移动时选方向 + 该方向允许的步数）。</summary>
    [Serializable]
    public class MoveStep
    {
        public Direction direction;     // 单个方向（绝对棋盘方向，非位标志）
        public List<int> steps = new List<int>(); // 可选步数集合：[1,2]=走1或2格；[2]=固定2格（不可走1格）

        public MoveStep() { }

        public MoveStep(Direction direction, List<int> steps)
        {
            this.direction = direction;
            this.steps = steps;
        }
    }

    /// <summary>移动段（一段 = 方向→步数选项列表；段内选一个方向+步数执行）。</summary>
    [Serializable]
    public class MoveSegment
    {
        public List<MoveStep> moves = new List<MoveStep>();

        public MoveSegment() { }

        public MoveSegment(List<MoveStep> moves)
        {
            this.moves = moves;
        }
    }

    /// <summary>移动路径（一条完整路径 = 段序列，顺序执行；落点 = 最后一段终点；段间从各终点继续）。</summary>
    [Serializable]
    public class MovePath
    {
        public List<MoveSegment> segments = new List<MoveSegment>();

        public MovePath() { }

        public MovePath(List<MoveSegment> segments)
        {
            this.segments = segments;
        }
    }

    /// <summary>目标选择参数（AI 自动选目标用）。</summary>
    [Serializable]
    public struct TargetParam
    {
        public TargetRule rule;
        public int amount;

        public TargetParam(TargetRule rule, int amount)
        {
            this.rule = rule;
            this.amount = amount;
        }
    }

    // ========== 模板族（程序槽位内容，参数化可编辑）==========

    /// <summary>模板基类（预设拼图块；程序 = 4 槽模板排列）。</summary>
    [Serializable]
    public abstract class Template
    {
        /// <summary>
        /// 程序块编号（种类内编号，如 Move-1/Attack-2——同结构可复用同 id）。
        /// JSON modules 手动指定；描述表（slot-descriptions.json）按"种类+编号"查描述；0 = 未编号（回退代码生成）。
        /// </summary>
        public int id;
    }

    /// <summary>移动模板：路径选项集合（多条路径独立计算；可达格 = 各路径落点合集；移动方式由模板决定，可编辑）。
    /// ⚠️ 2026-08-16：+ jumpOffsets（跳跃落点——与常规路径共存，落点并集；跳跃只查落点合法性，不查中间路径）。</summary>
    [Serializable]
    public class MoveTemplate : Template
    {
        public List<MovePath> paths = new List<MovePath>();

        /// <summary>
        /// 跳跃落点（2026-08-16）：相对棋子位置的偏移集合（绝对方向——与攻击模板 points 同语义，不随 facing 旋转）。
        /// 与常规路径共存（GetLegalMoves 落点并集）；跳跃只查落点合法性（界内 + 非占用 + 非障碍），不查中间路径。
        /// 配置来源：templates.json jumpOffsets / 棋子 JSON 移动模块 jumpOffsets。
        /// </summary>
        public List<Vector2Int> jumpOffsets = new List<Vector2Int>();

        public MoveTemplate() { }

        public MoveTemplate(List<MovePath> paths)
        {
            this.paths = paths;
        }
    }

    /// <summary>攻击射程步（方案 B，2026-08-16）：方向 → 可选射程集合（与移动 MoveStep 同构对称）。
    /// ranges=[1,2,3] = 该方向 1~3 格皆可攻击；[2] = 固定第 2 格；判定逐格、首个棋子/障碍截断（同直射）。</summary>
    [Serializable]
    public class AttackRangeStep
    {
        public Direction direction;         // 相对棋子 facing（解析时旋转——与 directions 同语义）
        public List<int> ranges = new List<int>(); // 可选射程集合

        public AttackRangeStep() { }

        public AttackRangeStep(Direction direction, List<int> ranges)
        {
            this.direction = direction;
            this.ranges = ranges;
        }
    }

    /// <summary>攻击模板：可选方向集 + 射程 + 伤害 + 友伤 + 攻击方式（攻击参数全部由模板决定，可编辑）。
    /// ⚠️ 2026-08-16：+ rangeSteps（方向→射程集合，方案 B）——非空时优先于 directions+range（每方向独立射程）。</summary>
    [Serializable]
    public class AttackTemplate : Template
    {
        public AttackMode mode = AttackMode.Melee; // 攻击方式（近战/近战群攻/直射/抛射/法术）
        public Direction directions = Direction.Up; // 可选方向集（[Flags] 位标志；攻击时玩家从集合中选一格；默认 {上}=正前方；近战群攻忽略）
        public int range = 1;                      // 射程（修正型遗物"范围+1"作用于此；近战固定 1=相邻格）
        public int damage = 1;                     // 伤害（扣承伤次数）
        public bool friendlyFire = true;           // 友伤开关（默认 true——"大部分有友伤"）
        public AttackShape shape = AttackShape.Single; // 范围形状（保留——攻击模板不再使用；特殊能力附着用 Cross）
        public List<Vector2Int> points = new List<Vector2Int>(); // 抛射/法术：自由点选攻击点（相对棋子锚点的偏移集合，无射程概念、任意形状、对点攻击无视障碍）

        /// <summary>
        /// 方向→射程集合（方案 B，2026-08-16）：非空时优先于 directions+range——
        /// 支持"正前射程 3、两斜各 2"等多方向独立射程；近战 range&gt;1 时逐格、首个棋子/障碍截断（与直射同语义）。
        /// </summary>
        public List<AttackRangeStep> rangeSteps = new List<AttackRangeStep>();

        public AttackTemplate() { }

        public AttackTemplate(AttackMode mode, Direction directions, int range, int damage, bool friendlyFire = true, AttackShape shape = AttackShape.Single)
        {
            this.mode = mode;
            this.directions = directions;
            this.range = range;
            this.damage = damage;
            this.friendlyFire = friendlyFire;
            this.shape = shape;
        }
    }

    /// <summary>
    /// 效果模块（EffectTemplate——2026-08-19 数据层落地；**术语规范：模块 = 槽位内容，槽 = 4 个固定位置**）：
    /// 效果 = 可装备模块（装配即被动获得该能力；价值表 Effect-N）。
    /// 引用特殊能力资产：abilityKey = SpecialAbilityDef 资产名（如 "Ability_ShieldBlock_OnDamaged_1"——运行时 ConfigTable 查）。
    /// ⚠️ 执行语义（2026-08-19 确认）：**不耗 AP、被动、有模块即生效**——BattleFlow 执行遇效果模块跳过（不落账不扣费）；
    /// 能力生效 = PieceInstance.GetAllAbilities 动态并入（装配即生效）。
    /// </summary>
    [Serializable]
    public class EffectTemplate : Template
    {
        public string abilityKey; // 特殊能力资产名（templates.json ability 字段；导入器原样填写）

        public EffectTemplate() { }

        public EffectTemplate(string abilityKey)
        {
            this.abilityKey = abilityKey;
        }
    }

    /// <summary>
    /// 空操作槽（主动编排的"什么都不做"）。
    /// ⚠️ 2026-08-15 策划新案：行动槽仅移动/攻击/效果三类——**无跳过槽**。本类保留不删：
    /// ① 运行时自动跳过机制（BattleFlow 无路可走/无目标 → SkipAction）仍依赖执行分支；
    /// ② 模板库（templates.json）无 skip 条目——实际不可编排；此分支为兼容保留（暂无用代码）。
    /// </summary>
    [Serializable]
    public class SkipTemplate : Template
    {
    }

    /// <summary>程序定义（4 槽模板排列——默认模组/备用模组的单位）。</summary>
    [Serializable]
    public class ProgramDef
    {
        // [SerializeReference]：Unity YAML 多态序列化必需——否则基类列表存子类会退化成 Template（丢数据）
        [SerializeReference]
        public List<Template> slots = new List<Template>(4);

        public ProgramDef() { }

        public ProgramDef(List<Template> slots)
        {
            this.slots = slots;
        }
    }
}
