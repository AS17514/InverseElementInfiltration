# 《逆元渗透》非 UI 层开发任务表

> 依据：`内部文档/架构设计.md`（最新版，含 2026-08-05 全部更新）+ `README.md`（目录/命名规范）+ 决策记录
> 范围：Core / Data / Gameplay 三层全部代码（UI 层不在本表，由前端同学负责）
> 执行顺序：**必须按编号顺序**（依赖关系：Core → Data → Gameplay）
> 命名空间：`TheLaw.Core` / `TheLaw.Data` / `TheLaw.Gameplay`（架构文档 §一；如需改项目名请全局替换）

## 通用规范（每个任务必须遵守）

1. 文件放 README 规定的目录（见每个任务的"路径"），**不新建目录**
2. 行尾 LF（`.gitattributes` 强制）；禁止无语义命名（a/b/temp1）
3. 每个文件一个主类（接口/结构体可合并小文件）
4. `namespace TheLaw.Core / TheLaw.Data / TheLaw.Gameplay`（按所在层）
5. 允许引用 UnityEngine（asmdef `noEngineReferences: false`）；Core 不引用 Data/Gameplay，Data 只引用 Core，Gameplay 引用 Core+Data
6. 数据层：纯数据零行为（字段/属性，不做逻辑）；行为全部在 Gameplay
7. 落账纪律：**所有 GameState 修改必须经 Resolver**（internal 访问控制），GameState 内部用 internal set
8. 提交规范（如提交）：`feat: xxx` 前缀 + 中文描述

---

# 阶段一：Core（Assets/_Project/Core/）

## C1. Utils/Assert.cs
- 静态类 `Assert`：`IsTrue(bool, string)` / `IsNotNull(object, string)` / `Fail(string)`——不满足抛 `InvalidOperationException`
- 用途：前置断言（时序错误当场爆）

## C2. Utils/ReentrantGuard.cs
- 类 `ReentrantGuard`：`bool TryEnter()` / `void Exit()`——int 深度计数，>0 拒绝再进入；Exit 减计数并允许为 0

## C3. Utils/BaseManager.cs
- 泛型单例基类（纯 C#，非 Mono）：`public static T Instance`，泛型约束 `where T : BaseManager<T>, new()`，构造时注册
- 用途：EventCenter/SaveManager/RandomManager/SettingsSystem/GameState 继承

## C4. Utils/SingletonAutoMono.cs
- 泛型单例基类（MonoBehaviour）：`public static T Instance`，首次访问时场景内查找/创建 DontDestroyOnLoad
- 用途：AudioManager 继承

## C5. Data/ISnapshot.cs
- 接口 `ISnapshot`：`string Key { get; }` / `string ToJson()` / `void FromJson(string)`
- 实现者：GameState/SettingsSystem/TutorialSystem/ProgressSystem/RandomManager/RunHistory（后二者在 Gameplay/Data 实现）

## C6. Data/IPanel.cs
- 接口 `IPanel`：`string Key { get; }` / `void Show()` / `void Hide()`
- 依赖倒置：Core 定义接口，UI 层面板实现并主动注册——Core 不认识具体面板

## C7. Data/GameConfigBase.cs
- ScriptableObject 基类：`public abstract class GameConfigBase : ScriptableObject`——统一标识字段 `id: int`（配置资产基类）
- 用途：Data 层全部配置资产（PieceDef/FloorConfig/RelicDef...）继承

## C8. Managers/EventCenter.cs
- 继承 BaseManager；`void EventTrigger(string eventName, object data = null)` / `void AddEventListener(string, Action<object>)` / `void RemoveEventListener(string, Action<object>)`
- 内部：`Dictionary<string, Action<object>>`（事件名用字符串，UI 枚举→字符串转换在 UI 层）

## C9. Managers/RandomManager.cs
- 继承 BaseManager + ISnapshot；`void SetSeed(int)` / `int Range(int minInclusive, int maxExclusive)` / `T NextWeighted<T>(IList<T> items, Func<T,float> weightGetter)`
- 内部 `System.Random`；Key="RandomManager"；快照存 seed + 调用计数（读档后随机序列不漂移）

