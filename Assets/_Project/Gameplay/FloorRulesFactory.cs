using System;
using System.Collections.Generic;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 层规则注册表工厂：加层注册一行（Dictionary<int, Func<FloorRules>>）。
    /// </summary>
    public static class FloorRulesFactory
    {
        private static readonly Dictionary<int, Func<FloorRules>> _registry = new Dictionary<int, Func<FloorRules>>();

        /// <summary>注册层规则（floorIndex → 工厂）。</summary>
        public static void Register(int floorIndex, Func<FloorRules> factory)
        {
            _registry[floorIndex] = factory;
        }

        /// <summary>创建层规则（未注册 → 默认空实现）。</summary>
        public static FloorRules Create(int floorIndex)
        {
            if (_registry.TryGetValue(floorIndex, out var factory))
            {
                return factory();
            }
            return new DefaultFloorRules();
        }
    }
}
