using System;

namespace TheLaw.Data
{
    // ========== 战斗 ==========

    /// <summary>控制方（对称设计：一套棋子系统，控制方不同而已）。</summary>
    public enum Side
    {
        Player,
        Enemy,
    }

    /// <summary>棋子种类（有且只有三种）。</summary>
    public enum PieceType
    {
        Initial,     // 起始：战斗开始在场（带起始标记，数量由构筑决定）
        Deployable,  // 部署：战斗中耗 AP 部署到己方部署区
        Promoted,    // 升变：手牌中预置的升变牌，替换使用
    }

    /// <summary>
    /// 属性（2026-08-20「属性」玩法——金木水火土；None = 无属性（基础玩法/牌不带属性））。
    /// 麻将牌不视为棋子 → 不携带属性（始终 None）；复制牌带属性（"属性相同"——Card.element → 棋子实例）。
    /// </summary>
    public enum Element
    {
        None,   // 无属性（基础玩法 / 麻将牌）
        Gold,   // 金
        Wood,   // 木
        Water,  // 水
        Fire,   // 火
        Earth,  // 土
    }

    /// <summary>朝向（初始=defaultFacing，默认 Up=向前）。</summary>
    public enum Facing
    {
        Up,
        Down,
        Left,
        Right,
    }

    /// <summary>战斗阶段（副作用只在阶段切换触发）。</summary>
    public enum BattlePhase
    {
        Placement,   // 开局摆放：起始标记棋子自由布置
        PlayerTurn,
        EnemyTurn,
        GameOver,
    }

    /// <summary>方向（位标志，| 组合）——MoveStep/AttackTemplate.directions 用。</summary>
    [Flags]
    public enum Direction
    {
        None = 0,
        Up = 1 << 0,
        Down = 1 << 1,
        Left = 1 << 2,
        Right = 1 << 3,
        UpLeft = 1 << 4,
        UpRight = 1 << 5,
        DownLeft = 1 << 6,
        DownRight = 1 << 7,
    }

    /// <summary>占格大小。</summary>
    public enum Footprint
    {
        Size1x1,
        Size1x2,
        Size1x3,
    }

    // ========== 规则 ==========

    /// <summary>目标选择规则（AI 自动选目标用；玩家目标手动指定）。</summary>
    public enum TargetRule
    {
        Nearest,     // 最近
        LowestHP,    // 承伤最少
        HighestValue,// 价值最高（短视吃子）
    }

    /// <summary>效果目标范围（事件效果用）。</summary>
    public enum TargetScope
    {
        PieceCollection, // 一组棋子（选棋子）
        Board,           // 棋盘（选格）
    }

    /// <summary>关卡胜利规则（参数化：差异全在配置）。</summary>
    public enum VictoryRule
    {
        WipeOut,       // 击败敌方全部棋子
        ScoreTarget,   // 达成目标分数（或击败所有波次）
        Both,          // 最后波全灭 + 达目标分数（双条件）
        PerWaveScore,  // 击败所有波次前提下：每波得分达标 或 达目标分数
    }

    /// <summary>攻击形状（特殊能力附着结算用——攻击模板不再使用，近战群攻 = 方向集×深度）。</summary>
    public enum AttackShape
    {
        Single,   // 单体：只结算目标格
        Cross,    // 十字：目标格 + 上下左右共 5 格（十字额外伤害附着用）
        Surround, // 周围：以攻击者为中心周围 8 格（保留，暂未使用）
    }

    /// <summary>攻击方式（模块类型——棋子程序槽位里的攻击模块）。</summary>
    public enum AttackMode
    {
        Melee,      // 近战：相邻格直接攻击（无阻挡概念）
        MeleeAOE,   // 近战群攻：沿可选方向集×深度，范围内全部格子被攻击（玩家选一格确认）
        DirectFire, // 直射：直线射程，路径受障碍物阻挡
        Arcing,     // 抛射：越过障碍物直接攻击（射程内任意直线格）
        Spell,      // 法术：越过障碍物直接攻击（与抛射暂时无差别——为未来差异留位）
    }

    // ========== 爬塔 ==========

    /// <summary>节点类型（单线序列：事件关在前、战斗关在层末）。</summary>
    public enum NodeType
    {
        Event,
        Battle,
    }

    /// <summary>节点状态。</summary>
    public enum NodeState
    {
        Locked,
        Available,
        Completed,
    }

    // ========== 特殊能力 ==========

    /// <summary>特殊能力类型（三类执行路径）。</summary>
    public enum SpecialAbilityType
    {
        Passive, // 被动修正：模板解析时/结算时修正数值
        Trigger, // 事件触发：触发点 + 待执行队列
        Attach,  // 附着：随攻击/移动附加结算
    }

    /// <summary>触发点（流程的固定插槽；FloorRules 层差异 + 遗物/特殊能力都是消费者）。</summary>
    public enum TriggerPoint
    {
        OnBattleStart,
        OnTurnStart,
        OnTurnEnd,
        OnKill,
        OnPieceLanded,
        OnDamaged, // 受击（结算前拦截——护盾抵挡用；未来"受击回血"类能力挂这里）
    }

    /// <summary>被动修正目标。</summary>
    public enum PassiveTarget
    {
        MoveStep,     // 移动步数
        AttackDamage, // 攻击伤害
        AttackRange,  // 攻击射程
        Durability,   // 承伤（恢复/上限）
    }

    /// <summary>附着点。</summary>
    public enum AttachPoint
    {
        OnAttack, // 攻击结算内附加（如十字额外伤害）
        OnMove,   // 移动结算内附加
    }

    /// <summary>触发型效果动作（第一版全部作用于自身）。</summary>
    public enum TriggerEffect
    {
        ExtraAction,    // 免费额外行动一次（完整执行，不耗 AP）
        HealDurability, // 恢复承伤（+amount）
        ShieldBlock,    // 护盾：抵挡 amount 点伤害（一次性消耗，不恢复；结算前拦截）
    }
}
