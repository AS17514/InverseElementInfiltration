using System;
using UnityEngine;

namespace TheLaw.Data
{
    // ========== 行动族（回放凭据，有目标）==========

    /// <summary>行动基类（Resolver 落账的单位）。
    /// ⚠️ 2026-08-23 回放增强：附加上下文（side/defId/turn）——离线推演可直接读（旧档缺省=0 兼容，读档不校验）。</summary>
    [Serializable]
    public abstract class ConcreteAction
    {
        public Side side;       // 发起方阵营（Deploy 自带；其余由 Resolver.LogAction 按棋子补全；旧档缺省=0[Player]）
        public int defId;       // 发起棋子定义 id（Deploy 为 pieceDefId；旧档缺省 0）
        public int turn;        // 发起时回合数（GameState.TurnCount；事件区/战斗前 = 0）
    }

    /// <summary>移动行动。</summary>
    [Serializable]
    public class MoveAction : ConcreteAction
    {
        public int pieceId;
        public Vector2Int from;
        public Vector2Int to;

        public MoveAction(int pieceId, Vector2Int from, Vector2Int to)
        {
            this.pieceId = pieceId;
            this.from = from;
            this.to = to;
        }
    }

    /// <summary>攻击行动（目标 = 格子——可空放/可打己方/友伤由模板控制）。</summary>
    [Serializable]
    public class AttackAction : ConcreteAction
    {
        public int pieceId;
        public Vector2Int targetCell;
        public AttackTemplate template; // 记录形状/伤害/友伤（结算用）

        public AttackAction(int pieceId, Vector2Int targetCell, AttackTemplate template)
        {
            this.pieceId = pieceId;
            this.targetCell = targetCell;
            this.template = template;
        }
    }

    /// <summary>部署行动。</summary>
    [Serializable]
    public class DeployAction : ConcreteAction
    {
        public int pieceDefId;
        public Vector2Int cell;
        public int waveIndex = -1; // 所属波次（-1=非波次棋子；每波得分按此累计）
        public int cardInstanceId; // 2026-08-21：消耗的牌实例 id（0=隐式选择回退）

        // ⚠️ 2026-08-23：side 不再子类声明——统一用基类 ConcreteAction.side（回放上下文字段；同名字段会遮蔽 CS0108 + 存档序列化可能丢回放上下文）
        public DeployAction(int pieceDefId, Side side, Vector2Int cell)
        {
            this.pieceDefId = pieceDefId;
            this.side = side;
            this.defId = pieceDefId; // 回放上下文同步（部署发起=被部署的定义）
            this.cell = cell;
        }
    }

    /// <summary>升变行动（落账时手牌减一）。</summary>
    [Serializable]
    public class PromoteAction : ConcreteAction
    {
        public int pieceId;
        public int newDefId;
        public int cardInstanceId; // 2026-08-21：消耗的升变牌实例 id（0=隐式选择回退）

        public PromoteAction(int pieceId, int newDefId)
        {
            this.pieceId = pieceId;
            this.newDefId = newDefId;
        }
    }

    /// <summary>跳过原因（移动槽无路可走等）。</summary>
    public enum SkipReason
    {
        NoMove,   // 移动被拒（没格子走）
        NoTarget, // 攻击无目标（已无合法目标——玩家不选时兜底）
        NoAttack, // 2026-08-24 能力「吃子」：执行跳过全部攻击槽（攻击行动不生效——移动吃子代替）
    }

    /// <summary>跳过行动（无表现，不进入表现等待）。</summary>
    [Serializable]
    public class SkipAction : ConcreteAction
    {
        public int pieceId;
        public SkipReason reason;

        public SkipAction(int pieceId, SkipReason reason)
        {
            this.pieceId = pieceId;
            this.reason = reason;
        }
    }

    /// <summary>死亡记录（2026-08-23 回放增强——死亡也进 ReplayLog，补"死亡黑盒"缺口；非"行动"语义，仅时序占位）。</summary>
    [Serializable]
    public class DeathAction : ConcreteAction
    {
        public int victimId;
        public int victimDefId;
        public Side victimSide;
        public int killerId = -1;   // -1 = 非攻击击杀（结算/清理/竞态等——未知来源时标识）
        public int x;
        public int y;

        public DeathAction(int victimId, int victimDefId, Side victimSide, int killerId, Vector2Int pos)
        {
            this.victimId = victimId;
            this.victimDefId = victimDefId;
            this.victimSide = victimSide;
            this.killerId = killerId;
            this.x = pos.x;
            this.y = pos.y;
        }
    }
}
