using System;
using UnityEngine;

namespace TheLaw.Data
{
    // ========== 行动族（回放凭据，有目标）==========

    /// <summary>行动基类（Resolver 落账的单位）。</summary>
    [Serializable]
    public abstract class ConcreteAction
    {
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
        public Side side;
        public Vector2Int cell;
        public int waveIndex = -1; // 所属波次（-1=非波次棋子；每波得分按此累计）
        public int cardInstanceId; // 2026-08-21：消耗的牌实例 id（0=隐式选择回退）

        public DeployAction(int pieceDefId, Side side, Vector2Int cell)
        {
            this.pieceDefId = pieceDefId;
            this.side = side;
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
}
