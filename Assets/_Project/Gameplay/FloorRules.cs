using TheLaw.Data;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 每层规则集（窄钩子）：机制差异走钩子，数值差异走 FloorConfig 参数化。
    /// 触发点 = 流程的固定插槽——FloorRules（层差异）与遗物/特殊能力（持有物）都是消费者。
    /// </summary>
    public abstract class FloorRules
    {
        public virtual void OnBattleStart(GameState state, Resolver resolver) { }
        public virtual void OnTurnStart(GameState state, Resolver resolver) { }
        public virtual void OnTurnEnd(GameState state, Resolver resolver) { }
        public virtual void OnKill(GameState state, Resolver resolver, PieceInstance killer, PieceInstance victim) { }
        public virtual void OnPieceLanded(GameState state, Resolver resolver, PieceInstance piece) { }
    }

    /// <summary>默认空实现（未注册层规则时兜底）。</summary>
    public class DefaultFloorRules : FloorRules
    {
    }
}
