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

        // ---- UI → 规则层（玩家输入/表现完成）----
        PlayerCellSelected,    // 玩家选格（落点/目标）
        PresentationFinished,  // 表现完成（UI 播完一组表现后发——BattleFlow 解锁）
        PlacementFinished,     // 开局摆放完成

        // ---- 整局流程 ----
        RunEnded,              // 整局结束（携带 bool victory——Bootstrap 监听：清档/回主菜单）
    }
}
