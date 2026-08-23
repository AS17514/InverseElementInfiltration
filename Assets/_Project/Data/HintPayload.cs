namespace TheLaw.Data
{
    /// <summary>
    /// 通用"变亮/提示"通道负载（2026-08-23 设计定案——同一通道 + 参数区分）。
    /// 携带：类别（决定前端渲染器与样式）+ 目标（作用对象）+ 备用字段。
    /// 显示真值以后端状态容器为准（各 kind 对应状态入档），本事件只是即时刷新信号；开局/读档按状态回填。
    /// </summary>
    public class HintPayload
    {
        public HintKind kind;    // 显示类别：决定前端渲染器与样式（棋子/手牌/能力三类互不相同）
        public int targetId;     // 目标对象：棋子 id / 牌 instanceId / 能力遗物 id；0 = 整体或取消
        public int extra;        // 备用（如预告倒计时）
    }

    /// <summary>变亮/提示类别（加新需求 = 加 kind + 状态容器 + 前端渲染器，事件通道不变）。</summary>
    public enum HintKind
    {
        PiecePromotePreview,   // 棋子变亮·升变预告（现状 PromoteAnnounced 可并入本 kind——迁移由前端自决）
        CardQualify,           // 手牌变亮·E5 资格（目标 = 牌 instanceId；0 = 取消）
        AbilityActive,         // 能力面板变亮（目标 = 能力遗物 id；0 = 整个能力栏）
        PieceMark,             // 棋子变亮·特殊标记（未来：区分被编辑/敌我两类棋子等）
    }
}