using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace TheLaw.Data
{
    /// <summary>
    /// 棋子价值体系（2026-08-15 策划新案落地）：
    /// 任何棋子的价值 = 生效程序槽位价值总和；类型 = 价值档位（0-3 初始 / 4-6 部署 / 7+ 升变）。
    /// ⚠️ 价值/类型是纯推导（可推导不入快照——架构原则 4）：唯一状态 = 生效程序（实例覆盖 &gt; 编辑差异 &gt; Def 默认），
    /// 编辑落账后下一次查询自然得到新结果（"编辑跨档 → 手牌直接变种类"天然成立）。
    /// 价值表 = Assets/Data/Templates/slot-values.json（模板"种类+编号"→价值）：
    /// 键与描述表同款（Move-N / Attack-N——攻击模板共用编号空间）；占位数值保证现有 12 棋子价值与旧配置一致（零回归），
    /// 正式数值待策划定稿（替换只改 JSON，代码零改动）。
    /// </summary>
    public static class PieceValue
    {
        private static Dictionary<string, int> _values;

        [System.Serializable]
        private class ValueJson
        {
            public string type; // Move / Attack（模板大类，与描述表键一致）
            public int id;
            public int value;
        }

        [System.Serializable]
        private class TableJson
        {
            public string _note; // 说明（加载忽略）
            public List<ValueJson> values;
        }

        /// <summary>加载价值表（Bootstrap 拖 TextAsset；失败 → 空表（推导全部按 0 + 警告——价值表就绪前行为降级）。</summary>
        public static void Load(TextAsset asset)
        {
            _values = null;
            if (asset == null) return;
            try
            {
                var table = JsonConvert.DeserializeObject<TableJson>(asset.text);
                if (table?.values == null) return;
                _values = new Dictionary<string, int>();
                foreach (var v in table.values)
                {
                    if (v == null || string.IsNullOrEmpty(v.type)) continue;
                    _values[$"{v.type}-{v.id}"] = v.value;
                }
                Debug.Log($"[PieceValue] 槽位价值表加载：{_values.Count} 条");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PieceValue] 价值表解析失败（推导按 0 降级）：{e.Message}");
                _values = null;
            }
        }

        /// <summary>单槽位价值（未登记/未编号 → 警告 + 0——新增模板必须同步价值表条目）。</summary>
        public static int GetValue(Template slot)
        {
            if (slot == null) return 0;
            string key = null;
            switch (slot)
            {
                case MoveTemplate m:
                    if (m.id > 0) key = $"Move-{m.id}";
                    break;
                case AttackTemplate a:
                    // 攻击模板共用编号空间（与描述表一致：Attack-N，跨 mode 不重复编号）
                    if (a.id > 0) key = $"Attack-{a.id}";
                    break;
                case EffectTemplate e:
                    // 效果模块（2026-08-19）：价值表 Effect-N（护盾/刺客能力/炮手能力——slot-values.json 已预留）
                    if (e.id > 0) key = $"Effect-{e.id}";
                    break;
                default:
                    break; // SkipTemplate 等（新规则无跳过槽——价值 0）
            }
            if (key == null)
            {
                Debug.LogWarning($"[PieceValue] 未编号模板无价值（按 0 计）：{slot.GetType().Name}");
                return 0;
            }
            if (_values != null && _values.TryGetValue(key, out var v))
            {
                return v;
            }
            Debug.LogWarning($"[PieceValue] 价值表缺失条目 {key}（按 0 计——请补 slot-values.json）");
            return 0;
        }

        /// <summary>程序总价值（Σ 槽位价值；空/空程序 = 0）。</summary>
        public static int SumValue(IList<Template> program)
        {
            if (program == null || program.Count == 0) return 0;
            int total = 0;
            foreach (var slot in program)
            {
                total += GetValue(slot);
            }
            return total;
        }

        /// <summary>价值 → 类型档位（0-3 初始 / 4-6 部署 / 7+ 升变）。</summary>
        public static PieceType GetType(int value)
        {
            if (value <= 3) return PieceType.Initial;
            if (value <= 6) return PieceType.Deployable;
            return PieceType.Promoted;
        }

        /// <summary>程序 → 类型档位（Σ 价值后映射）。</summary>
        public static PieceType GetType(IList<Template> program)
        {
            return GetType(SumValue(program));
        }
    }
}
