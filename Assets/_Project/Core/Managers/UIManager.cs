using System.Collections.Generic;
using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 面板管理器（依赖倒置）：只认识 IPanel 接口，维护面板注册表 + 显示状态。
    /// 2026-08-12 重构（UI 面板显示架构）：
    /// - 切换型（一次一个）：_current 记录当前显示面板；ShowPanel 隐藏当前→显示目标；HidePanel 隐藏指定→_current 置空
    /// - 覆盖型（独立 overlay 栈）：PushOverlay 盖在切换型之上（置顶 + 不隐藏下层）；PopOverlay 隐藏 overlay + 恢复 pop 时刻 _current
    /// - ⚠️ 判空必须用 (UnityEngine.Object)panel == null——IPanel 接口上的 != null 走引用比较不触发 Unity 伪 null（已销毁面板判空恒真会抛 MissingReference）
    /// 实例由 Bootstrap 创建并显式传递（非单例——依赖可见）。
    /// </summary>
    public class UIManager
    {
        private readonly Dictionary<string, IPanel> _panels = new Dictionary<string, IPanel>();
        private readonly Stack<string> _overlayStack = new Stack<string>(); // 覆盖层栈（结算/弹窗）
        private string _current; // 当前显示的切换型面板（null=无）

        /// <summary>当前切换型面板 key（null=无——启动前/隐藏后）。面板切换过渡（PanelTransition）依赖。</summary>
        public string CurrentKey => _current;

        /// <summary>按 key 取面板（伪 null 防御——已销毁返回 null）。面板切换过渡（PanelTransition 等 LoadingPanel 渐入事件）用。</summary>
        public IPanel GetPanel(string key)
        {
            if (string.IsNullOrEmpty(key)) return null; // 防御：空键查字典抛 ArgumentNullException
            if (_panels.TryGetValue(key, out var panel) && (UnityEngine.Object)panel != null)
            {
                return panel;
            }
            _panels.Remove(key);
            return null;
        }

        /// <summary>注册面板（UI 层面板构造时主动调用；同 key 覆盖旧引用——会话面板随会话重建）。</summary>
        public void RegisterPanel(IPanel panel)
        {
            _panels[panel.Key] = panel;
        }

        /// <summary>切换型：隐藏当前显示的面板 → 显示目标（_current=key）。同 key 且已 active → 幂等跳过。</summary>
        public void ShowPanel(string key)
        {
            if (!_panels.TryGetValue(key, out var panel) || (UnityEngine.Object)panel == null) // 伪 null（接口判空无效——显式转 UnityEngine.Object）
            {
                _panels.Remove(key);
                if (_current == key) _current = null; // 早退也清理 _current（防残留指向已销毁面板）
                return;
            }
            // UI 架构重构 §三.1：Show 必 OnShow（移除幂等早退——重复 Show 也必须走 OnShow 刷新，
            // "像新加载"由面板 OnShow 自决；同 key 不重复隐藏当前）
            // 隐藏当前（伪 null 防御——BattlePanel 随会话销毁）
            if (_current != null && _current != key)
            {
                if (_panels.TryGetValue(_current, out var cur) && (UnityEngine.Object)cur != null)
                {
                    cur.Hide();
                }
                else
                {
                    _panels.Remove(_current);
                }
            }
            _current = key;
            bool wasVisible = panel.IsVisible; // 验收 A：暂停计数只在"隐藏→显示"时 Push——
            // 已显示面板重复 Show 不叠加计数（否则关闭只 Pop 一次 → 计数泄漏 → 永暂停卡死）
            panel.Show();
            if (panel.IsPausing && !wasVisible) GamePause.Push(); // 暂停型面板（设置/确认）→ 时间冻结（§四）
        }

        /// <summary>隐藏指定面板（不切换 _current；_current 相等则置空）。</summary>
        public void HidePanel(string key)
        {
            if (_panels.TryGetValue(key, out var panel) && (UnityEngine.Object)panel != null)
            {
                panel.Hide();
                if (panel.IsPausing) GamePause.Pop(); // 暂停型面板关闭 → 恢复时间（§四）
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

        /// <summary>覆盖型压栈：显示 overlay（置顶——盖住下层；不隐藏下层、不改 _current）。</summary>
        public void PushOverlay(string key)
        {
            if (_overlayStack.Contains(key)) return; // 同 key 去重：防多层同 overlay 压栈（PopOverlay 弹错对象/泄漏）
            if (!_panels.TryGetValue(key, out var panel) || (UnityEngine.Object)panel == null)
            {
                _panels.Remove(key);
                return;
            }
            _overlayStack.Push(key); // 栈存 overlay 自身 key（多层可叠）
            panel.Show();
            if (panel.IsPausing) GamePause.Push(); // 暂停型 overlay（设置/确认）→ 时间冻结（§四）
            // 置顶：overlay 必须渲染在下层之上（同根 Canvas 下渲染顺序=兄弟顺序——结算面板创建最早会被后建面板遮挡）
            if (panel is MonoBehaviour mb && mb.transform != null)
            {
                mb.transform.SetAsLastSibling();
            }
        }

        /// <summary>
        /// 覆盖型弹栈：隐藏 overlay 面板 → 恢复 pop 时刻的 _current（幂等 Show + 置顶）。
        /// ⚠️ overlay 不改变 _current——Pop 只需 Hide overlay + 恢复下层显示；不依赖 push 快照（失败路径下层=已销毁 Battle，靠 _current 现值兜底）。
        /// </summary>
        public void PopOverlay(bool restoreCurrent = true)
        {
            if (_overlayStack.Count == 0)
            {
                return;
            }
            var overlayKey = _overlayStack.Pop();
            // 隐藏被弹出的 overlay（否则结算确认后永不关闭）
            if (_panels.TryGetValue(overlayKey, out var ov) && (UnityEngine.Object)ov != null)
            {
                ov.Hide();
                if (ov.IsPausing) GamePause.Pop(); // 暂停型 overlay 关闭 → 恢复时间（§四）
            }
            else
            {
                _panels.Remove(overlayKey);
            }
            if (!restoreCurrent)
            {
                return; // 过渡弹栈（loading）：目标面板已显示，跳过恢复——恢复会重复 OnShow/RefreshLayout，遮挡渐出时露出面板刷新过程
            }
            // 恢复下层（pop 时刻的 _current——FinalizeRun 已把失败路径 _current 改写为 MainMenu）
            if (_current != null && _panels.TryGetValue(_current, out var cur) && (UnityEngine.Object)cur != null)
            {
                cur.Show(); // 幂等：已 active 的 Show 无害（面板 Show 内部 SetActive(true)）
                if (cur is MonoBehaviour mb && mb.transform != null)
                {
                    mb.transform.SetAsLastSibling(); // 恢复的下层置顶（保证在残余兄弟之上）
                }
            }
        }

        /// <summary>按 key 定向弹栈（过渡专用——2026-08-26）：从栈中移除指定 overlay 并隐藏，其上层/下层条目保持。
        /// ⚠️ 过渡期间可能有 Tutorial 等 overlay 压到 Loading 之上——End 必须弹 Loading 本身而非栈顶（防弹错对象致 Loading 滞留遮挡）。</summary>
        public void PopOverlay(string key)
        {
            if (!_overlayStack.Contains(key)) return;
            var list = new List<string>(_overlayStack);
            list.Remove(key);
            _overlayStack.Clear();
            for (int i = list.Count - 1; i >= 0; i--) _overlayStack.Push(list[i]); // 保持原顺序（栈顶在末）
            if (_panels.TryGetValue(key, out var panel) && (UnityEngine.Object)panel != null)
            {
                panel.Hide();
                if (panel.IsPausing) GamePause.Pop();
            }
            else
            {
                _panels.Remove(key);
            }
        }

        /// <summary>硬性全清（彻底退出重开用）——⚠️ 不进 FinalizeRun（会清掉正在显示的结算 overlay）。</summary>
        public void ClearAll()
        {
            foreach (var panel in _panels.Values)
            {
                if ((UnityEngine.Object)panel != null) panel.Hide();
            }
            _panels.Clear();
            _overlayStack.Clear();
            _current = null;
            GamePause.Reset(); // 清场必恢复时间（防暂停残留卡死）
        }
    }
}
