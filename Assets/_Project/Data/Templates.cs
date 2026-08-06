using System;
using System.Collections.Generic;

namespace TheLaw.Data
{
    // ========== 移动/目标参数 ==========

    /// <summary>移动段（方向集位标志 + 步数）——MoveTemplate 的一段（多段顺序执行：段 1 走完 → 从到达点开始段 2）。</summary>
    [Serializable]
    public class MoveSegment
    {
        public Direction directions; // 位标志，| 组合
        public int steps;            // 步数

        public MoveSegment() { }

        public MoveSegment(Direction directions, int steps)
        {
            this.directions = directions;
            this.steps = steps;
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
    }

    /// <summary>移动模板：多段路径（顺序执行——段 1 走完从到达点开始段 2；移动方式由模板决定，可编辑）。</summary>
    [Serializable]
    public class MoveTemplate : Template
    {
        public List<MoveSegment> segments = new List<MoveSegment>();

        public MoveTemplate() { }

        public MoveTemplate(List<MoveSegment> segments)
        {
            this.segments = segments;
        }
    }

    /// <summary>攻击模板：可选方向集 + 射程 + 伤害 + 友伤 + 攻击方式（攻击参数全部由模板决定，可编辑）。</summary>
    [Serializable]
    public class AttackTemplate : Template
    {
        public AttackMode mode = AttackMode.Melee; // 攻击方式（近战/近战群攻/直射/抛射/法术）
        public Direction directions = Direction.Up; // 可选方向集（[Flags] 位标志；攻击时玩家从集合中选一格；默认 {上}=正前方；近战群攻忽略）
        public int range = 1;                      // 射程（修正型遗物"范围+1"作用于此；近战固定 1=相邻格）
        public int damage = 1;                     // 伤害（扣承伤次数）
        public bool friendlyFire = true;           // 友伤开关（默认 true——"大部分有友伤"）
        public AttackShape shape = AttackShape.Single; // 范围形状（近战群攻用：Cross/Surround）

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

    /// <summary>空操作槽（主动编排的"什么都不做"）。</summary>
    [Serializable]
    public class SkipTemplate : Template
    {
    }

    /// <summary>程序定义（4 槽模板排列——默认模组/备用模组的单位）。</summary>
    [Serializable]
    public class ProgramDef
    {
        public List<Template> slots = new List<Template>(4);

        public ProgramDef() { }

        public ProgramDef(List<Template> slots)
        {
            this.slots = slots;
        }
    }
}
