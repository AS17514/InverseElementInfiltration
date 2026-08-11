using System.Collections.Generic;
using TheLaw.Data;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// buff 展示查询器（只读聚合——效果逻辑各机制保持现状，此处汇总供 UI 显示）。
    /// 各机制数据源不动（护盾=shieldCount / 免费资格=FreeExecutes / 临时能力=tempAbilities），
    /// 本类是唯一"知道各机制在哪查"的聚合点。
    /// 加新机制 = ① 此处加一个检查块 ② 变化点发 BuffsChanged 事件 ③ 配置表加条目。
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

            // 临时能力（数据源：piece.tempAbilities——逐个注册；显示名配置表未覆盖时 UI 回退 key）
            foreach (var ability in piece.tempAbilities)
            {
                list.Add(new BuffInfo { key = $"ability_{ability.Id}", remaining = -1 });
            }

            return list;
        }
    }
}
