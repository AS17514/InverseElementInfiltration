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
        public bool scoreDeductEnabled;                        // 敌方击杀扣分开关（2026-08-20：我方棋子被敌方棋子击败 → 本关总得分 - 价值；第 3/4 关配置启用——策划口述）
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
        public int waveScoreTarget;           // 每波达标线（2026-08-19 计分规则：该波次结算得分之和达标线；0=未配置——第 3 关旧骨架每波>0；数值策划回填）
        // 2026-08-26 多部署组（策划第 2-4 关新规则：同波多组不同池/数量/区域——如关 4 波 2 = 部署区 6 个 + 非部署区 2 个）
        public List<WaveGroupDef> groups = new List<WaveGroupDef>(); // 空 = 沿用顶层单组字段（随机池/阵容/数量 + positions）——向后兼容
        public bool randomCells;              // 2026-08-26 随机空格部署（策划："敌方部署区随机 N 个空格"——区域内空格随机抽 N，RandomManager 可复现；false = 固定站位/顺序找位）
        public int spawnShield;               // 2026-08-26 波次部署棋子上场时额外获得护盾数（关 4 波 3 "额外获得护盾1"——挂 PieceInstance.tempShield）
    }

    /// <summary>波次部署组（2026-08-26 多组部署：同波拆组——每组独立池/数量/区域；得分仍归本波，不拆评分波次）。</summary>
    [Serializable]
    public class WaveGroupDef
    {
        public bool randomPool;                               // true = 从 poolType 类随机抽 count 个（可重复）；false = pieceDefIds 固定阵容
        public PieceType poolType;                            // 随机池种类（Initial/Deployable）
        public int count;                                     // 随机抽取数量
        public List<int> pieceDefIds = new List<int>();       // 固定阵容
        public DeployArea deployArea;                         // 部署区域（enemy-deploy 敌方部署区 / midfield 非双方部署区）
    }

    /// <summary>部署区域（2026-08-26：波次部署位置约束——策划"敌方部署区随机空格"/"非双方部署区随机空格"）。</summary>
    public enum DeployArea
    {
        EnemyDeploy,  // 敌方部署区（最上 2 行 y6~7——FindDeployCell 现状区域）
        Midfield,     // 非双方部署区（中间 y2~5）
    }

    /// <summary>波次升变预告配置（波次部署时对指定棋子添加预告——下波次升变）。</summary>
    [Serializable]
    public class WavePromotion
    {
        public int pieceIndexInWave; // 本波阵容第几个（0 起）
        public int toDefId;          // 升变目标 Def id
    }
}