## C10. Managers/SettingsSystem.cs
- 继承 BaseManager + ISnapshot；`SetBGMVolumePercent(int)` / `SetSFXVolumePercent(int)` / `SetFullscreen(bool)` / `SetResolution(int w, int h)` / `GetResolutions()` / `SetBGMVolumeChanged` 事件
- 只存值 + 发事件（引擎级设置直接应用）；Key="SettingsSystem"

## C11. Managers/AudioManager.cs
- 继承 SingletonAutoMono；`PlayBGM(string)` / `PlaySFX(string)` / `SetVolume(float)`——监听设置事件（骨架：播放逻辑由 UI/资源同学补）

## C12. Managers/SaveManager.cs
- 继承 BaseManager；`RegisterSnapshot(ISnapshot)` / `SaveAll()` / `LoadAll()`——收集所有 ISnapshot → 打包 JSON → persistentDataPath；读 → 分发
- 不做版本号校验；存储文件固定名（多槽位字段预留 `slots: List<SaveSlot>` 结构，本轮先单槽）

## C13. Managers/UIManager.cs
- 继承 BaseManager；`RegisterPanel(IPanel)` / `ShowPanel(string key)` / `HidePanel(string key)` / `PushPanel(string)` / `PopPanel()`
- 只依赖 IPanel 维护面板栈（Dictionary<string, IPanel> + Stack<string>）；不认识具体面板

---

# 阶段二：Data（Assets/_Project/Data/）

## D1. Enums/GameEnums.cs（枚举族全集，按语义分组）
- 战斗：`Side { Player, Enemy }` / `PieceType { Initial, Deployable, Promoted }` / `Facing { Up, Down, Left, Right }` / `BattlePhase { PlayerTurn, EnemyTurn, GameOver, Placement }`（Placement=开局摆放阶段）/ `Direction [Flags] { None, Up, Down, Left, Right, UpLeft, UpRight, DownLeft, DownRight }`
- 棋盘：`Footprint { Size1x1, Size1x2, Size1x3 }`
- 规则：`TargetRule { Nearest, LowestHP, HighestValue }` / `TargetScope { PieceCollection, Board }` / `VictoryRule { WipeOut, ScoreTarget, Both, PerWaveScore }` / `AttackShape { Single, Cross, Surround }` / `AttackMode { Melee, MeleeAOE, DirectFire, Arcing, Spell }`（抛射/法术暂同，越障直接命中；直射受障碍阻挡）
- 爬塔：`NodeType { Event, Battle }` / `NodeState { Locked, Available, Completed }`
- 特殊能力：`SpecialAbilityType { Passive, Trigger, Attach }` / `TriggerPoint { OnBattleStart, OnTurnStart, OnTurnEnd, OnKill, OnPieceLanded }` / `PassiveTarget { MoveStep, AttackDamage, AttackRange, Durability }` / `AttachPoint { OnAttack, OnMove }`

## D2. Templates.cs（模板族——程序槽位内容，参数化可编辑）
- `MoveStep`（class）：`Direction direction`（单方向）+ `List<int> steps`（可选步数集合）——最底层移动单元
- `MoveSegment`（class）：`List<MoveStep> moves`（段内选一个方向+步数执行）
- `MovePath`（class）：`List<MoveSegment> segments`（段序列顺序执行，段间从各终点继续）
- `TargetParam`（struct）：`TargetRule rule` + `int amount`
- `abstract class Template`（基类，可留空标识）
- `MoveTemplate : Template`：`List<MovePath> paths`（路径选项集合，每条独立计算；可达格 = 各路径落点合集）——程序槽位：移动
- `AttackTemplate : Template`：`AttackMode mode` / `Direction directions`（[Flags] 可选方向集，默认 {Up}=正前方；攻击时玩家从中选一格）/ `int range` / `int damage` / `bool friendlyFire = true` / `AttackShape shape`（保留，攻击模板不再使用）/ `List<Vector2Int> points`（抛射/法术自由点选：相对棋子锚点偏移集合，无射程概念、对点无视障碍）——程序槽位：攻击（mode 决定分派：直射首个可攻击物阻挡 / 近战群攻方向集×深度全部攻击 / 抛射法术对点）
- `SkipTemplate : Template`——空操作槽
- 注：无 TargetParam 字段需求则从模板移除（PlayerSelect 目标选择由棋盘规则/玩家手动决定）

