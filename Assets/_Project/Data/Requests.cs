using System;
using UnityEngine;

namespace TheLaw.Data
{
    // ========== 请求族（输入，未校验；UI + AI 共同契约）==========

    /// <summary>请求基类（free = 不扣 AP——免费额外行动/波次部署用）。</summary>
    [Serializable]
    public abstract class Request
    {
        public bool free;
    }

    /// <summary>部署请求。</summary>
    [Serializable]
    public class DeployRequest : Request
    {
        public int pieceDefId;
        public Vector2Int cell;
        public int cardInstanceId; // 2026-08-21：要打出的那张牌的实例 id（0=旧请求——Resolver 隐式选择回退）

        public DeployRequest(int pieceDefId, Vector2Int cell)
        {
            this.pieceDefId = pieceDefId;
            this.cell = cell;
        }
    }

    /// <summary>升变请求。</summary>
    [Serializable]
    public class PromoteRequest : Request
    {
        public int pieceId;
        public int newDefId;
        public int cardInstanceId; // 2026-08-21：要消耗的那张升变牌实例 id（0=旧请求回退）

        public PromoteRequest(int pieceId, int newDefId)
        {
            this.pieceId = pieceId;
            this.newDefId = newDefId;
        }
    }

    /// <summary>执行请求（按程序执行一次棋子）。</summary>
    [Serializable]
    public class ExecuteRequest : Request
    {
        public int pieceId;

        public ExecuteRequest(int pieceId)
        {
            this.pieceId = pieceId;
        }
    }

    /// <summary>抽牌请求（2026-08-19 策划确认新行动：玩家消耗 1 AP 从抽牌堆抽 1 张到手牌；抽牌堆空 → 拒绝）。</summary>
    [Serializable]
    public class DrawCardRequest : Request
    {
        public DrawCardRequest()
        {
        }
    }

    /// <summary>麻将·打出墙体请求（2026-08-20 麻将玩法：耗 1 AP；手牌麻将牌 → 棋盘 1×2 竖两格墙体）。</summary>
    [Serializable]
    public class PlayMahjongRequest : Request
    {
        public int mahjongValue;    // 手牌中的麻将牌点数（1~9）
        public Vector2Int cell;     // 墙体放置格（1×2 竖——本格 + 下格）

        public PlayMahjongRequest(int mahjongValue, Vector2Int cell)
        {
            this.mahjongValue = mahjongValue;
            this.cell = cell;
        }
    }

    /// <summary>麻将·摸切请求（2026-08-20：手牌麻将填入牌山 + 抽一张牌；1 AP）。</summary>
    [Serializable]
    public class MochiRequest : Request
    {
        public int mahjongValue;    // 手牌中的麻将牌点数

        public MochiRequest(int mahjongValue)
        {
            this.mahjongValue = mahjongValue;
        }
    }

    /// <summary>麻将·和牌请求（2026-08-20：手牌有雀头（任意两牌价值相同）且番数 &gt; 0 → 1 AP → 倍率+番数、番数清零）。</summary>
    [Serializable]
    public class HuRequest : Request
    {
        public HuRequest()
        {
        }
    }

    /// <summary>能力事件·选择候选（2026-08-22：三选一——选 index 落账 GrantRelic 并完成事件）。</summary>
    [Serializable]
    public class SelectAbilityRequest : Request
    {
        public int choiceIndex; // 0-2（AbilityCandidates）

        public SelectAbilityRequest(int choiceIndex)
        {
            this.choiceIndex = choiceIndex;
        }
    }

    /// <summary>能力事件·刷新候选（2026-08-22：每项可刷新一次——从池重抽替换，排除已展示；池空回填但至少不回当前项）。</summary>
    [Serializable]
    public class RefreshAbilityRequest : Request
    {
        public int choiceIndex; // 0-2

        public RefreshAbilityRequest(int choiceIndex)
        {
            this.choiceIndex = choiceIndex;
        }
    }

    // ========== 2026-08-24 新玩法（骰子/围棋/代币——设计定稿；仅玩家侧）==========

    /// <summary>骰子·投掷请求（2026-08-24：执行类行动 1 AP——随机 1~6 → DiceValue + 基础分；骰子玩法激活时可用）。</summary>
    [Serializable]
    public class RollDiceRequest : Request
    {
        public RollDiceRequest() { }
    }

    /// <summary>骰子·点数直线移动请求（2026-08-24：消耗骰子点数（不耗 AP）启动——给全场我方棋子挂"点数直线移动"buff；下次点棋子执行时重定向）。</summary>
    [Serializable]
    public class DiceMoveRequest : Request
    {
        public DiceMoveRequest() { }
    }

    /// <summary>围棋·部署"棋子牌"请求（2026-08-24：不耗 AP、每回合限 1 次、任意空格；蓝红 side 切换；部署后触发围杀检查）。</summary>
    [Serializable]
    public class DeployGoRequest : Request
    {
        public Vector2Int cell;

        public DeployGoRequest(Vector2Int cell)
        {
            this.cell = cell;
        }
    }

    /// <summary>代币·购买请求（2026-08-24：选弃牌区一张牌 → 消耗该牌价值数代币 → 获得复制入手牌 + 基础分+消耗数；每回合不限次）。</summary>
    [Serializable]
    public class BuyTokenRequest : Request
    {
        public int discardIndex; // 弃牌区选择索引（0 = 第一个）

        public BuyTokenRequest(int discardIndex)
        {
            this.discardIndex = discardIndex;
        }
    }
}
