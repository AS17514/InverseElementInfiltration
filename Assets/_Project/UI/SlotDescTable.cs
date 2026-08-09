using System.Collections.Generic;
using TheLaw.Data;
using Newtonsoft.Json;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 程序块描述表（数据驱动）：Assets/Data/Pieces/slot-descriptions.json——key=程序块结构特征码，value=描述。
    /// 同结构程序块共用一条描述（程序块可复用语义）；未命中返回 null（调用方回退代码生成）。
    /// 后端给程序块加编号后，key 可切"种类+编号"，表结构不变。
    /// </summary>
    public static class SlotDescTable
    {
        private static Dictionary<string, string> _table;

        public static void Load(TextAsset asset)
        {
            _table = null;
            if (asset == null) return;
            try
            {
                _table = JsonConvert.DeserializeObject<Dictionary<string, string>>(asset.text);
                if (_table != null)
                {
                    Debug.Log($"[SlotDesc] 程序块描述表加载：{_table.Count} 条");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SlotDesc] 描述表解析失败（回退代码生成）：{e.Message}");
                _table = null;
            }
        }

        /// <summary>查询程序块描述（未命中返回 null）。</summary>
        public static string Get(Template slot)
        {
            if (_table == null || slot == null) return null;
            return _table.TryGetValue(FeatureOf(slot), out var desc) ? desc : null;
        }

        /// <summary>程序块结构特征码（描述表 key）。</summary>
        public static string FeatureOf(Template slot)
        {
            switch (slot)
            {
                case MoveTemplate m:
                    var dirs = new HashSet<Direction>();
                    var steps = new SortedSet<int>();
                    if (m.paths != null)
                    {
                        foreach (var path in m.paths)
                        {
                            foreach (var seg in path.segments)
                            {
                                foreach (var step in seg.moves)
                                {
                                    dirs.Add(step.direction);
                                    if (step.steps != null)
                                    {
                                        foreach (var n in step.steps) steps.Add(n);
                                    }
                                }
                            }
                        }
                    }
                    return $"Move-{DirsKey(dirs)}-{string.Join(",", steps)}";
                case AttackTemplate a:
                    string mode = a.mode switch
                    {
                        AttackMode.Melee => "Melee",
                        AttackMode.MeleeAOE => "AOE",
                        AttackMode.DirectFire => "Direct",
                        AttackMode.Arcing => "Arc",
                        AttackMode.Spell => "Spell",
                        _ => a.mode.ToString(),
                    };
                    // 抛射/法术：自由点选（无方向/射程概念）
                    if (a.mode == AttackMode.Arcing || a.mode == AttackMode.Spell)
                    {
                        return $"Attack-{mode}--{a.damage}";
                    }
                    return $"Attack-{mode}-{DirsKey(a.directions)}-{a.range}-{a.damage}";
                default:
                    return "Skip";
            }
        }

        /// <summary>方向位标志/集合 → 缩写码（固定顺序 U,D,L,R,UL,UR,DL,DR；全 8 向 → ALL）。</summary>
        static string DirsKey(Direction flags)
        {
            if (flags == 0) return "";
            var parts = new List<string>();
            if ((flags & Direction.Up) != 0) parts.Add("U");
            if ((flags & Direction.Down) != 0) parts.Add("D");
            if ((flags & Direction.Left) != 0) parts.Add("L");
            if ((flags & Direction.Right) != 0) parts.Add("R");
            if ((flags & Direction.UpLeft) != 0) parts.Add("UL");
            if ((flags & Direction.UpRight) != 0) parts.Add("UR");
            if ((flags & Direction.DownLeft) != 0) parts.Add("DL");
            if ((flags & Direction.DownRight) != 0) parts.Add("DR");
            return parts.Count == 8 ? "ALL" : string.Join(",", parts);
        }

        static string DirsKey(IEnumerable<Direction> dirs)
        {
            Direction flags = 0;
            foreach (var d in dirs) flags |= d;
            return DirsKey(flags);
        }
    }
}
