using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 全局暂停（UI 架构重构 §四：纯前端、不依赖后端——回合制后端只在"前端调用时"推进）。
    /// 计数式：暂停型面板（设置/确认）Push/Pop 对应加/减；计数归零才恢复 timeScale。
    /// timeScale=0 = 真时间静止（WaitForSeconds/DOTween 冻结；UGUI/InputSystem 输入不受影响）。
    /// </summary>
    public static class GamePause
    {
        private static int _depth;

        public static bool IsPaused => _depth > 0;

        /// <summary>暂停（计数++；0→1 时冻结时间）。</summary>
        public static void Push()
        {
            if (_depth == 0)
            {
                Time.timeScale = 0f;
            }
            _depth++;
        }

        /// <summary>恢复（计数--；1→0 时恢复时间）。</summary>
        public static void Pop()
        {
            if (_depth <= 0)
            {
                Debug.LogWarning("[GamePause] Pop 无对应 Push——计数已 0");
                return;
            }
            _depth--;
            if (_depth == 0)
            {
                Time.timeScale = 1f;
            }
        }

        /// <summary>硬重置（防御：异常路径/清场恢复时间）。</summary>
        public static void Reset()
        {
            _depth = 0;
            Time.timeScale = 1f;
        }
    }
}
