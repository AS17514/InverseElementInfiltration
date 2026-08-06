namespace TheLaw.Core
{
    /// <summary>
    /// 可重入守卫：int 深度计数防重入（防双击/竞态）。
    /// 比 bool 防误解锁（嵌套调用安全），比状态机轻量（不进 UI 不广播）。
    /// 用法：执行操作前 TryEnter()，结束后 Exit()；TryEnter 返回 false = 已在执行中。
    /// </summary>
    public class ReentrantGuard
    {
        private int _depth;

        /// <summary>尝试进入。已在执行中（深度 &gt; 0）返回 false。</summary>
        public bool TryEnter()
        {
            if (_depth > 0)
            {
                return false;
            }
            _depth++;
            return true;
        }

        /// <summary>退出（深度减一）。</summary>
        public void Exit()
        {
            if (_depth > 0)
            {
                _depth--;
            }
        }

        /// <summary>当前是否在执行中。</summary>
        public bool IsLocked => _depth > 0;
    }
}
