namespace TheLaw.Core
{
    /// <summary>
    /// 存档快照契约：各系统自行序列化（Core 不知道游戏内容，只认接口）。
    /// 实现者：GameState / SettingsSystem / TutorialSystem / RandomManager / RunHistory。（ProgressSystem 已于 2026-08-26 移除——AA5-13 无调用方）
    /// </summary>
    public interface ISnapshot
    {
        /// <summary>快照唯一键（存档内部分发依据）。</summary>
        string Key { get; }

        /// <summary>序列化为 JSON 字符串。</summary>
        string ToJson();

        /// <summary>从 JSON 恢复。</summary>
        void FromJson(string json);
    }
}
