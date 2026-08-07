using System;

namespace TheLaw.Core
{
    /// <summary>
    /// 前置断言：时序/契约错误当场抛出（Assert 失败 = 测试失败）。
    /// 纯 C# 静态类，不依赖 UnityEngine。
    /// </summary>
    public static class Assert
    {
        /// <summary>条件不成立时抛异常。</summary>
        public static void IsTrue(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException($"断言失败: {message}");
            }
        }

        /// <summary>对象为 null 时抛异常。</summary>
        public static void IsNotNull(object obj, string message)
        {
            if (obj == null)
            {
                throw new InvalidOperationException($"断言失败: {message}");
            }
        }

        /// <summary>直接失败（到达不可达分支）。</summary>
        public static void Fail(string message)
        {
            throw new InvalidOperationException($"断言失败: {message}");
        }
    }
}
