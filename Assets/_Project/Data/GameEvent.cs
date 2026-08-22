namespace TheLaw.Data
{
    /// <summary>
    /// 游戏事件枚举（事件名 = 枚举，不会拼错——"UI 不加门面层"决策）。
    /// 规则层发"发生了什么"（携数据）；UI 监听决定表现；BattleFlow 等"表现完成"。
    /// </summary>
    public enum GameEvent
    {
        // ---- 规则层 → UI（数据变化通知，携带信息供 UI 决定表现）----
        StateChanged,          // 通用：数据变了（UI 刷新）
        PieceMoved,            // 棋子移动（pieceId, from, to）
        DamageDealt,           // 伤害发生（攻击者 pieceId, 目标格, 伤害, 是否死亡, 友伤）
        PieceDeployed,         // 部署
        PiecePromoted,         // 升变
        PieceDied,             // 死亡（pieceId, side）
        PhaseChanged,          // 阶段切换（BattlePhase）
        ActionPointChanged,    // 行动点变化（side, 当前, 上限）
        HandChanged,           // 手牌/墓地变化
        ProgramEdited,         // 程序被编辑（defId）
        RelicObtained,         // 获得遗物
        WaveAnnounced,         // 波次预告（波次号, 阵容）
        PromoteAnnounced,      // 敌方升变预告（pieceId, newDefId, 倒计时）
        EventOpened,           // 事件关打开（携带 CurrentEventId——UI 打开事件界面）
        EditCandidatesDrawn,   // 编辑事件候选就绪（2026-08-19：三选一棋子 + 6 候选模块已抽取——UI 显示三选一；查询 GameState.EditCandidates/EditModuleCandidates）
        AbilityCandidatesDrawn,  // 能力事件候选就绪（2026-08-22：词条过滤随机 3——UI 显示三选一 + 每项可刷新；查询 GameState.AbilityCandidates/AbilityRefreshLeft）
        ExtraActionGranted,    // 免费行动资格授予（携带棋子 Id——UI 显示"免费行动"标记）
        BuffsChanged,          // 棋子 buff 变化（携带棋子 Id——UI 刷新该棋子的 buff 标记；查询 BuffDisplay.GetBuffs）

        // ---- UI → 规则层（玩家输入/表现完成）----
        PlayerCellSelected,    // 玩家选格（落点/目标）
        PresentationFinished,  // 表现完成（UI 播完一组表现后发——BattleFlow 解锁）
        PhaseDisplayed,        // 阶段已展示（UI 渲染阶段名后下一帧发——动画优先：无动画的阶段切换至少展示一帧）
        PlacementFinished,     // 开局摆放完成
        EventCompleted,        // 事件完成（UI 处理完事件交互后发——TowerFlow 推进下一节点；与 PlacementFinished 同模式）

        // ---- 整局流程 ----
        RunEnded,              // 整局结束（携带 bool victory——Bootstrap 监听：清档/回主菜单）
    }
}