## D3. Actions.cs（行动族——回放凭据，有目标）
- `abstract class ConcreteAction`（基类）
- `MoveAction : ConcreteAction`：`int pieceId` / `Vector2Int from` / `Vector2Int to`
- `AttackAction : ConcreteAction`：`int pieceId` / `Vector2Int targetCell`（格子目标！可空放/打己方）/ `AttackTemplate template`（记录形状/伤害/友伤）
- `DeployAction : ConcreteAction`：`string pieceDefId` / `Side side` / `Vector2Int cell`
- `PromoteAction : ConcreteAction`：`int pieceId` / `string newDefId`
- `SkipAction : ConcreteAction`：`int pieceId` / `SkipReason reason`（枚举：NoMove/NoTarget）

## D4. Requests.cs（请求族——输入，未校验）
- `DeployRequest`：`string pieceDefId` / `Vector2Int cell` / `bool free`
- `PromoteRequest`：`int pieceId` / `string newDefId` / `bool free`
- `ExecuteRequest`：`int pieceId` / `bool free`（free=true：不扣 AP——免费额外行动/波次部署）

## D5. PieceDef.cs（棋子定义，SO 资产）
- 继承 GameConfigBase（id 继承）
- `PieceType pieceType` / `int value` / `int durability`（承伤次数）/ `Footprint footprint` / `Facing defaultFacing` / `List<SpecialAbilityDef> specialAbilities` / `List<ProgramDef> programSet`（默认模组+备用模组）/ `int promotionConfigId`
- 注：无 movePattern/attackRange（模板参数化）；`ProgramDef` = 4 槽程序：`List<Template> slots`（数据层简单容器，与 PieceDef 同文件或独立文件）

## D6. 关卡配置族（FloorConfig.cs / MapConfig.cs / AIParams.cs / PromotionConfig.cs）
- `FloorConfig : GameConfigBase`：`VictoryRule victoryRule` / `int targetScore` / `List<WaveDef> waveDefs`（每波：回合间隔+阵容）/ `int enemyMaxAP` / `List<string> eventPoolIds` / `List<PromotionConfig> ...`（见下）
- `WaveDef`（数据类）：`int startTurn`（第几回合出波）/ `List<string> pieceDefIds`（阵容）/ `bool isLastWave`
- `MapConfig : GameConfigBase`：`List<FloorConfig> floors`（4 层）
- `AIParams : GameConfigBase`：AI 决策参数（短视吃子——`int valueWeight` 等，本轮留基础字段：`bool greedyCapture` / `int moveScoreWeight` / `int attackScoreWeight`）
- `PromotionConfig : GameConfigBase`：`string fromDefId` / `string toDefId`（升变映射——一对一定义）

## D7. 事件族（EventDefinitions.cs）
- `EventPool : GameConfigBase`：`List<EventPoolEntry> entries`
- `EventPoolEntry`：`string eventId` / `float weight` / `string conditionId`（池级条件，可空）
- `EventDefinition : GameConfigBase`：`List<EventOption> options`
- `EventOption`：`string optionId` / `string label` / `bool available`（availability：UI 灰显+规则层二次校验）/ `List<EffectDefinition> effects`
- `EffectDefinition`：`TargetScope targetScope` / `TargetRule? targetRule`（空=玩家手动选）/ `EffectType effectType`（枚举：AddPiece/ModifyDurability/EditProgram/GrantAbility 等，本轮定义枚举+字段骨架）/ 参数字段（targetDefId/amount...）

