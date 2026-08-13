using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace TheLaw.Core
{
    /// <summary>
    /// 随机管理器：种子随机（随机 bug 可复现）。
    /// ⚠️ 种子/调用计数必须入快照——读档后随机序列不漂移。
    /// </summary>
    public class RandomManager : BaseManager<RandomManager>, ISnapshot
    {
        private Random _random = new Random(0);
        private int _seed;
        private int _callCount;

        public string Key => "RandomManager";

        /// <summary>重置种子（测试/复现用）。</summary>
        public void SetSeed(int seed)
        {
            _seed = seed;
            _callCount = 0;
            _random = new Random(seed);
        }

        /// <summary>[minInclusive, maxExclusive) 随机整数。</summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            _callCount++;
            return _random.Next(minInclusive, maxExclusive);
        }

        /// <summary>加权随机取一项（事件池抽取等）。items 空抛异常。</summary>
        public T NextWeighted<T>(IList<T> items, Func<T, float> weightGetter)
        {
            Assert.IsTrue(items.Count > 0, "NextWeighted: 候选为空");
            float total = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                total += Math.Max(0f, weightGetter(items[i]));
            }
            Assert.IsTrue(total > 0f, "NextWeighted: 总权重为 0");
            float roll = (float)(_random.NextDouble()) * total;
            float acc = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                acc += Math.Max(0f, weightGetter(items[i]));
                if (roll < acc)
                {
                    _callCount++;
                    return items[i];
                }
            }
            _callCount++;
            return items[items.Count - 1]; // 浮点误差兜底
        }

        // ---- ISnapshot ----

        public string ToJson()
        {
            return JsonConvert.SerializeObject(new RandomState { Seed = _seed, CallCount = _callCount });
        }

        public void FromJson(string json)
        {
            var state = JsonConvert.DeserializeObject<RandomState>(json);
            _seed = state.Seed;
            _callCount = state.CallCount;
            _random = new Random(_seed);
            // 重放 _callCount 次调用（保证读档后序列与存档时一致）。
            // ⚠️ 2026-08-13：补的方式必须与消耗方式一致（调用端只用 NextDouble——事件池抽取）——
            // 原用 Next() 整数补，两种方式内部消耗的随机原料数可能不同（旧 .NET 实现）→ 补不齐序列漂移。
            // 用同款 NextDouble 补 → 无论内部消耗几份原料都必然对齐。
            for (int i = 0; i < _callCount; i++)
            {
                _random.NextDouble();
            }
        }

        [Serializable]
        private class RandomState
        {
            public int Seed;
            public int CallCount;
        }
    }
}
