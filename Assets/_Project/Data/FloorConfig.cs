using System;
using System.Collections.Generic;
using TheLaw.Core;
using UnityEngine;

namespace TheLaw.Data
{
    /// <summary>
    /// 关卡配置（SO 资产）：数值差异全部参数化——胜利规则/波次/敌方 AP。
    /// 每层规则不同 = 换一份 FloorConfig。
    /// </summary>
    public class FloorConfig : GameConfigBase
    {
        public VictoryRule victoryRule;                       // 胜利规则（1关全灭/2关波次或分/3关每波达标/4关双条件）
        public int targetScore;                               // 目标分数（2/3/4 关用；数值待策划回填）
        public List<WaveDef> waveDefs = new List<WaveDef>();  // 波次定义（按回合触发）
        public int enemyMaxAP = 3;                            // 敌方每回合行动次数上限（随关卡变化）
        public List<string> eventSequence = new List<string>(); // 事件节点类型序列（固定顺序：如 [ability, edit, deck]——与 eventPoolIds 顺序对应）
        public List<string> eventPoolIds = new List<string>();// 本层事件池（跨层复用：池为一等对象；与 eventSequence 顺序对应）
    }

    /// <summary>波次定义（敌方按回合间隔部署）。</summary>
    [Serializable]
    public class WaveDef
    {
        public int startTurn;                 // 第几回合出波（间隔 3~5 回合，可调）
        public List<int> pieceDefIds = new List<int>(); // 阵容（Def id）——randomPool=false 时用固定阵容
        public bool randomPool;               // 随机池模式（2026-08-19：true=从 poolType 类棋子随机抽 count 个，可重复——RandomManager 可复现；false=固定阵容）
        public PieceType poolType;            // 随机池种类（randomPool=true 时：Initial/Deployable）
        public int count;                     // 随机抽取数量（randomPool=true 时）
        public List<Vector2Int> positions = new List<Vector2Int>(); // 固定站位（2026-08-19：与阵容顺序对应；空=部署区自动找位；被占用格跳过）
        public bool isLastWave;               // 最后一波：出现后再过 N 回合强制判定胜负
        public int endCountdown;              // 末波强制判定倒计时（回合数）
        public List<WavePromotion> promotions = new List<WavePromotion>(); // 升变预告：本波第 N 个棋子将在下一波升变（旧机制）
        public bool autoPromote;              // 自动预告模式（2026-08-19）：本波第 1 回合结束后预告敌方场上离中心最近 2 个棋子，第 3 回合开始自动升变为随机升变棋子（RandomManager）
    }

    /// <summary>波次升变预告配置（波次部署时对指定棋子添加预告——下波次升变）。</summary>
    [Serializable]
    public class WavePromotion
    {
        public int pieceIndexInWave; // 本波阵容第几个（0 起）
        public int toDefId;          // 升变目标 Def id
    }
}
