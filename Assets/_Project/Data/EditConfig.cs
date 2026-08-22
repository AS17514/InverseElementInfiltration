using Newtonsoft.Json;
using UnityEngine;

namespace TheLaw.Data
{
    /// <summary>
    /// 编辑规则配置（2026-08-19——被替换模块展示策略两方案切换）：
    /// 数据源 = Assets/Resources/Configs/edit-config.json（**Resources 自动加载——免编辑器拖拽**）。
    /// replacedModuleVisibility：
    ///   "show"（默认）——被替换模块进候选池展示（双池模型/隐藏格，可放回——我们原设想）；
    ///   "hide"（策划方案）——被替换模块候选区直接隐藏（本棋子级 HiddenModules 存档标记；回退靠撤销/还原）。
    /// 模块数据永不删除（模板库实例常驻 + 快照撤销）——切换开关不丢任何数据。
    /// </summary>
    public static class EditConfig
    {
        private static bool _isHideMode;

        /// <summary>hide 模式（策划"直接隐藏"）；false = show 模式（候选池展示，默认）。</summary>
        public static bool IsHideMode => _isHideMode;

        [System.Serializable]
        private class ConfigJson
        {
            public string _note; // 说明（加载忽略）
            public string replacedModuleVisibility;
        }

        /// <summary>自动加载（Bootstrap 启动调用——Resources.Load，无需在 Inspector 拖拽；缺失 = show 模式）。</summary>
        public static void AutoLoad()
        {
            Load(Resources.Load<TextAsset>("Configs/edit-config"));
        }

        /// <summary>加载编辑规则配置（TextAsset 直载——测试/特例用；失败/缺失 = show 模式）。</summary>
        public static void Load(TextAsset asset)
        {
            _isHideMode = false;
            if (asset == null) return;
            try
            {
                var cfg = JsonConvert.DeserializeObject<ConfigJson>(asset.text);
                _isHideMode = cfg != null && cfg.replacedModuleVisibility == "hide";
                Debug.Log($"[EditConfig] 编辑规则加载：replacedModuleVisibility = {(_isHideMode ? "hide（策划：直接隐藏）" : "show（候选池展示——默认）")}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[EditConfig] 解析失败（按 show 模式降级）：{e.Message}");
                _isHideMode = false;
            }
        }
    }
}
