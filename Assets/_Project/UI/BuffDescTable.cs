using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// buff 描述表（数据驱动）：Assets/Data/Pieces/buffs-descriptions.json——key → 名称/描述/格式。
    /// key：shield / free_execute / ability_{Id}（临时能力）。未命中回退 key 本身。
    /// 消费方：BattleController 信息面板 buff 区（Txt_Other 拼接）。
    /// 与 SlotDescTable 同模式：Bootstrap 拖 TextAsset 引用 → Load。
    /// </summary>
    public static class BuffDescTable
    {
        private static Dictionary<string, BuffDescEntry> _table;

        public static void Load(TextAsset asset)
        {
            _table = null;
            if (asset == null) return;
            try
            {
                _table = JsonConvert.DeserializeObject<Dictionary<string, BuffDescEntry>>(asset.text);
                if (_table != null)
                {
                    Debug.Log($"[BuffDesc] buff 描述表加载：{_table.Count} 条");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BuffDesc] 描述表解析失败（回退 key）：{e.Message}");
                _table = null;
            }
        }

        /// <summary>查名称（未命中返回 null——调用方回退 key）。</summary>
        public static string GetName(string key)
        {
            return _table != null && key != null && _table.TryGetValue(key, out var e) ? e.name : null;
        }

        /// <summary>查描述（未命中 null）。</summary>
        public static string GetDesc(string key)
        {
            return _table != null && key != null && _table.TryGetValue(key, out var e) ? e.desc : null;
        }

        /// <summary>格式：count=剩余≥2 显示 ×N，=1 只显示名称；plain=只名称（未命中默认 count）。</summary>
        public static bool IsCountFormat(string key)
        {
            if (_table != null && key != null && _table.TryGetValue(key, out var e))
            {
                return e.format == "count";
            }
            return true;
        }
    }

    [System.Serializable]
    public class BuffDescEntry
    {
        public string name;
        public string desc;
        public string format; // count / plain
    }
}
