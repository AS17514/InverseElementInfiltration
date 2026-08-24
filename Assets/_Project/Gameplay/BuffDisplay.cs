using System.Collections.Generic;
using TheLaw.Data;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// buff 展示查询器（只读聚合——效果逻辑各机制保持现状，此处汇总供 UI 显示）。
    /// 各机制数据源不动（护盾=shieldCount / 免费资格=FreeExecutes / 临时能力=tempAbilities），
    /// 本类是唯一"知道各机制在哪查"的聚合点。
    /// 加新机制 = ① 此处加一个检查块 ② 变化点发 BuffsChanged 事件 ③ 配置表加条目。
    /// （2026-08-24 行动经济：② 由 UI 层 ActionEconomyBuffSync 补发——后端变化点不动。）
    /// </summary>
    public static class BuffDisplay
    {
        public static List<BuffInfo> GetBuffs(PieceInstance piece, GameState state)
        {
            var list = new List<BuffInfo>();

            // 护盾（数据源：piece.shieldCount——不迁移，直接读字段）
            if (piece.shieldCount > 0)
            {
                list.Add(new BuffInfo { key = "shield", remaining = piece.shieldCount });
            }

            // 免费执行资格（数据源：state.FreeExecutes——不迁移，直接查集合）
            if (state.FreeExecutes.Contains(piece.Id))
            {
                list.Add(new BuffInfo { key = "free_execute", remaining = 1 });
            }

            // 行动经济（2026-08-24：ActionEconomy 激活且己方棋子——执行不耗 AP + 每棋子每回合一次；
            // 数据源 state.ActionEconomyActed 随回合重置；已行动 → 态 B，否则 → 态 A）
            if (state.ActionEconomyActive && piece.side == Side.Player)
            {
                list.Add(state.ActionEconomyActed.Contains(piece.Id)
                    ? new BuffInfo { key = "action_economy_acted", remaining = -1 }
                    : new BuffInfo { key = "action_economy", remaining = -1 });
            }

            // 临时能力（数据源：piece.tempAbilities——逐个注册；显示名配置表未覆盖时 UI 回退 key）
            foreach (var ability in piece.tempAbilities)
            {
                list.Add(new BuffInfo { key = $"ability_{ability.Id}", remaining = -1 });
            }

            // 升变预告（数据源：state.PromoteAnnouncements——预告存在即显示；remaining=剩余敌方回合数）
            // ⚠️ 2026-08-23：预告挂载/倒计时移除（升变）/死亡清理点均已发 BuffsChanged——生命周期与棋子一致：
            // 升变完成 → 预告移除 → buff 消失；棋子死亡 → 预告随棋子清理 → buff 不残留。
            foreach (var ann in state.PromoteAnnouncements)
            {
                if (ann.pieceId == piece.Id)
                {
                    list.Add(new BuffInfo { key = "promote_announced", remaining = ann.countdown });
                    break;
                }
            }

            return list;
        }
    }
}
