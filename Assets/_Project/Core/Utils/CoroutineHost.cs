using System.Collections;
using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 通用协程宿主（2026-08-21）——规则层纯 C# 类（BattleFlow 等）需要主动计时/协程时使用；
    /// SingletonAutoMono 常驻：BattleFlow 表现等待超时降级等。
    /// </summary>
    public class CoroutineHost : SingletonAutoMono<CoroutineHost>
    {
        public Coroutine Run(IEnumerator routine) => StartCoroutine(routine);
    }
}