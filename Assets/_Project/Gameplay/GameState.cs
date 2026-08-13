using System;
using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;
using UnityEngine;
using Newtonsoft.Json;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 游戏状态（唯一状态源）。⚠️ 所有字段 internal set——程序集内只有 Resolver 写（落账纪律，
    /// 靠 internal + 日志 + 回放断言保证）。序列化经 DTO（快照契约 ISnapshot）。
    /// </summary>
    public class GameState : BaseManager<GameState>, ISnapshot
    {
        // ========== 战斗 ==========
        public BattlePhase Phase { get; internal set; }
        public int TurnCount { get; internal set; }
        public Dictionary<Vector2Int, PieceInstance> Pieces { get; internal set; } = new Dictionary<Vector2Int, PieceInstance>();
        public HashSet<Vector2Int> Obstacles { get; internal set; } = new HashSet<Vector2Int>(); // 障碍物格标记（直射阻挡/移动阻挡）
        private int _nextPieceId = 1;
        public Dictionary<int, PieceInstance> PiecesById { get; internal set; } = new Dictionary<int, PieceInstance>();

        // ========== 玩家 ==========
        public List<int> Hand { get; internal set; } = new List<int>();      // 手牌（Def id 列表，最多 12）
        public List<int> Graveyard { get; internal set; } = new List<int>(); // 墓地（不算手牌）
        public int PlayerAP { get; internal set; }
        public int PlayerAPMax { get; internal set; } = 2; // 初始 2、每回合回满、回合末清零
        public int PlayerScore { get; internal set; }

        // ========== 敌方 ==========
        public List<int> EnemyWavePool { get; internal set; } = new List<int>(); // 波次池（加牌落点）
        public int EnemyAP { get; internal set; }
        public int EnemyAPMax { get; internal set; } = 3;
        public int EnemyScore { get; internal set; }

        // ========== 程序 ==========
        public Dictionary<int, List<Template>> CurrentPrograms { get; internal set; } = new Dictionary<int, List<Template>>(); // ② 种类级表（只存编辑差异）
        public HashSet<int> EditingDefs { get; internal set; } = new HashSet<int>(); // 编辑态标记（实时编辑——防半截程序进战斗）

        // ========== 局内 ==========
        public List<RelicDef> Relics { get; internal set; } = new List<RelicDef>();
        public List<int> WaveScores { get; internal set; } = new List<int>();     // 每波得分（第 3 关"每波达标"）
        public List<PromoteAnnouncement> PromoteAnnouncements { get; internal set; } = new List<PromoteAnnouncement>();
        public int WaveEndCountdown { get; internal set; } = -1;                  // 末波强制判定倒计时（-1=未启用）
        public string CurrentEventId { get; internal set; }
        public List<string> DrawnEventIds { get; internal set; } = new List<string>();
        public HashSet<int> FreeExecutes { get; internal set; } = new HashSet<int>(); // 免费执行资格（额外行动：击杀触发——下次执行该棋子不扣 AP，用掉移除；有效期待策划拍板——当前保留到使用为止）

        // ========== 爬塔 ==========
        public int CurrentFloor { get; internal set; }
        public int CurrentNodeIndex { get; internal set; }
        public List<NodeState> NodeStates { get; internal set; } = new List<NodeState>();

        // ========== 回放 ==========
        public List<ConcreteAction> ReplayLog { get; internal set; } = new List<ConcreteAction>();

        // ========== 查询（只读，供 BoardRules/UI）==========

        public PieceInstance GetPiece(int pieceId) => PiecesById.TryGetValue(pieceId, out var p) ? p : null;

        public PieceInstance GetPieceAt(Vector2Int cell) => Pieces.TryGetValue(cell, out var p) ? p : null;

        public bool TryGetCurrentProgram(int defId, out List<Template> program)
        {
            return CurrentPrograms.TryGetValue(defId, out program);
        }

        /// <summary>分配新 pieceId（唯一）。</summary>
        public int AllocatePieceId()
        {
            return _nextPieceId++;
        }

        /// <summary>
        /// 玩家判负（无己方棋子 且 无手牌——仅玩家侧；敌方是 AI 测试员不吃此规则）。
        /// ⚠️ 2026-08-13：原 `Pieces.Count == 0` 未按阵营过滤——玩家被清盘+手牌打光时敌方在场不判负，
        /// 只能空过回合等末波兜底（延迟失败）。改为按 side==Player 过滤（"棋盘无棋"=玩家的棋——架构原意）。
        /// </summary>
        public bool IsPlayerDefeated()
        {
            if (Hand.Count > 0)
            {
                return false; // 还有牌能部署——不判负
            }
            foreach (var piece in Pieces.Values)
            {
                if (piece.side == Side.Player)
                {
                    return false; // 还有己方棋子——不判负
                }
            }
            return true;
        }

        /// <summary>敌方波次池增强（加牌落点：玩家→手牌，敌方→波次池）。</summary>
        public void AddToEnemyWavePool(int defId)
        {
            EnemyWavePool.Add(defId);
        }

        /// <summary>整局重置（失败/通关 → 回塔底；下局从默认程序重新开始）。</summary>
        public void ResetForNewRun()
        {
            Phase = default;
            TurnCount = 0;
            Pieces.Clear();
            Obstacles.Clear();
            PiecesById.Clear();
            Hand.Clear();
            Graveyard.Clear();
            PlayerAP = 0;
            PlayerScore = 0;
            EnemyWavePool.Clear();
            EnemyAP = 0;
            EnemyScore = 0;
            CurrentPrograms.Clear();
            EditingDefs.Clear();
            Relics.Clear();
            WaveScores.Clear();
            PromoteAnnouncements.Clear();
            WaveEndCountdown = -1;
            CurrentEventId = null;
            DrawnEventIds.Clear();
            FreeExecutes.Clear();
            CurrentFloor = 0;
            CurrentNodeIndex = 0;
            NodeStates.Clear();
            ReplayLog.Clear();
            _nextPieceId = 1;

            // 初始手牌 = 基础牌组：全部已注册棋子（当前 12 个 = 4 初始 + 4 部署 + 4 升变——构筑事件后续限定）
            foreach (var def in ConfigTable.All<PieceDef>())
            {
                Hand.Add(def.Id);
            }
        }

        /// <summary>
        /// 战斗态重置（每场战斗开始时调用——与 ResetForNewRun 整局重置区分）。
        /// ⚠️ 2026-08-13：跨战斗的战斗态此前从未重置（第 1 层是末层掩盖了问题）——胜利推进下一场战斗时
        /// TurnCount 继承（波次瞬发）/棋盘继承（残局）/波次分继承（结算数据串）。
        /// 清：每场战斗重来的字段；留：整局积累的字段（手牌/积分/遗物/塔进度/回放——局内持久）。
        /// 注：积分（PlayerScore/EnemyScore）当前保留（跨战斗累计）——语义待策划确认（待确认清单⑥），确认后调整。
        /// </summary>
        public void ResetForBattle()
        {
            Phase = BattlePhase.Placement;
            TurnCount = 0;
            Pieces.Clear();
            PiecesById.Clear();
            Obstacles.Clear();
            _nextPieceId = 1;
            PlayerAP = 0;
            EnemyAP = 0;
            WaveScores.Clear();
            PromoteAnnouncements.Clear();
            WaveEndCountdown = -1;
        }

        // ========== ISnapshot（经 DTO——Vector2Int/PieceDef 引用不可直接序列化）==========

        public string Key => "GameState";

        public string ToJson()
        {
            var dto = new GameStateDto
            {
                Phase = Phase,
                TurnCount = TurnCount,
                NextPieceId = _nextPieceId,
                PlayerAP = PlayerAP,
                PlayerAPMax = PlayerAPMax,
                PlayerScore = PlayerScore,
                EnemyAP = EnemyAP,
                EnemyAPMax = EnemyAPMax,
                EnemyScore = EnemyScore,
                Hand = new List<int>(Hand),
                Graveyard = new List<int>(Graveyard),
                EnemyWavePool = new List<int>(EnemyWavePool),
                CurrentPrograms = CurrentPrograms,
                EditingDefs = new List<int>(EditingDefs),
                Relics = Relics.ConvertAll(r => r.Id),
                WaveScores = new List<int>(WaveScores),
                PromoteAnnouncements = PromoteAnnouncements,
                WaveEndCountdown = WaveEndCountdown,
                CurrentEventId = CurrentEventId,
                DrawnEventIds = new List<string>(DrawnEventIds),
                FreeExecutes = new List<int>(FreeExecutes),
                CurrentFloor = CurrentFloor,
                CurrentNodeIndex = CurrentNodeIndex,
                NodeStates = new List<NodeState>(NodeStates),
                ReplayLog = ReplayLog,
                Obstacles = new List<Vector2Int>(Obstacles),
            };
            foreach (var piece in PiecesById.Values)
            {
                dto.Pieces.Add(new PieceDto
                {
                    Id = piece.Id,
                    DefId = piece.DefId,
                    Side = piece.side,
                    Durability = piece.durability,
                    X = piece.position.x,
                    Y = piece.position.y,
                    Facing = piece.facing,
                    ProgramOverride = piece.programOverride,
                    TempAbilities = piece.tempAbilities.ConvertAll(a => a.Id),
                    IsDeployed = piece.isDeployed,
                    ShieldCount = piece.shieldCount,
                    WaveIndex = piece.waveIndex, // 波次标（2026-08-13 补——原 DTO 缺字段，读档后每波得分链路断）
                });
            }
            // TypeNameHandling.Auto：多态基类（Template/ConcreteAction）序列化需写类型名，否则反序列化丢失子类
            return JsonConvert.SerializeObject(dto, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
        }

        public void FromJson(string json)
        {
            var dto = JsonConvert.DeserializeObject<GameStateDto>(json, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
            Phase = dto.Phase;
            TurnCount = dto.TurnCount;
            _nextPieceId = dto.NextPieceId;
            PlayerAP = dto.PlayerAP;
            PlayerAPMax = dto.PlayerAPMax;
            PlayerScore = dto.PlayerScore;
            EnemyAP = dto.EnemyAP;
            EnemyAPMax = dto.EnemyAPMax;
            EnemyScore = dto.EnemyScore;
            Hand = dto.Hand ?? new List<int>();
            Graveyard = dto.Graveyard ?? new List<int>();
            EnemyWavePool = dto.EnemyWavePool ?? new List<int>();
            CurrentPrograms = dto.CurrentPrograms ?? new Dictionary<int, List<Template>>();
            EditingDefs = dto.EditingDefs != null ? new HashSet<int>(dto.EditingDefs) : new HashSet<int>();
            Relics = new List<RelicDef>();
            if (dto.Relics != null)
            {
                foreach (var relicId in dto.Relics)
                {
                    var relic = ConfigTable.Find<RelicDef>(relicId);
                    if (relic != null)
                    {
                        Relics.Add(relic);
                    }
                }
            }
            WaveScores = dto.WaveScores ?? new List<int>();
            PromoteAnnouncements = dto.PromoteAnnouncements ?? new List<PromoteAnnouncement>();
            WaveEndCountdown = dto.WaveEndCountdown;
            CurrentEventId = dto.CurrentEventId;
            DrawnEventIds = dto.DrawnEventIds ?? new List<string>();
            FreeExecutes = dto.FreeExecutes != null ? new HashSet<int>(dto.FreeExecutes) : new HashSet<int>();
            CurrentFloor = dto.CurrentFloor;
            CurrentNodeIndex = dto.CurrentNodeIndex;
            NodeStates = dto.NodeStates ?? new List<NodeState>();
            ReplayLog = dto.ReplayLog ?? new List<ConcreteAction>();
            Obstacles = dto.Obstacles != null ? new HashSet<Vector2Int>(dto.Obstacles) : new HashSet<Vector2Int>();
            Pieces.Clear();
            PiecesById.Clear();
            if (dto.Pieces != null)
            {
                foreach (var pdto in dto.Pieces)
                {
                    // ⚠️ 2026-08-13 读档健壮性：原 ConfigTable.Get（查不到抛异常崩读档）——改 Find（配置缺失跳过该棋子+警告）
                    var pieceDef = ConfigTable.Find<PieceDef>(pdto.DefId);
                    if (pieceDef == null)
                    {
                        UnityEngine.Debug.LogWarning($"[GameState] 读档：棋子配置缺失 DefId={pdto.DefId}——跳过该棋子");
                        continue;
                    }
                    var piece = new PieceInstance(pieceDef, pdto.Side, new Vector2Int(pdto.X, pdto.Y))
                    {
                        Id = pdto.Id,
                        durability = pdto.Durability,
                        facing = pdto.Facing,
                        programOverride = pdto.ProgramOverride,
                        isDeployed = pdto.IsDeployed,
                        shieldCount = pdto.ShieldCount,
                        waveIndex = pdto.WaveIndex, // 波次标（2026-08-13 补：第 3 关每波得分依赖——原 DTO 缺字段读档归 -1）
                    };
                    foreach (var abilityId in pdto.TempAbilities)
                    {
                        var ability = ConfigTable.Find<SpecialAbilityDef>(abilityId);
                        if (ability != null)
                        {
                            piece.tempAbilities.Add(ability);
                        }
                    }
                    Pieces[piece.position] = piece;
                    PiecesById[piece.Id] = piece;
                }
            }
        }
    }

    /// <summary>敌方升变预告（波次 N 开始预告波次 N+1；目标 = promotionConfigId 指向的升变版）。</summary>
    [Serializable]
    public class PromoteAnnouncement
    {
        public int pieceId;
        public int newDefId;
        public int countdown; // 剩余波次数（到 0 升变）
    }

    [Serializable]
    public class GameStateDto
    {
        public BattlePhase Phase;
        public int TurnCount;
        public int NextPieceId;
        public int PlayerAP;
        public int PlayerAPMax;
        public int PlayerScore;
        public int EnemyAP;
        public int EnemyAPMax;
        public int EnemyScore;
        public List<int> Hand;
        public List<int> Graveyard;
        public List<int> EnemyWavePool;
        public Dictionary<int, List<Template>> CurrentPrograms;
        public List<int> EditingDefs;
        public List<int> Relics;
        public List<int> WaveScores;
        public List<PromoteAnnouncement> PromoteAnnouncements;
        public int WaveEndCountdown;
        public string CurrentEventId;
        public List<string> DrawnEventIds;
        public List<int> FreeExecutes;
        public int CurrentFloor;
        public int CurrentNodeIndex;
        public List<NodeState> NodeStates;
        public List<ConcreteAction> ReplayLog;
        public List<Vector2Int> Obstacles;
        public List<PieceDto> Pieces = new List<PieceDto>();
    }

    [Serializable]
    public class PieceDto
    {
        public int Id;
        public int DefId;
        public Side Side;
        public int Durability;
        public int X;
        public int Y;
        public Facing Facing;
        public List<Template> ProgramOverride;
        public List<int> TempAbilities;
        public bool IsDeployed;
        public int ShieldCount;
        public int WaveIndex; // 所属波次（2026-08-13 补——每波得分按此累计）
    }
}
