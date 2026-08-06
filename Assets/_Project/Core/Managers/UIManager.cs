using System.Collections.Generic;

namespace TheLaw.Core
{
    /// <summary>
    /// 面板管理器（依赖倒置）：只认识 IPanel 接口，维护面板注册表与栈。
    /// UI 层面板实现 IPanel 并主动注册——Core 不依赖 UI 层。
    /// 实例由 Bootstrap 创建并显式传递（非单例——依赖可见）。
    /// </summary>
    public class UIManager
    {
        private readonly Dictionary<string, IPanel> _panels = new Dictionary<string, IPanel>();
        private readonly Stack<string> _stack = new Stack<string>();

        /// <summary>注册面板（UI 层面板构造时主动调用）。</summary>
        public void RegisterPanel(IPanel panel)
        {
            _panels[panel.Key] = panel;
        }

        /// <summary>显示指定面板（隐藏当前栈顶，入栈）。</summary>
        public void ShowPanel(string key)
        {
            if (!_panels.TryGetValue(key, out var panel))
            {
                return;
            }
            if (_stack.Count > 0 && _panels.TryGetValue(_stack.Peek(), out var top))
            {
                top.Hide();
            }
            _stack.Push(key);
            panel.Show();
        }

        /// <summary>隐藏指定面板（直接隐藏，不动栈）。</summary>
        public void HidePanel(string key)
        {
            if (_panels.TryGetValue(key, out var panel))
            {
                panel.Hide();
            }
        }

        /// <summary>压栈显示（带返回语义的面板切换）。</summary>
        public void PushPanel(string key)
        {
            ShowPanel(key);
        }

        /// <summary>弹栈：隐藏当前，显示上一层。</summary>
        public void PopPanel()
        {
            if (_stack.Count == 0)
            {
                return;
            }
            var current = _stack.Pop();
            if (_panels.TryGetValue(current, out var panel))
            {
                panel.Hide();
            }
            if (_stack.Count > 0 && _panels.TryGetValue(_stack.Peek(), out var prev))
            {
                prev.Show();
            }
        }

        /// <summary>清空面板栈（整局结束回主菜单用）。</summary>
        public void ClearAll()
        {
            while (_stack.Count > 0)
            {
                _stack.Pop();
            }
        }
    }
}
