using System.Collections.Generic;
using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 面板管理器（依赖倒置）：只认识 IPanel 接口，维护面板注册表 + 显示状态。
    /// 2026-08-12 重构（UI 面板显示架构）：
    /// - 切换型（一次一个）：_current 记录当前显示面板；ShowPanel 隐藏当前→显示目标；HidePanel 隐藏指定→_current 置空
    /// - 覆盖型（独立 overlay 栈）：PushOverlay 盖在切换型之上（不隐藏下层）；PopOverlay 恢复 pop 时刻的 _current
    /// - 伪 null 防御：面板随会话销毁（BattlePanel）→ 判空 + 字典清理
    /// - 幂等：ShowPanel 同 key 且面板 active 才跳过（面板自隐藏后 _current stale——inactive 时仍执行 Show）
    /// 实例由 Bootstrap 创建并显式传递（非单例——依赖可见）。
    /// </summary>
    public class UIManager
    {
        private readonly Dictionary<string, IPanel> _panels = new Dictionary<string, IPanel>();
        private readonly Stack<string> _overlayStack = new Stack<string>(); // 覆盖层栈（结算/弹窗）
        private string _current; // 当前显示的切换型面板（null=无）

        /// <summary>注册面板（UI 层面板构造时主动调用；同 key 覆盖旧引用——会话面板随会话重建）。</summary>
        public void RegisterPanel(IPanel panel)
        {
            _panels[panel.Key] = panel;
        }

        /// <summary>切换型：隐藏当前显示的面板 → 显示目标（_current=key）。同 key 且已 active → 幂等跳过。</summary>
        public void ShowPanel(string key)
        {
            if (!_panels.TryGetValue(key, out var panel) || panel == null) // 伪 null 防御（面板已销毁）
            {
                _panels.Remove(key);
                return;
            }
            if (_current == key && panel.IsVisible)
            {
                return; // 幂等：同 key 且面板已显示
            }
            // 隐藏当前（伪 null 防御——BattlePanel 随会话销毁）
            if (_current != null && _current != key && _panels.TryGetValue(_current, out var cur) && cur != null)
            {
                cur.Hide();
            }
            else if (_current != null)
            {
                _panels.Remove(_current);
            }
            _current = key;
            panel.Show();
        }

        /// <summary>隐藏指定面板（不切换 _current；_current 相等则置空）。</summary>
        public void HidePanel(string key)
        {
            if (_panels.TryGetValue(key, out var panel) && panel != null)
            {
                panel.Hide();
            }
            else
            {
                _panels.Remove(key);
            }
            if (_current == key)
            {
                _current = null;
            }
        }

        /// <summary>覆盖型压栈：显示 overlay（不隐藏下层——下层保持 active）。</summary>
        public void PushOverlay(string key)
        {
            if (!_panels.TryGetValue(key, out var panel) || panel == null)
            {
                _panels.Remove(key);
                return;
            }
            _overlayStack.Push(_current); // 记录当前切换型（仅用于 null 恢复；Pop 时以当时 _current 为准）
            panel.Show();
        }

        /// <summary>
        /// 覆盖型弹栈：隐藏 overlay → 恢复 pop 时刻的 _current（幂等 Show）。
        /// ⚠️ 不恢复 push 快照——失败路径 push 时下层=已销毁的 Battle，恢复它必崩；FinalizeRun 已把 _current 改写为 MainMenu。
        /// </summary>
        public void PopOverlay()
        {
            if (_overlayStack.Count == 0)
            {
                return;
            }
            _overlayStack.Pop();
            if (_current != null && _panels.TryGetValue(_current, out var cur) && cur != null)
            {
                cur.Show(); // 幂等：已 active 的 Show 无害（面板 Show 内部 SetActive(true)）
            }
        }

        /// <summary>硬性全清（彻底退出重开用）——⚠️ 不进 FinalizeRun（会清掉正在显示的结算 overlay）。</summary>
        public void ClearAll()
        {
            foreach (var panel in _panels.Values)
            {
                if (panel != null) panel.Hide();
            }
            _panels.Clear();
            _overlayStack.Clear();
            _current = null;
        }
    }
}
