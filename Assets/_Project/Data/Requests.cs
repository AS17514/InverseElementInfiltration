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
}
