namespace TheLaw.Core
{
    /// <summary>
    /// 诊断开关（2026-08-23 第二梯队）：开启时记录随机用途标注 / 承伤与攻击结算结果 / AI 决策过程 ——
    /// 全部写入存档诊断数据（GameState.LogDiagnostic / RandomManager 随机缓冲）。
    /// ⚠️ 开发/编辑器开启（Debug.isDebugBuild 联动）；发布构建自动 false（避免存档回零膨胀）。
    /// 属性化（2026-08-23）：变更联动留单一挂点（如切换时清缓冲/打日志）。
    /// </summary>
    public static class Diagnostics
    {
        private static bool _verboseEnabled = UnityEngine.Debug.isDebugBuild; // 发布构建 false（isDebugBuild 联动），开发/编辑器保留
        public static bool VerboseEnabled
        {
            get => _verboseEnabled;
            set => _verboseEnabled = value;
        }
    }
}