## D8. RelicDef.cs + SpecialAbilityDef.cs（遗物与特殊能力）
- `RelicDef : GameConfigBase`：`List<SpecialAbilityDef> abilities`（引用底层工具——消费者）
- `SpecialAbilityDef : GameConfigBase`：
  - `SpecialAbilityType type`
  - Passive：`PassiveTarget passiveTarget` / `int passiveValue` / `bool applyBeforeResolve`（解析前=true/结算时=false）
  - Trigger：`TriggerPoint triggerPoint` / `TriggerEffect triggerEffect`（枚举：ExtraAction/HealDurability）/ `int amount`
  - Attach：`AttachPoint attachPoint` / `AttackShape attachShape` / `int attachDamage`（=0 表示沿用主伤害）

## D9. ConfigTable.cs（配置注册表）
- 静态类：`Register<T>(GameConfigBase config) where T : GameConfigBase` / `T Get<T>(int id) where T : GameConfigBase`——`Dictionary<Type, Dictionary<int, GameConfigBase>>`
- fail-fast：查不到抛异常（Assert）；Bootstrap 加载 SO 资产后注册

---

# 阶段三：Gameplay（Assets/_Project/Gameplay/，核心类直接放本目录）

## G1. PieceInstance.cs（运行时棋子）
- `PieceDef def` / `Side side` / `int durability`（当前承伤）/ `Vector2Int position` / `Facing facing` / `List<Template> programOverride`（实例覆盖①，null=无）/ `List<SpecialAbilityDef> tempAbilities`（临时获得能力，随战斗销毁）/ `bool isDeployed`
- `List<Template> GetProgram(GameState state)`：**程序三层查找**——programOverride != null → 用 override；state.CurrentPrograms 有条目 → 用条目；否则 def 默认模组（def.programSet[0].slots）

## G2. GameState.cs（唯一状态源 + ISnapshot）
- 全部状态（internal set，**只有 Resolver 能写**）：
  - 棋盘：`Dictionary<Vector2Int, PieceInstance> pieces` / `int turnCount` / `BattlePhase phase`
  - 玩家：`List<PieceDef> hand`（手牌）/ `List<PieceDef> graveyard`（墓地）/ `int playerAP` / `int playerAPMax` / `int playerScore`
  - 敌方：`List<PieceDef> enemyWavePool`（波次池）/ `int enemyAP` / `int enemyAPMax` / `int enemyScore`
  - 程序：`Dictionary<int, List<Template>> currentPrograms`（种类级表：defId→程序，只存编辑差异）
  - 局内：`List<RelicDef> relics` / `List<int> waveScores`（每波得分）/ `List<PromoteAnnouncement> promoteAnnouncements`（升变预告：pieceId+newDefId+countdown）/ `int currentFloor` / `int currentNodeIndex` / `List<NodeState> nodeStates`
  - `string Key => "GameState"` / ToJson/FromJson（Newtonsoft.Json 序列化全部字段）
  - `bool IsPlayerDefeated`（棋盘无棋子 且 手牌空——**仅玩家侧**判负）
  - `void ResetForNewRun()`（局结束重置：清空全部局内状态）

## G3. BoardRules.cs（棋盘规则判定，纯逻辑）
- 依赖 GameState（只读）
- `bool IsInsideBoard(Vector2Int)` / `bool IsCellPassable(GameState, Vector2Int)` / `bool IsCellOccupied(GameState, Vector2Int)` / `bool IsMoveLegal(GameState, PieceInstance, Vector2Int to)` / `bool IsPathClear(GameState, PieceInstance, Vector2Int to)`
- `List<Vector2Int> GetLegalMoves(GameState, PieceInstance)`（沿 MovePattern 方向集走 maxSteps，出界/障碍/占用逐格检查；解析时应用被动修正：步数+修正值）
- `List<Vector2Int> GetAttackableCells(GameState, PieceInstance, AttackTemplate)`（**任意格子**：范围内所有格，含己方/空格）
- `int GetMoveRange / GetAttackRange`（能力修正载体，作用于模板参数）
- `bool IsPromotionValid(PieceInstance)`（零门槛：def.promotionConfigId != 0 即可）/ `bool IsVictory(GameState, FloorConfig)`（按 victoryRule：WipeOut=敌方无棋子；ScoreTarget=playerScore≥targetScore；Both=全灭+达分；PerWaveScore=每波达标）/ `bool IsPlayerDefeated(GameState)` / `bool IsStalemate` / `int CountAlivePieces(GameState, Side)`

