namespace TheLaw.Core
{
    /// <summary>
    /// 面板接口（依赖倒置）：Core 定义接口，UI 层面板（PanelBase 派生）实现并主动注册进 UIManager。
    /// Core 不认识任何具体面板——依赖方向 UI → Core 保持合法。
    /// </summary>
    public interface IPanel
    {
        /// <summary>面板唯一键（UIManager 按此查找/切换）。</summary>
        string Key { get; }

        /// <summary>当前是否可见（UIManager 幂等判断用——面板自隐藏后 _current stale，靠此判定）。</summary>
        bool IsVisible { get; }

        /// <summary>暂停型面板（UI 架构重构 §四）：Push 显示时自动 GamePause（设置/确认等需冻结后台的面板 = true）。</summary>
        bool IsPausing { get; }

        /// <summary>显示面板。</summary>
        void Show();

        /// <summary>隐藏面板。</summary>
        void Hide();
    }
}
