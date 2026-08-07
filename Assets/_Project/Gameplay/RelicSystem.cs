using System.Collections.Generic;
using TheLaw.Data;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 遗物系统：触发型遗物消费者（触发点调用——与 FloorRules 同为插槽消费者）。
    /// 修正型遗物走 BoardRules/Resolver 修正路径（Passive 修正查询）。
    /// </summary>
    public class RelicSystem
    {
        private readonly GameState _state;
        private readonly Resolver _resolver;

        public RelicSystem(GameState state, Resolver resolver)
        {
            _state = state;
            _resolver = resolver;
        }

        /// <summary>回合开始触发（如"回合开始回血"——作用于玩家场上全部棋子）。</summary>
        public void OnTurnStart()
        {
            foreach (var relic in _state.Relics)
            {
                foreach (var ability in relic.abilities)
                {
                    if (ability.type == SpecialAbilityType.Trigger && ability.triggerPoint == TriggerPoint.OnTurnStart)
                    {
                        if (ability.triggerEffect == TriggerEffect.HealDurability)
                        {
                            foreach (var piece in new List<PieceInstance>(_state.Pieces.Values))
                            {
                                if (piece.side == Side.Player)
                                {
                                    _resolver.ModifyDurability(piece, ability.amount);
                                }
                            }
                        }
                    }
                }
            }
        }

        // 击杀触发（OnKill）已并入 Resolver.OnKillTriggers（击杀者回血/额外行动——killer 传递）
        // 本类只保留回合开始等流程级触发
    }
}
