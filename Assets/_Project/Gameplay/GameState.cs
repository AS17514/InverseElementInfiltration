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
        private int _nextCardId = 1; // 牌实例 id 计数器（2026-08-21——跨战斗唯一；入存档，读档恢复）
        public Dictionary<int, PieceInstance> PiecesById { get; internal set; } = new Dictionary<int, PieceInstance>();

        // ========== 诊断（2026-08-21——只写不读；超时降级等异常留痕，存档可查，不参与游戏逻辑）==========
        /// <summary>表现回执超时记录（环形缓冲——上限 MaxTimeoutRecords）。</summary>
        public List<TimeoutRecord> PresentationTimeouts { get; internal set; } = new List<TimeoutRecord>();
        public const int MaxTimeoutRecords = 20;

        // ========== 玩家 ==========
        /// <summary>手牌（牌列表——棋子牌 Card(defId) / 麻将牌 Card(value)——抽牌堆抽出；2026-08-20 牌结构改造）。</summary>
        public List<Card> Hand { get; internal set; } = new List<Card>();
        public List<int> Graveyard { get; internal set; } = new List<int>(); // 墓地（不算手牌）

        // ========== 牌去向记录区（2026-08-24 用户定案：弃牌区=墓地同一概念[出处：游戏方案描述 §4"死亡棋子进墓地/弃牌堆"]；麻将池独立、升变替换池——记录留痕，不参与规则）==========
        // 语义：棋子牌死亡→墓地（Graveyard，记死亡时 defId=升变后形态）；场上原牌被升变替换→升变替换池（仅玩家侧，敌方无"牌"概念）；
        //      麻将摸切/打出墙体→使用池（实际那张牌——含实例 id）；墙体被破坏→从使用池转移至死亡池（死亡池与棋子牌墓地独立）。
        /// <summary>升变替换池：场上原牌被升变替换（原牌 defId+属性——仅玩家侧；升变后的新牌死亡仍进墓地）。</summary>
        private readonly List<Card> _promotedReplaced = new List<Card>();
        /// <summary>麻将使用池：摸切/打出墙体（记录实际那张麻将牌——含实例 id；打出=已使用[在场上也算使用]）。</summary>
        private readonly List<Card> _mahjongUsed = new List<Card>();
        /// <summary>麻将死亡池：墙体被破坏（从使用池转移——与棋子牌墓地独立）。</summary>
        private readonly List<Card> _mahjongDead = new List<Card>();

        /// <summary>弃牌区大容器（2026-08-24 设计定稿：不改原区域[Graveyard/三池保留]，新增权威 Card 化弃牌存储——棋子死亡区 + 麻将死亡区，粒度细分供代币购买/前端展示查询）。</summary>
        public DiscardZone Discard { get; } = new DiscardZone();

        /// <summary>升变替换池只读视图。</summary>
        public IReadOnlyList<Card> PromotedReplacedPile => _promotedReplaced;
        /// <summary>麻将使用池只读视图。</summary>
        public IReadOnlyList<Card> MahjongUsedPile => _mahjongUsed;
        /// <summary>麻将死亡池只读视图。</summary>
        public IReadOnlyList<Card> MahjongDeadPile => _mahjongDead;

        /// <summary>记录升变替换（原牌进替换池——受限写：append-only，无不变式）。</summary>
        public void RecordPromotedReplaced(Card card) => _promotedReplaced.Add(card);
        /// <summary>记录麻将使用（摸切/打出墙体——append-only；非麻将牌忽略）。</summary>
        public void RecordMahjongUsed(Card card) { if (card.IsMahjong) _mahjongUsed.Add(card); }
        /// <summary>记录麻将死亡（墙体破坏——append-only；与棋子牌墓地独立）。</summary>
        public void RecordMahjongDead(Card card) { if (card.IsMahjong) _mahjongDead.Add(card); }

        /// <summary>
        /// 麻将使用池 → 死亡池转移（2026-08-24：墙体破坏时调用——先按 instanceId 精确；
        /// 找不到（0=旧档墙体/防御）兜底按点数——同点数等价，转移语义一致）。返回是否转移成功。
        /// </summary>
        public bool MoveMahjongUsedToDead(int instanceId, int value)
        {
            int idx = _mahjongUsed.FindIndex(c => c.instanceId == instanceId);
            if (idx < 0) idx = _mahjongUsed.FindIndex(c => c.IsMahjong && c.value == value);
            if (idx < 0) return false;
            var card = _mahjongUsed[idx];
            _mahjongUsed.RemoveAt(idx);
            _mahjongDead.Add(card);
            return true;
        }

        /// <summary>抽牌堆（2026-08-19 策划确认新概念）：构筑牌组【部署/升变】棋子 + 麻将玩法 18 张麻将牌；第一回合自动抽 4 + 1 AP 抽 1 行动。</summary>
        public List<Card> DrawPile { get; internal set; } = new List<Card>();
        public int PlayerAP { get; internal set; }
        public int PlayerAPMax { get; internal set; } = 1; // ⚠️ 2026-08-19：策划新案确认初始上限 1（能力可增加）；原 2
        /// <summary>总得分（2026-08-19 计分规则：回合结算后的本关总得分——**不跨关累计**，ResetForBattle 清）。</summary>
        public int PlayerScore { get; internal set; }
        /// <summary>基础得分（2026-08-19 计分规则：敌方棋子被击败 → +该棋子价值；回合结束按 基础分×倍率 结算后清零）。</summary>
        public int BaseScore { get; internal set; }
        /// <summary>得分倍率（2026-08-19 计分规则：默认 1；特殊效果可修改——来源未设计，字段预留；结算后复位 1）。</summary>
        public int ScoreMultiplier { get; internal set; } = 1;

        // ========== 敌方 ==========
        public List<int> EnemyWavePool { get; internal set; } = new List<int>(); // 波次池（加牌落点）
        public int EnemyAP { get; internal set; }
        public int EnemyAPMax { get; internal set; } = 3;
        public int EnemyScore { get; internal set; }

        // ========== 程序 ==========
        public Dictionary<int, List<Template>> CurrentPrograms { get; internal set; } = new Dictionary<int, List<Template>>(); // ② 种类级表（只存编辑差异）
        public HashSet<int> EditingDefs { get; internal set; } = new HashSet<int>(); // 编辑态标记（实时编辑——防半截程序进战斗）

        // ========== 编辑会话（2026-08-19：两方案切换 + 编辑事件候选）==========
        /// <summary>hide 模式：本棋子被替换/移除的外部模块（候选区隐藏；存档字段——还原时清空恢复展示）。show 模式恒空。</summary>
        public Dictionary<int, List<Template>> HiddenModules { get; internal set; } = new Dictionary<int, List<Template>>();
        /// <summary>编辑事件三选一：未修改基础棋子候选（defId，三类型各 1——最多 3；编辑事件触发时抽取）。</summary>
        public List<int> EditCandidates { get; internal set; } = new List<int>();
        /// <summary>编辑事件外部候选模块（移动/攻击/效果各随机 2——RandomManager 种子相关；GetEditCandidates ①部分优先）。</summary>
        public List<Template> EditModuleCandidates { get; internal set; } = new List<Template>();

        // ========== 局内 ==========
        /// <summary>玩法激活集合（2026-08-20：本关已激活的玩法——"mahjong"/"element"等；改变规则事件选择玩法后加入；机制后议，先用配置/进关填入占位）。</summary>
        public HashSet<string> ActiveStyles { get; internal set; } = new HashSet<string>();
        /// <summary>E5 资格牌实例 id（2026-08-23 高亮资格式定案：抽到被编辑棋牌时授予；玩家打出该牌=免费+立即执行，其他行动/回合结束=取消；0=无资格）。入档（战斗中途存档一致）。</summary>
        public int EditedCardQualifyId { get; internal set; }

        // ========== 麻将玩法（2026-08-20——玩法机制区）==========
        /// <summary>牌山（麻将玩法：数字队列——最多 2 个；敌方棋子被击败/己方麻将牌被破坏/摸切填入；第 3 个填入时先判定刻子/顺子）。</summary>
        public List<int> MahjongScore { get; internal set; } = new List<int>();
        /// <summary>番数（麻将玩法：刻子/顺子成形 → +1；和牌消耗清空）。</summary>
        public int FanCount { get; internal set; }
        /// <summary>麻将墙体（2026-08-20：格 → 点数——1×2 竖两格各记一条；攻击命中墙体格 → 整墙破坏 + 填牌山点数 + 基础分 +1；阻挡移动穿过/直射路径——与 Obstacles 合并判定）。</summary>
        public Dictionary<Vector2Int, ObstacleData> MahjongWalls { get; internal set; } = new Dictionary<Vector2Int, ObstacleData>();

        // ========== 玩法·骰子（2026-08-24 设计定稿——仅玩家侧）==========
        /// <summary>当前点数（0=未投掷；投掷=执行类行动 1 AP → 随机 1~6 + 基础分；保留到下次投掷或被"点数直线移动"消耗）。</summary>
        public int DiceValue { get; internal set; }
        /// <summary>骰子移动待执行（全场 buff：消耗点数启动 → 下次点某棋子执行时重定向为点数步直线移动；其他行动不取消；**不跨回合**）。</summary>
        public bool DiceMovePending { get; internal set; }
        /// <summary>待执行移动步数（启动时点数额）。</summary>
        public int DiceMoveSteps { get; internal set; }

        // ========== 玩法·围棋（2026-08-24 设计定稿——仅玩家侧）==========
        /// <summary>上次部署颜色（默认 Player=蓝——首次部署蓝；每次部署切换；战斗边界清 → 新战斗首次蓝）。</summary>
        public Side GoLastColor { get; internal set; }
        /// <summary>是否部署过围棋（首次=蓝；之后按 GoLastColor 切换）。</summary>
        public bool GoEverDeployed { get; internal set; }
        /// <summary>本回合围棋已部署次数（回合开始重置；速攻能力 → 上限 2）。</summary>
        public int GoDeployCount { get; internal set; }
        /// <summary>买子购买的额外部署次数（2026-08-24 能力「买子」：固定 2 币 +1——当回合有效，回合开始清；GO 部署容量 = 免费限次 + 本值）。</summary>
        public int GoExtraDeploys { get; internal set; }
        /// <summary>围棋价值加成（2026-08-24 能力「升值」：每次部署围棋 → 全场围棋价值+1 累计；**战斗级**——ResetForBattle 复原，新战斗归 0）。</summary>
        public int GoValueBonus { get; internal set; }

        // ========== 玩法·代币（2026-08-24 设计定稿——仅玩家侧；**不跨战斗**）==========
        /// <summary>代币（初始 0；每回合开始 +1；购买消耗——不跨战斗：ResetForBattle 清）。</summary>
        public int TokenCount { get; internal set; }

        // ========== 能力「宝牌」（2026-08-24 能力池 P1——整局级）==========
        /// <summary>宝牌数字（1-9 选中；0=未选——获得能力后经前端数字选择面板写入；判定"数字对应价值的牌"）。</summary>
        public int BaopaiNumber { get; internal set; }

        // ========== 能力「震击」（2026-08-24 能力池 P3——战斗级）==========
        /// <summary>震击墙（2 个——开局非部署区随机生成，不可破坏；攻击命中 → 周围 8 格敌我双方受固定 1 伤害；并入 IsBlocked 阻挡）。</summary>
        public HashSet<Vector2Int> ShockWalls { get; internal set; } = new HashSet<Vector2Int>();

        public List<RelicDef> Relics { get; internal set; } = new List<RelicDef>();
        /// <summary>能力事件三选一候选（2026-08-22：当前能力事件展示的 3 个候选——词条过滤随机抽取；事件进行中入档）。</summary>
        public List<RelicDef> AbilityCandidates { get; internal set; } = new List<RelicDef>();
        /// <summary>能力候选刷新次数（2026-08-22：每项各可刷新 1 次——与 AbilityCandidates 顺序对应）。</summary>
        public List<int> AbilityRefreshLeft { get; internal set; } = new List<int>();
        /// <summary>玩法事件二选一候选（2026-08-24 玩法选择机制：未激活玩法随机抽 2——玩家选 1 激活；落选保留[后续可再出现]；事件进行中入档——中断续玩一致）。</summary>
        public List<string> RuleCandidates { get; internal set; } = new List<string>();
        /// <summary>行动经济已行动集（2026-08-22：ActionEconomy 激活时——本回合已执行过行动的棋子——回合级，回合开始重置；额外行动穿透不查此集）。</summary>
        public HashSet<int> ActionEconomyActed { get; internal set; } = new HashSet<int>();
        /// <summary>本层模块消耗（净增量——2026-08-23 决策 4 定案"消耗制=池子构成规则"）：
        /// key=类型名:id（外部模块）；值=本层净放入次数。放入 +1（候选消失）；撤销/移除 -1（=0 移除键——候选恢复）；
        /// EnterFloor 进层清空（跨层复原——池子每层完整，上层用过的模块可再抽）。入档（中断续玩一致）。</summary>
        public Dictionary<string, int> ConsumedModules { get; internal set; } = new Dictionary<string, int>();
        /// <summary>排查诊断（2026-08-23 第二梯队——唯一写入口 LogDiagnostic：内部判开关 + 环形上限；私有防外部误清/直改）。</summary>
        private readonly List<string> _diagnosticLog = new List<string>();
        private const int DiagnosticLogCap = 5000; // 环形上限：防长局/循环下存档无界膨胀
        /// <summary>追加排查诊断（唯一写入口——开关判定与上限在内部；默认关零开销）。</summary>
        public void LogDiagnostic(string message)
        {
            if (!TheLaw.Core.Diagnostics.VerboseEnabled) return;
            if (_diagnosticLog.Count >= DiagnosticLogCap) _diagnosticLog.RemoveAt(0); // 删最旧保上限
            _diagnosticLog.Add(message);
        }
        /// <summary>诊断只读视图（存档序列化用内部值；外部读取防边读边改）。</summary>
        public System.Collections.Generic.IReadOnlyList<string> DiagnosticLogView => _diagnosticLog;
        public List<int> WaveScores { get; internal set; } = new List<int>();     // 每波得分（第 3 关"每波达标"）
        public List<PromoteAnnouncement> PromoteAnnouncements { get; internal set; } = new List<PromoteAnnouncement>();
        public int WaveEndCountdown { get; internal set; } = -1;                  // 末波强制判定倒计时（-1=未启用）
        public string CurrentEventId { get; internal set; }
        public List<string> DrawnEventIds { get; internal set; } = new List<string>();
        public HashSet<int> FreeExecutes { get; internal set; } = new HashSet<int>(); // 免费执行资格（额外行动：击杀触发——下次执行该棋子不扣 AP，用掉移除；有效期待策划拍板——当前保留到使用为止）

        // ========== 爬塔 ==========
        public int CurrentFloor { get; internal set; }
        /// <summary>当前关卡配置（FloorConfig 引用——2026-08-20 进层时 TowerFlow 设置；运行时只读，不入存档——续档经 EnterFloor 重设）。</summary>
        public FloorConfig CurrentFloorConfig { get; internal set; }
        public int CurrentNodeIndex { get; internal set; }
        public List<NodeState> NodeStates { get; internal set; } = new List<NodeState>();

        // ========== 回放 ==========
        public List<ConcreteAction> ReplayLog { get; internal set; } = new List<ConcreteAction>();

        // ========== 查询（只读，供 BoardRules/UI）==========

        /// <summary>玩法是否激活（2026-08-20：麻将"mahjong"/属性"element"）。</summary>
        public bool IsStyleActive(string style) => ActiveStyles != null && ActiveStyles.Contains(style);

        /// <summary>
        /// 是否持有指定基础效果（2026-08-22：能力=遗物效果组合——遍历持有遗物；供统一入口挂点查询）。
        /// </summary>
        public bool HasRelicEffect(RelicEffectType type)
        {
            foreach (var relic in Relics)
            {
                if (relic == null) continue;
                foreach (var e in relic.effects)
                {
                    if (e != null && e.type == type) return true;
                }
            }
            return false;
        }

        /// <summary>行动经济激活（ActionEconomy——执行不耗 AP + 每棋子每回合一次）。</summary>
        public bool ActionEconomyActive => HasRelicEffect(RelicEffectType.ActionEconomy);

        /// <summary>己方部署区行数加成（DeployRow——累加）。</summary>
        public int DeployRowBonus
        {
            get
            {
                int bonus = 0;
                foreach (var relic in Relics)
                {
                    if (relic == null) continue;
                    foreach (var e in relic.effects)
                    {
                        if (e != null && e.type == RelicEffectType.DeployRow) bonus += e.value;
                    }
                }
                return bonus;
            }
        }

        /// <summary>我方直射距离加成（2026-08-24 能力「强劲」：仅 DirectFire——与通用 AttackRange 修正并存叠加；玩家侧由 BoardRules 判别）。</summary>
        public int DirectFireRangeBonus
        {
            get
            {
                int bonus = 0;
                foreach (var relic in Relics)
                {
                    if (relic == null) continue;
                    foreach (var e in relic.effects)
                    {
                        if (e != null && e.type == RelicEffectType.DirectFireRange) bonus += e.value;
                    }
                }
                return bonus;
            }
        }

        /// <summary>围棋每回合部署次数上限（2026-08-24 能力「速攻」：1→2；规则单一来源——BattleFlow/Resolver 共用）。</summary>
        public int GoDeployLimit()
        {
            return HasRelicEffect(RelicEffectType.GoDeployExtra) ? 2 : 1;
        }

        /// <summary>围棋本回合部署容量 = 免费限次（速攻→2）+ 买子购买次数（2026-08-24 能力「买子」——当回合有效，回合开始清）。</summary>
        public int GoDeployCapacity()
        {
            return GoDeployLimit() + GoExtraDeploys;
        }

        /// <summary>追加诊断记录（2026-08-21：超时降级等——环形缓冲，只写不读；存档可查）。</summary>
        public void AppendTimeoutRecord(int sessionId, int actionId, int waitMs, string phase)
        {
            if (PresentationTimeouts == null) PresentationTimeouts = new List<TimeoutRecord>();
            PresentationTimeouts.Add(new TimeoutRecord
            {
                SessionId = sessionId,
                ActionId = actionId,
                WaitMs = waitMs,
                Phase = phase,
                At = System.DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            });
            if (PresentationTimeouts.Count > MaxTimeoutRecords)
            {
                PresentationTimeouts.RemoveAt(0); // 环形缓冲（保留最近 N 条——防存档膨胀）
            }
        }

        /// <summary>
        /// 棋盘格是否障碍（2026-08-20 统一入口：普通障碍 Obstacles ∪ 麻将墙体 MahjongWalls——决策记录_牌数据结构与玩法语义）。
        /// 移动阻挡 + 直射阻挡共用；以后新增障碍源 = 在此加一行，查询点收拢。
        /// </summary>
        public bool IsBlocked(Vector2Int cell) => Obstacles.Contains(cell) || (MahjongWalls != null && MahjongWalls.ContainsKey(cell))
            || (ShockWalls != null && ShockWalls.Contains(cell)); // 2026-08-24 能力「震击」墙：并入统一障碍判定（阻挡移动/直射）

        public PieceInstance GetPiece(int pieceId) => PiecesById.TryGetValue(pieceId, out var p) ? p : null;

        public PieceInstance GetPieceAt(Vector2Int cell) => Pieces.TryGetValue(cell, out var p) ? p : null;

        public bool TryGetCurrentProgram(int defId, out List<Template> program)
        {
            return CurrentPrograms.TryGetValue(defId, out program);
        }

        /// <summary>
        /// 生效程序（编辑差异优先 → Def 默认模组）——价值/类型推导的唯一状态源（2026-08-15 策划新案）。
        /// 与 PieceInstance.GetProgram 的三层查找同构（此处无实例覆盖层——覆盖是战斗内实例态）。
        /// </summary>
        public List<Template> GetEffectiveProgram(int defId)
        {
            if (TryGetCurrentProgram(defId, out var edited))
            {
                return edited; // ② 种类级表（编辑差异，入快照）
            }
            var def = ConfigTable.Find<PieceDef>(defId);
            if (def != null && def.programSet != null && def.programSet.Count > 0)
            {
                return def.programSet[0].slots; // ③ Def 默认模组
            }
            return null;
        }

        /// <summary>棋子当前类型（价值档位推导——编辑跨档即变种类；可推导不入快照）。</summary>
        public PieceType GetEffectiveType(int defId)
        {
            return PieceValue.GetType(GetEffectiveProgram(defId));
        }

        /// <summary>棋子当前价值（槽位价值总和推导——积分/构筑/选目标统一口径）。</summary>
        public int GetEffectiveValue(int defId)
        {
            return PieceValue.SumValue(GetEffectiveProgram(defId));
        }

        /// <summary>分配新 pieceId（唯一）。</summary>
        public int AllocatePieceId()
        {
            return _nextPieceId++;
        }

        /// <summary>分配新牌实例 id（2026-08-21——唯一；牌进入 Hand/DrawPile 统一入口调用）。</summary>
        public int AllocateCardId()
        {
            return _nextCardId++;
        }

        /// <summary>
        /// 读档后牌实例 id 兼容处理（2026-08-21）：旧档 Card 无 instanceId（全 0）或存档损坏重复 → 重新分配；
        /// 新档 id 已唯一则原样保留（回放按动作 id 消费依赖存档态一致）。
        /// </summary>
        private void ReAssignCardIdsAfterLoad()
        {
            var seen = new HashSet<int>();
            foreach (var list in new List<Card>[] { Hand, DrawPile })
            {
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c.instanceId <= 0 || !seen.Add(c.instanceId))
                    {
                        c.instanceId = AllocateCardId(); // 缺省/重复 → 重新分配
                        list[i] = c;
                    }
                }
            }
        }

        /// <summary>
        /// 玩家判负（无己方棋子 且 手牌空 且 抽牌堆空——仅玩家侧；敌方是 AI 测试员不吃此规则）。
        /// ⚠️ 2026-08-13：原 `Pieces.Count == 0` 未按阵营过滤——玩家被清盘+手牌打光时敌方在场不判负，
        /// 只能空过回合等末波兜底（延迟失败）。改为按 side==Player 过滤（"棋盘无棋"=玩家的棋——架构原意）。
        /// ⚠️ 2026-08-19（策划确认）：抽牌堆有牌 → 不判负——玩家仍可花 1 AP 抽牌翻盘（判负三条件：无棋+手牌空+抽牌堆空）。
        /// </summary>
        public bool IsPlayerDefeated()
        {
            if (Hand.Count > 0 || (DrawPile != null && DrawPile.Count > 0))
            {
                return false; // 还有牌能部署/能抽——不判负
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
            _promotedReplaced.Clear(); // 2026-08-24：升变替换池随整局重置（整局累计——ResetForBattle 不清）
            _mahjongUsed.Clear();      // 2026-08-24：麻将使用池随整局重置（与麻将牌跨战斗延续一致）
            _mahjongDead.Clear();      // 2026-08-24：麻将死亡池随整局重置
            Discard.Clear();           // 2026-08-24：弃牌区大容器随整局重置（整局累计——ResetForBattle 保留）
            DiceValue = 0;             // 2026-08-24：骰子点数随整局重置
            DiceMovePending = false;   // 2026-08-24：骰子移动 buff 随整局重置
            DiceMoveSteps = 0;
            TokenCount = 0;            // 2026-08-24：代币随整局重置（每局初始 0）
            GoLastColor = default;     // 2026-08-24：围棋颜色随整局重置（首次蓝）
            GoEverDeployed = false;
            GoDeployCount = 0;
            GoExtraDeploys = 0;        // 2026-08-24 能力「买子」：购买的部署次数随整局重置
            GoValueBonus = 0;          // 2026-08-24 能力「升值」：围棋价值加成随整局重置（战斗级——ResetForBattle 同清）
            DrawPile.Clear();
            PlayerAP = 0;
            PlayerAPMax = 1; // ⚠️ 2026-08-23 修复：AP 上限须随新局复位——此前漏复位（启动自动读档恢复旧档值 + 同进程多局累计 → 新局继承旧局能力叠加；实测第 5 局开局 5）；初始 1（L37）
            PlayerScore = 0;
            BaseScore = 0;
            ScoreMultiplier = 1;
            EnemyWavePool.Clear();
            EnemyAP = 0;
            EnemyAPMax = 3; // ⚠️ 2026-08-23 修复：敌方上限一并复位（此前漏——当前由 EnterFloor 按关覆盖无实际影响，一并清净；初始 3（L48））
            EnemyScore = 0;
            CurrentPrograms.Clear();
            EditingDefs.Clear();
            HiddenModules.Clear();
            EditCandidates.Clear();
            EditModuleCandidates.Clear();
            Relics.Clear();
            AbilityCandidates.Clear();   // 2026-08-22 能力候选（整局重置）
            AbilityRefreshLeft.Clear();
            RuleCandidates.Clear();      // 2026-08-24 玩法事件候选（整局重置——新局重新抽取）
            ActionEconomyActed.Clear();
            ConsumedModules.Clear(); // 2026-08-23：本层模块消耗随整局重置（层内由 EnterFloor 清——跨层复原）
            _diagnosticLog.Clear(); // 2026-08-23：排查诊断随整局重置
            WaveScores.Clear();
            PromoteAnnouncements.Clear();
            WaveEndCountdown = -1;
            CurrentEventId = null;
            DrawnEventIds.Clear();
            FreeExecutes.Clear();
            MahjongScore.Clear(); // 麻将状态随整局重置（ResetForBattle 同样清）
            PresentationTimeouts.Clear(); // 诊断随整局重置（2026-08-21——新局新诊断）
            FanCount = 0;
            MahjongWalls.Clear();
            ShockWalls.Clear();       // 2026-08-24 能力「震击」：墙随整局重置（战斗级——ResetForBattle 同清）
            ActiveStyles.Clear(); // 玩法激活随整局重置（跨关累积——ResetForBattle 不清）
            BaopaiNumber = 0;     // 2026-08-24 能力「宝牌」：数字随整局重置（0=未选；整局级——ResetForBattle 保留）
            EditedCardQualifyId = 0; // 2026-08-23：E5 资格随整局重置
            CurrentFloor = 0;
            CurrentNodeIndex = 0;
            NodeStates.Clear();
            ReplayLog.Clear();
            _nextCardId = 1; // 2026-08-21：牌实例 id 计数器随整局重置（保持现状）
            _nextPieceId = 1; // ⚠️ 2026-08-23 决策 1 修正：棋子 Id 改回"局内单调"——新局重新计数（状态清理已完备[死亡/战斗边界清资格/预告/已行动集]，Id 复用不再有串态风险；同进程连开多局也归 1；Continue 读档恢复沿用存档值不受影响）
            // 2026-08-23 决策 1：棋子实例 Id **全局单调递增**——不随新局/战斗重置
            // （NextPieceId 已入档 = 天然记录"上次用到的位置"；Id 永不重复 → 按 Id 存的状态不可能串到新棋子，
            // 与 _nextCardId 的业务面区分：棋子 Id 全局延续是本决策，牌 Id 保持原状）

            // 初始牌组分区：准备阶段只持有初始棋子；部署/升变棋子预先进入抽牌堆。
            // 首个玩家回合 StartPlayerTurn 自动从 DrawPile 抽 4 张，后续再按 AP 抽牌。
            foreach (var def in ConfigTable.All<PieceDef>())
            {
                if (GetEffectiveType(def.Id) == PieceType.Initial) Hand.Add(Card.Piece(def.Id));
                else DrawPile.Add(Card.Piece(def.Id));
            }
            ReAssignCardIdsAfterLoad(); // 2026-08-21：新局初始化分配牌实例 id（Card.Piece 默认 0——统一分配）
        }

        /// <summary>
        /// 战斗态重置（每场战斗开始时调用——与 ResetForNewRun 整局重置区分）。
        /// ⚠️ 2026-08-13：跨战斗的战斗态此前从未重置（第 1 层是末层掩盖了问题）——胜利推进下一场战斗时
        /// TurnCount 继承（波次瞬发）/棋盘继承（残局）/波次分继承（结算数据串）。
        /// 清：每场战斗重来的字段；留：整局积累的字段（手牌/遗物/塔进度/回放——局内持久）。
        /// ⚠️ 2026-08-19（策划确认）：积分**不跨关累计**——PlayerScore/EnemyScore/BaseScore/ScoreMultiplier 每关清
        /// （本关从 0 开始；原"跨战斗保留"注释作废——待确认清单⑥已答）。
        /// </summary>
        public void ResetForBattle()
        {
            Phase = BattlePhase.Placement;
            TurnCount = 0;
            Pieces.Clear();
            PiecesById.Clear();
            Obstacles.Clear();
            // 2026-08-23 决策 1：_nextPieceId 不随战斗重置——棋子 Id 全局单调（防 Id 复用串状态）
            PlayerAP = 0;
            EnemyAP = 0;
            PlayerScore = 0;
            EnemyScore = 0;
            BaseScore = 0;
            ScoreMultiplier = 1;
            WaveScores.Clear();
            PromoteAnnouncements.Clear();
            WaveEndCountdown = -1;
            FreeExecutes.Clear();      // 2026-08-23：免费执行资格属战斗内（击杀授予）——跨战斗必须清（_nextPieceId 重置后 Id 复用会串资格）
            ActionEconomyActed.Clear(); // 2026-08-23：行动经济已行动集属战斗内（玩家回合开始也会重置——战斗边界一并清，防 Id 复用串态）
            EditedCardQualifyId = 0;     // 2026-08-23：E5 资格属战斗内瞬态——战斗边界清
            // 2026-08-24 玩法状态（战斗级——每场战斗重置；玩法整局激活但机制状态战斗内有效）：
            DiceValue = 0;               // 骰子点数（新战斗重新投掷）
            DiceMovePending = false;     // 骰子移动 buff（战斗边界清）
            DiceMoveSteps = 0;
            TokenCount = 0;              // 代币不跨战斗（用户定案：每场战斗初始 0、每回合 +1）
            GoLastColor = default;       // 围棋颜色（新战斗首次蓝）
            GoEverDeployed = false;
            GoDeployCount = 0;           // 围棋部署次数（战斗边界清）
            GoExtraDeploys = 0;          // 2026-08-24 能力「买子」：购买次数战斗边界清（当回合有效）
            GoValueBonus = 0;            // 2026-08-24 能力「升值」：战斗级——新战斗复原（部署→+1 全场叠加）
            // 麻将玩法状态每关清（牌山/番数/墙体随战斗重置）
            MahjongScore.Clear();
            FanCount = 0;
            MahjongWalls.Clear();
            ShockWalls.Clear(); // 2026-08-24 能力「震击」：墙战斗级（新战斗重新生成）
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
                NextCardId = _nextCardId, // 2026-08-21：牌实例 id（读档恢复防撞车）
                PlayerAP = PlayerAP,
                PlayerAPMax = PlayerAPMax,
                PlayerScore = PlayerScore,
                BaseScore = BaseScore,
                ScoreMultiplier = ScoreMultiplier,
                EnemyAP = EnemyAP,
                EnemyAPMax = EnemyAPMax,
                EnemyScore = EnemyScore,
                Hand = new List<Card>(Hand),
                Graveyard = new List<int>(Graveyard),
                PromotedReplacedPile = new List<Card>(_promotedReplaced), // 2026-08-24 升变替换池
                MahjongUsedPile = new List<Card>(_mahjongUsed),           // 2026-08-24 麻将使用池
                MahjongDeadPile = new List<Card>(_mahjongDead),           // 2026-08-24 麻将死亡池
                DiscardPieceDeaths = new List<Card>(Discard.PieceDeaths),   // 2026-08-24 弃牌区·棋子死亡（Card 化）
                DiscardMahjongDeaths = new List<Card>(Discard.MahjongDeaths), // 2026-08-24 弃牌区·麻将死亡
                DiceValue = DiceValue, DiceMovePending = DiceMovePending, DiceMoveSteps = DiceMoveSteps, // 2026-08-24 骰子
                TokenCount = TokenCount, // 2026-08-24 代币
                GoLastColor = GoLastColor, GoDeployCount = GoDeployCount, // 2026-08-24 围棋
                GoEverDeployed = GoEverDeployed,
                GoExtraDeploys = GoExtraDeploys, // 2026-08-24 能力「买子」购买次数（当回合——入档一致）
                GoValueBonus = GoValueBonus,       // 2026-08-24 能力「升值」（战斗级——读档续战一致）
                BaopaiNumber = BaopaiNumber,       // 2026-08-24 能力「宝牌」数字（0=未选；整局级）
                DrawPile = new List<Card>(DrawPile),
                EnemyWavePool = new List<int>(EnemyWavePool),
                CurrentPrograms = CurrentPrograms,
                EditingDefs = new List<int>(EditingDefs),
                HiddenModules = HiddenModules,
                EditCandidates = new List<int>(EditCandidates),
                EditModuleCandidates = EditModuleCandidates,
                Relics = Relics.ConvertAll(r => r.Id),
                AbilityCandidates = AbilityCandidates.ConvertAll(r => r.Id), // 2026-08-22 能力事件候选（事件进行中入档）
                AbilityRefreshLeft = new List<int>(AbilityRefreshLeft),
                RuleCandidates = new List<string>(RuleCandidates), // 2026-08-24 玩法事件候选（事件进行中入档）
                WaveScores = new List<int>(WaveScores),
                PromoteAnnouncements = PromoteAnnouncements,
                WaveEndCountdown = WaveEndCountdown,
                CurrentEventId = CurrentEventId,
                DrawnEventIds = new List<string>(DrawnEventIds),
                FreeExecutes = new List<int>(FreeExecutes),
                ConsumedModules = ConsumedModules, // 2026-08-23：本层模块消耗（净增量——入档，中断续玩一致）
                DiagnosticLog = _diagnosticLog, // 2026-08-23：排查诊断（开时才非空）
                ActiveStyles = new List<string>(ActiveStyles),
                EditedCardQualifyId = EditedCardQualifyId, // 2026-08-23：E5 资格（0=无）
                PresentationTimeouts = PresentationTimeouts, // 2026-08-21 诊断（存档可查——只写不读）
                MahjongScore = new List<int>(MahjongScore),
                FanCount = FanCount,
                MahjongWalls = MahjongWalls,
                CurrentFloor = CurrentFloor,
                CurrentNodeIndex = CurrentNodeIndex,
                NodeStates = new List<NodeState>(NodeStates),
                ReplayLog = ReplayLog,
                Obstacles = new List<Vector2Int>(Obstacles),
                ShockWalls = new List<Vector2Int>(ShockWalls), // 2026-08-24 能力「震击」墙（战斗级——读档续战一致）
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
                    TempShield = piece.tempShield, // 2026-08-26 波次部署额外护盾（入档——续战一致）
                    WaveIndex = piece.waveIndex, // 波次标（2026-08-13 补——原 DTO 缺字段，读档后每波得分链路断）
                    Element = piece.element,     // 属性玩法（2026-08-20——读档恢复）
                    IsGo = piece.IsGo,           // 2026-08-24 围棋棋子
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
            _nextCardId = dto.NextCardId <= 0 ? 1 : dto.NextCardId; // 2026-08-21：旧档缺省 → 1（配合下方重分配）
            PlayerAP = dto.PlayerAP;
            PlayerAPMax = dto.PlayerAPMax;
            PlayerScore = dto.PlayerScore;
            BaseScore = dto.BaseScore;
            // ⚠️ 2026-08-19：倍率缺省防御——旧档缺字段（int 默认 0）→ 结算全 0；显式 clamp ≥1
            ScoreMultiplier = dto.ScoreMultiplier <= 0 ? 1 : dto.ScoreMultiplier;
            EnemyAP = dto.EnemyAP;
            EnemyAPMax = dto.EnemyAPMax;
            EnemyScore = dto.EnemyScore;
            Hand = dto.Hand ?? new List<Card>();
            Graveyard = dto.Graveyard ?? new List<int>();
            _promotedReplaced.Clear();
            if (dto.PromotedReplacedPile != null) _promotedReplaced.AddRange(dto.PromotedReplacedPile); // 2026-08-24（旧档缺省空）
            _mahjongUsed.Clear();
            if (dto.MahjongUsedPile != null) _mahjongUsed.AddRange(dto.MahjongUsedPile);                 // 2026-08-24（旧档缺省空）
            _mahjongDead.Clear();
            if (dto.MahjongDeadPile != null) _mahjongDead.AddRange(dto.MahjongDeadPile);                 // 2026-08-24（旧档缺省空）
            Discard.Load(dto.DiscardPieceDeaths, dto.DiscardMahjongDeaths); // 2026-08-24 弃牌区（旧档缺省空）
            DiceValue = dto.DiceValue; DiceMovePending = dto.DiceMovePending; DiceMoveSteps = dto.DiceMoveSteps; // 2026-08-24 骰子
            TokenCount = dto.TokenCount; // 2026-08-24 代币
            GoLastColor = dto.GoLastColor; GoDeployCount = dto.GoDeployCount; // 2026-08-24 围棋
            GoEverDeployed = dto.GoEverDeployed;
            GoExtraDeploys = dto.GoExtraDeploys; // 2026-08-24 能力「买子」（旧档缺省 0）
            GoValueBonus = dto.GoValueBonus; // 2026-08-24 能力「升值」（旧档缺省 0）
            BaopaiNumber = dto.BaopaiNumber; // 2026-08-24 能力「宝牌」（旧档缺省 0=未选）
            DrawPile = dto.DrawPile ?? new List<Card>();
            EnemyWavePool = dto.EnemyWavePool ?? new List<int>();
            ReAssignCardIdsAfterLoad(); // 2026-08-21：旧档兼容——牌实例 id 缺省 0 或重复 → 重分配（新档 id 已唯一则不动）
            CurrentPrograms = dto.CurrentPrograms ?? new Dictionary<int, List<Template>>();
            EditingDefs = dto.EditingDefs != null ? new HashSet<int>(dto.EditingDefs) : new HashSet<int>();
            HiddenModules = dto.HiddenModules ?? new Dictionary<int, List<Template>>();
            EditCandidates = dto.EditCandidates ?? new List<int>();
            EditModuleCandidates = dto.EditModuleCandidates ?? new List<Template>();
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
            AbilityCandidates = new List<RelicDef>();
            if (dto.AbilityCandidates != null)
            {
                foreach (var relicId in dto.AbilityCandidates)
                {
                    var relic = ConfigTable.Find<RelicDef>(relicId);
                    if (relic != null)
                    {
                        AbilityCandidates.Add(relic);
                    }
                }
            }
            AbilityRefreshLeft = dto.AbilityRefreshLeft ?? new List<int>();
            RuleCandidates = dto.RuleCandidates ?? new List<string>(); // 2026-08-24 玩法事件候选（旧档缺省空）
            WaveScores = dto.WaveScores ?? new List<int>();
            PromoteAnnouncements = dto.PromoteAnnouncements ?? new List<PromoteAnnouncement>();
            WaveEndCountdown = dto.WaveEndCountdown;
            CurrentEventId = dto.CurrentEventId;
            DrawnEventIds = dto.DrawnEventIds ?? new List<string>();
            FreeExecutes = dto.FreeExecutes != null ? new HashSet<int>(dto.FreeExecutes) : new HashSet<int>();
            ConsumedModules = dto.ConsumedModules ?? new Dictionary<string, int>(); // 2026-08-23：消耗净增量（旧档缺省空——层内重开编辑即重建）
            _diagnosticLog.Clear();
            if (dto.DiagnosticLog != null) _diagnosticLog.AddRange(dto.DiagnosticLog); // 2026-08-23：排查诊断（旧档缺省空）
            ActiveStyles = dto.ActiveStyles != null ? new HashSet<string>(dto.ActiveStyles) : new HashSet<string>();
            EditedCardQualifyId = dto.EditedCardQualifyId; // 2026-08-23：E5 资格（旧档缺省 0=无资格）
            PresentationTimeouts = dto.PresentationTimeouts ?? new List<TimeoutRecord>(); // 诊断保留（不参与逻辑）
            MahjongScore = dto.MahjongScore ?? new List<int>();
            FanCount = dto.FanCount;
            MahjongWalls = dto.MahjongWalls ?? new Dictionary<Vector2Int, ObstacleData>();
            CurrentFloor = dto.CurrentFloor;
            CurrentNodeIndex = dto.CurrentNodeIndex;
            NodeStates = dto.NodeStates ?? new List<NodeState>();
            ReplayLog = dto.ReplayLog ?? new List<ConcreteAction>();
            Obstacles = dto.Obstacles != null ? new HashSet<Vector2Int>(dto.Obstacles) : new HashSet<Vector2Int>();
            ShockWalls = dto.ShockWalls != null ? new HashSet<Vector2Int>(dto.ShockWalls) : new HashSet<Vector2Int>(); // 2026-08-24 能力「震击」（旧档缺省空）
            Pieces.Clear();
            PiecesById.Clear();
            if (dto.Pieces != null)
            {
                foreach (var pdto in dto.Pieces)
                {
                    // ⚠️ 2026-08-13 读档健壮性：原 ConfigTable.Get（查不到抛异常崩读档）——改 Find（配置缺失跳过该棋子+警告）
                    // ⚠️ 2026-08-24 围棋棋子：代码内建 def（不注册 ConfigTable）——按 IsGo 特判取 def，不走 Find
                    var pieceDef = pdto.IsGo ? GoPiece.GetDef() : ConfigTable.Find<PieceDef>(pdto.DefId);
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
                        tempShield = pdto.TempShield, // 2026-08-26 额外护盾（旧档缺省 0）
                        waveIndex = pdto.WaveIndex ?? -1, // 波次标（2026-08-13 补：第 3 关每波得分依赖——原 DTO 缺字段读档归 -1）
                        element = pdto.Element,     // 属性（2026-08-20——缺省 None）
                        IsGo = pdto.IsGo,           // 2026-08-24 围棋棋子
                    };
                    foreach (var abilityId in (pdto.TempAbilities ?? new List<int>())) // AA5-02：旧档缺省 null 兜底（不 NRE）
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
            // AA4-02 读档兜底：NextPieceId 缺省 0（旧档/损坏）→ 取已恢复棋子最大 Id + 1（至少 1）
            if (_nextPieceId <= 0)
            {
                int maxId = 0;
                foreach (var id in PiecesById.Keys)
                {
                    if (id > maxId) maxId = id;
                }
                _nextPieceId = maxId + 1;
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
        public int NextCardId; // 牌实例 id 计数器（2026-08-21——缺省 0 = 旧档）
        public int PlayerAP;
        public int PlayerAPMax;
        public int PlayerScore;
        public int BaseScore;        // 计分：基础得分（2026-08-19）
        public int ScoreMultiplier;  // 计分：倍率（2026-08-19——缺省 0 读档 clamp 1）
        public int EnemyAP;
        public int EnemyAPMax;
        public int EnemyScore;
        public List<Card> Hand;          // 牌（棋子牌/麻将牌——2026-08-20 牌结构改造）
        public List<int> Graveyard;
        public List<Card> PromotedReplacedPile; // 升变替换池（2026-08-24——原牌被升变替换）
        public List<Card> MahjongUsedPile;      // 麻将使用池（2026-08-24——摸切/打出墙体）
        public List<Card> MahjongDeadPile;      // 麻将死亡池（2026-08-24——墙体破坏；与棋子牌墓地独立）
        public List<Card> DiscardPieceDeaths;   // 弃牌区·棋子死亡（2026-08-24——Card 化；升变死亡两张）
        public List<Card> DiscardMahjongDeaths; // 弃牌区·麻将死亡（2026-08-24——摸切立即/墙体破坏后）
        public int DiceValue;                   // 骰子点数（2026-08-24）
        public bool DiceMovePending;            // 骰子移动待执行（2026-08-24）
        public int DiceMoveSteps;               // 骰子移动步数（2026-08-24）
        public int TokenCount;                  // 代币（2026-08-24）
        public Side GoLastColor;                // 围棋上次部署颜色（2026-08-24）
        public bool GoEverDeployed;             // 围棋是否部署过（2026-08-24）
        public int GoDeployCount;               // 围棋本回合部署次数（2026-08-24）
        public int GoExtraDeploys;              // 买子购买次数（2026-08-24 能力「买子」——当回合）
        public int GoValueBonus;                // 围棋价值加成（2026-08-24 能力「升值」——战斗级）
        public int BaopaiNumber;                // 宝牌数字（2026-08-24 能力「宝牌」——0=未选）
        public List<Card> DrawPile;      // 抽牌堆（牌——2026-08-20）
        public List<int> EnemyWavePool;
        public Dictionary<int, List<Template>> CurrentPrograms;
        public List<int> EditingDefs;
        public Dictionary<int, List<Template>> HiddenModules;   // 编辑会话 hide 模式（2026-08-19）
        public List<int> EditCandidates;                        // 编辑事件三选一候选（defId）
        public List<Template> EditModuleCandidates;             // 编辑事件模块候选（2026-08-24 起 4 个：移动/攻击各 2——效果不参与）
        public List<int> Relics;
        public List<int> AbilityCandidates;   // 能力事件候选（2026-08-22——id 列表）
        public List<int> AbilityRefreshLeft;  // 候选刷新次数（2026-08-22）
        public List<string> RuleCandidates;   // 玩法事件候选（2026-08-24——玩法 id 列表）
        public List<int> WaveScores;
        public List<PromoteAnnouncement> PromoteAnnouncements;
        public int WaveEndCountdown;
        public string CurrentEventId;
        public List<string> DrawnEventIds;
        public List<int> FreeExecutes;
        public Dictionary<string, int> ConsumedModules; // 本层模块消耗净增量（2026-08-23 决策 4）
        public List<string> DiagnosticLog; // 排查诊断（2026-08-23 第二梯队——开时才非空）
        public List<string> ActiveStyles;   // 玩法激活（2026-08-20）
        public int EditedCardQualifyId;        // E5 资格牌实例 id（2026-08-23；0=无）
        public List<TimeoutRecord> PresentationTimeouts; // 表现回执超时诊断（2026-08-21）
        public List<int> MahjongScore;       // 麻将牌山（2026-08-20）
        public int FanCount;                 // 麻将番数（2026-08-20）
        public Dictionary<Vector2Int, ObstacleData> MahjongWalls; // 麻将墙体（2026-08-20）
        public int CurrentFloor;
        public int CurrentNodeIndex;
        public List<NodeState> NodeStates;
        public List<ConcreteAction> ReplayLog;
        public List<Vector2Int> Obstacles;
        public List<Vector2Int> ShockWalls; // 2026-08-24 能力「震击」墙（战斗级）
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
        public int TempShield; // 2026-08-26 波次部署额外护盾（spawnShield——旧档缺省 0）
        public int? WaveIndex; // 所属波次（2026-08-13 补——每波得分按此累计；可空：旧档缺字段=null，读档归 -1）
        public Element Element; // 属性玩法（2026-08-20）
        public bool IsGo; // 围棋棋子（2026-08-24——专用"棋子牌"部署，B1 定稿）
    }

    /// <summary>表现回执超时记录（2026-08-21 诊断——只写不读，存档可查）。</summary>
    [Serializable]
    public class TimeoutRecord
    {
        public int SessionId;   // 战斗会话 id
        public int ActionId;    // 等待中的动作 token
        public int WaitMs;      // 实际等待毫秒
        public string Phase;    // 等待时阶段（PlayerTurn/EnemyTurn…）
        public string At;       // 时间（ISO——留痕排序）
    }

    /// <summary>
    /// 弃牌区大容器（2026-08-24 设计定稿——"更大的储存区域同时读取两种死亡区"）：
    /// 棋子死亡区（Card 化——升变死亡记两张）+ 麻将死亡区（摸切立即/墙体破坏后）；
    /// 原区域（Graveyard/三池）保留双写（兼容），本容器为权威 Card 化查询源（代币购买/前端展示）。
    /// 记录档（三档标准）：受限写方法 + 只读视图；整局累计（ResetForNewRun 清、ResetForBattle 保留）。
    /// </summary>
    [Serializable]
    public class DiscardZone
    {
        private readonly List<Card> _pieceDeaths = new List<Card>();
        private readonly List<Card> _mahjongDeaths = new List<Card>();

        public IReadOnlyList<Card> PieceDeaths => _pieceDeaths;
        public IReadOnlyList<Card> MahjongDeaths => _mahjongDeaths;

        public void RecordPieceDeath(Card card) => _pieceDeaths.Add(card);
        public void RecordMahjongDeath(Card card) { if (card.IsMahjong) _mahjongDeaths.Add(card); }
        public void Clear() { _pieceDeaths.Clear(); _mahjongDeaths.Clear(); }

        /// <summary>读档重建（整批填充——旧档缺省 null 兼容）。</summary>
        public void Load(List<Card> pieces, List<Card> mahjongs)
        {
            _pieceDeaths.Clear();
            if (pieces != null) _pieceDeaths.AddRange(pieces);
            _mahjongDeaths.Clear();
            if (mahjongs != null) _mahjongDeaths.AddRange(mahjongs);
        }
    }
}