## G4. IntentResolver.cs（模板→行动，逐槽解析）
- `ConcreteAction Resolve(Template, PieceInstance, GameState)`——MoveTemplate→MoveAction（选落点由调用方传入或留空）；AttackTemplate→AttackAction（目标格由调用方传入）
- 解析时应用**被动修正**（Passive/applyBeforeResolve：步数/射程修正）；目标选择（PickClosestToEnemy/PickByTemplate）供 AI 用

## G5. Resolver.cs（统一落账器——唯一写入口 ★）
- **所有 GameState 写入只能经此**（GameState internal set 保证）
- `void Resolve(ConcreteAction)` / `void ResolveAll(List<ConcreteAction>)`
- 内部：ResolveMove/ResolveAttack/ResolveDeploy/ResolvePromote（升变：落账手牌减一——从 hand 移除 newDefId 对应牌）/ `ModifyDurability(PieceInstance, int delta)`（承伤±N 统一入口，归 0 → HandleDeath）
- `ApplyModifiers`（伤害修正：Passive/applyBeforeResolve=false + Attach 结算：ResolveAttack 时检查棋子 specialAbilities——Attach/OnAttack → 对 targetCell 周围 attachShape 范围额外结算（attachDamage=0 沿用主伤害））
- `HandleDeath(PieceInstance)`：击杀者积分（value）+ 棋子进墓地 + 发 OnKill 触发点（FloorRules + RelicSystem + 特殊能力 Trigger/OnKill → 动作进**待执行队列**）+ 发事件
- **待执行队列**：`Queue<ConcreteAction>`——触发点登记的动作，当前请求守卫退出后统一执行（防重入）
- `LogAction(ConcreteAction)`：落账日志 + 回放记录（写入 GameState.replayLog）
- **发事件**（EventCenter）：落账后发"状态变了/伤害发生"事件，携带数据（攻击者/目标/伤害/是否死亡——UI 表现依据）

## G6. EditorSession.cs（实时编辑会话——方案 B）
- `BeginEdit(int defId)` / `EndEdit(int defId)`（编辑态标记在 GameState：`HashSet<int> editingDefs`，入快照）/ `EditProgram(int defId, List<Template>)`（实时修改，经 Resolver 写 currentPrograms）/ `RestoreOriginal(int defId)` / `Undo(int defId)` / `RestoreAll()`（撤销栈：`Dictionary<int, Stack<EditOp>>`，会话级不入存档）/ `GetAvailableTemplates(PieceDef)` / `PreviewProgram(PieceDef, List<Template>)`（副本模拟，不改状态）

## G7. EventNodeSystem.cs（事件关）
- `OpenEvent(string nodeId)`（校验+落账编排）/ `OnOptionSelected(string eventId, int optionIndex)`（availability 校验+防重入）/ `OnTargetSelected(int pieceId)`（targetRule 空时走这步）/ `ExecuteEffects(List<EffectDefinition>)`（**经 Resolver 落账**——禁止绕过结算器）
- 抽取流程：TowerFlow 调 OpenEvent → RandomManager.NextWeighted（事件池，过滤 drawnEventIds，候选空→必出兜底）→ 写 GameState.currentEventId

## G8. EnemyAI.cs（敌方 AI）
- `List<Request> DecideTurn(GameState, FloorConfig)`——按 enemyAPMax 预算产出多个请求（执行/部署），走统一管线；**短视吃子算法**：评估吃子收益（棋子 value + TargetRule.HighestValue），产出 ExecuteRequest（目标选择用 PickClosestToEnemy/PickByTemplate）
- 波次部署**不走 AI**（BattleFlow 波次调度直接产出 DeployRequest，free=true）

