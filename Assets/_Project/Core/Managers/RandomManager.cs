using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace TheLaw.Core
{
    /// <summary>
    /// 随机管理器：种子随机（随机 bug 可复现）。
    /// ⚠️ 种子/调用计数必须入快照——读档后随机序列不漂移。
    /// 2026-08-23 诊断（第二梯队）：VerboseEnabled 开启时记录每次调用的来源方法名（[CallerMemberName] 可选参数——
    /// 不改调用点、不影响 CallCount 与读档对齐；默认关零开销）。
    /// </summary>
    public class RandomManager : BaseManager<RandomManager>, ISnapshot
    {
        // ⚠️ 2026-08-13：启动随机种子（原固定 0 → 每次 play 抽取序列完全确定 → 事件池永远抽同一事件"每次疾风之靴"）。
        // 读档路径 FromJson 会用存档种子覆盖（序列可复现保留）；SetSeed 供测试复现。
        private Random _random = new Random(Environment.TickCount);
        private int _seed = Environment.TickCount;
        private int _callCount;

        // 诊断缓冲（2026-08-23：随机用途标注——调用来源方法名；定长环形数组防膨胀与 RemoveAt(0) O(n) 移位；默认关）
        private const int DiagnosticCap = 2000;
        private readonly string[] _diagnosticRing = new string[DiagnosticCap];
        private int _diagnosticHead;   // 下一次写入位置
        private int _diagnosticCount;  // 有效条数（0..DiagnosticCap）

        public string Key => "RandomManager";

        /// <summary>重置种子（测试/复现用）。</summary>
        public void SetSeed(int seed)
        {
            _seed = seed;
            _callCount = 0;
            _random = new Random(seed);
            ClearDiagnosticRing();
        }

        /// <summary>[minInclusive, maxExclusive) 随机整数。
        /// ⚠️ 2026-08-19：改用 NextDouble 实现（原 `_random.Next(min,max)` 整数消耗与 FromJson 重放的
        /// NextDouble 补不一致 → 读档序列漂移——记忆 #18 警告场景；同款 NextDouble 补必然对齐）。</summary>
        public int Range(int minInclusive, int maxExclusive, [CallerMemberName] string caller = "")
        {
            _callCount++;
            TraceRandomCall(caller);
            int span = maxExclusive - minInclusive;
            if (span <= 0)
            {
                // span<=0 仍消耗一次 NextDouble——保持 _callCount 与 RNG 消耗一致（读档序列不漂移）
                _random.NextDouble();
                return minInclusive;
            }
            return minInclusive + (int)(_random.NextDouble() * span);
        }

        /// <summary>加权随机取一项（事件池抽取等）。items 空抛异常。</summary>
        public T NextWeighted<T>(IList<T> items, Func<T, float> weightGetter, [CallerMemberName] string caller = "")
        {
            Assert.IsTrue(items.Count > 0, "NextWeighted: 候选为空");
            float total = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                total += Math.Max(0f, weightGetter(items[i]));
            }
            if (total <= 0f)
            {
                // 总权重<=0 兜底：等权随机取一项（保留 Assert 仅用于候选空——配置权重异常不抛、不卡流程）
                return items[Range(0, items.Count, caller)];
            }
            float roll = (float)(_random.NextDouble()) * total;
            float acc = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                acc += Math.Max(0f, weightGetter(items[i]));
                if (roll < acc)
                {
                    _callCount++;
                    TraceRandomCall(caller);
                    return items[i];
                }
            }
            _callCount++;
            TraceRandomCall(caller);
            return items[items.Count - 1]; // 浮点误差兜底
        }

        /// <summary>诊断：记录"第几次随机调用 → 来源方法"（仅 VerboseEnabled 时；环形防膨胀）。</summary>
        private void TraceRandomCall(string caller)
        {
            if (!Diagnostics.VerboseEnabled) return;
            AddDiagnostic($"{_callCount}:{caller}");
        }

        private void AddDiagnostic(string entry)
        {
            _diagnosticRing[_diagnosticHead] = entry;
            _diagnosticHead = (_diagnosticHead + 1) % DiagnosticCap;
            if (_diagnosticCount < DiagnosticCap) _diagnosticCount++;
        }

        private void ClearDiagnosticRing()
        {
            _diagnosticHead = 0;
            _diagnosticCount = 0;
            Array.Clear(_diagnosticRing, 0, _diagnosticRing.Length);
        }

        /// <summary>诊断缓冲（按时间顺序快照——存档/外部查阅用）。</summary>
        private List<string> SnapshotDiagnosticCalls()
        {
            var list = new List<string>(_diagnosticCount);
            int start = _diagnosticCount < DiagnosticCap ? 0 : _diagnosticHead;
            for (int i = 0; i < _diagnosticCount; i++)
            {
                list.Add(_diagnosticRing[(start + i) % DiagnosticCap]);
            }
            return list;
        }

        /// <summary>诊断缓冲（存档查阅——存档时并入快照）。</summary>
        public List<string> DiagnosticCalls => SnapshotDiagnosticCalls();

        // ---- ISnapshot ----

        public string ToJson()
        {
            return JsonConvert.SerializeObject(new RandomState { Seed = _seed, CallCount = _callCount, DiagnosticCalls = SnapshotDiagnosticCalls() });
        }

        public void FromJson(string json)
        {
            var state = JsonConvert.DeserializeObject<RandomState>(json);
            _seed = state.Seed;
            _callCount = state.CallCount;
            _random = new Random(_seed);
            ClearDiagnosticRing();
            if (state.DiagnosticCalls != null)
            {
                foreach (var entry in state.DiagnosticCalls) AddDiagnostic(entry);
            }
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
            public List<string> DiagnosticCalls; // 2026-08-23 诊断（旧档缺省 null 兼容）
        }
    }
}
