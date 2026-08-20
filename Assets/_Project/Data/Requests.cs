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
}