## G9. FloorRules.cs + FloorRulesFactory.cs（层规则窄钩子）
- `abstract class FloorRules`：虚方法 `OnBattleStart(GameState, Resolver)` / `OnTurnStart` / `OnTurnEnd` / `OnKill(PieceInstance killer, PieceInstance victim)` / `OnPieceLanded(PieceInstance)`
- `static class FloorRulesFactory`：`Dictionary<int, Func<FloorRules>>` 注册表 + `Create(int floorIndex)`（加层注册一行；默认返回空实现基类）

## G10. RelicSystem.cs（遗物触发）
- `OnTurnStart(GameState, Resolver)` / `OnKill(...)` / `OnBattleStart(...)` 等——遍历 GameState.relics 的 Trigger 特殊能力，经 Resolver 落账（触发点消费者之一）
- 修正型遗物走 BoardRules/Resolver 修正路径（被动修正数据源：GameState.relics + PieceInstance.tempAbilities + def.specialAbilities 合并查询）

## G11. TutorialSystem.cs / G12. ProgressSystem.cs（可先骨架）
- TutorialSystem：`HashSet<string> triggered` + `TryShow(string)`（BattleFlow/TowerFlow 主动调用）+ ISnapshot
- ProgressSystem：`int storyIndex` + `AdvanceStory()` + ISnapshot（剧情区；策划案无成就）

## G13. BattleFlow.cs（战斗流程——最复杂）
- 依赖：GameState/BoardRules/IntentResolver/Resolver/EnemyAI/FloorRules/ReentrantGuard
- `StartBattle(FloorConfig)`（开局：起始标记棋子生成→**Placement 阶段**玩家自由摆→开始 PlayerTurn；初始化波次/升变预告为空）/ `EndBattle(Side winner)` / `ChangePhase(BattlePhase)`
- `OnPlayerRequestDeploy/Promote/Execute(Request)`（管线入口：阶段检查→守卫 TryEnter→TranslateToAction→Resolver→守卫 Exit→DeductActionPoint（free=false 才扣）→CheckActionPoints/CheckVictory）
- `OnPlayerEndTurn()` / `StartPlayerTurn`（回满 AP→OnTurnStart 触发点）/ `StartEnemyTurn` / `ResolveEnemyTurn`（AI 产出请求→走管线）
- **波次调度**（OnTurnEnd 时）：按 FloorConfig.waveDefs 检查回合数→产出 DeployRequest（free）→ **升变预告处理**：promoteAnnouncements 倒计时归零→执行升变（PromoteRequest 走管线）→ 本波次开始前预告下一波升变棋子
- **表现等待**：每槽位落账后进入表现等待子状态（ReentrantGuard 锁），等"表现完成"事件（**无限等+日志**）→解锁
- `CheckVictory`：玩家判负（IsPlayerDefeated）→ 失败；按 victoryRule → 胜利
- 末波倒计时：waveEndCountdown 归零 → 强制结算胜负

## G14. TowerFlow.cs（爬塔推进）
- `EnterFloor(int)` / `AdvanceNode()`（节点=Event → EventNodeSystem.OpenEvent；节点=Battle → 等待战斗）/ `OnBattleEnded(Side winner)`（胜利→推进；失败→RunEnded）
- 塔状态（currentFloor/节点状态）在 GameState（入快照）；TowerFlow 不实现 ISnapshot

---

## 验证清单（写完代码后逐条检查）
1. `git status`：文件全部在 README 规定目录
2. 编译：Unity 打开无编译错误（asmdef 依赖方向正确——Core 不引用 Data/Gameplay）
3. 命名空间一致：TheLaw.Core / TheLaw.Data / TheLaw.Gameplay
4. 落账纪律：grep `GameState` 的写入，全部经 Resolver（internal set 编译期保证）
5. 无无语义命名
