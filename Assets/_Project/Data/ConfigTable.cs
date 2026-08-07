using System;
using System.Collections.Generic;
using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>
    /// 配置注册表（静态）：SO 资产注册表 + 按 id 查询 + fail-fast。
    /// Bootstrap 启动时加载 SO 资产（Addressables）后逐条注册；运行时只查不增。
    /// </summary>
    public static class ConfigTable
    {
        private static readonly Dictionary<Type, Dictionary<int, GameConfigBase>> _table =
            new Dictionary<Type, Dictionary<int, GameConfigBase>>();

        /// <summary>注册一条配置（重复 id 覆盖并断言）。</summary>
        public static void Register<T>(T config) where T : GameConfigBase
        {
            if (!_table.TryGetValue(typeof(T), out var map))
            {
                map = new Dictionary<int, GameConfigBase>();
                _table[typeof(T)] = map;
            }
            Assert.IsTrue(!map.ContainsKey(config.Id), $"ConfigTable: 重复注册 id={config.Id} ({typeof(T).Name})");
            map[config.Id] = config;
        }

        /// <summary>按 id 查询（查不到抛异常——fail-fast）。</summary>
        public static T Get<T>(int id) where T : GameConfigBase
        {
            if (_table.TryGetValue(typeof(T), out var map))
            {
                if (map.TryGetValue(id, out var config))
                {
                    return (T)config;
                }
            }
            Assert.Fail($"ConfigTable: 找不到 {typeof(T).Name} id={id}");
            return null;
        }

        /// <summary>查询（找不到返回 null，不抛——供"可空引用"场景用）。</summary>
        public static T Find<T>(int id) where T : GameConfigBase
        {
            if (_table.TryGetValue(typeof(T), out var map))
            {
                if (map.TryGetValue(id, out var config))
                {
                    return (T)config;
                }
            }
            return null;
        }

        /// <summary>按资产名查询（事件 id 用资产名匹配——AssetName = SO 资产名）。</summary>
        public static T FindByName<T>(string assetName) where T : GameConfigBase
        {
            if (_table.TryGetValue(typeof(T), out var map))
            {
                foreach (var config in map.Values)
                {
                    if (config.name == assetName)
                    {
                        return (T)config;
                    }
                }
            }
            return null;
        }

        /// <summary>全部配置（事件池遍历用）。</summary>
        public static IEnumerable<T> All<T>() where T : GameConfigBase
        {
            if (_table.TryGetValue(typeof(T), out var map))
            {
                foreach (var config in map.Values)
                {
                    yield return (T)config;
                }
            }
        }

        /// <summary>清空（测试/整局重置用）。</summary>
        public static void Clear()
        {
            _table.Clear();
        }
    }
}
