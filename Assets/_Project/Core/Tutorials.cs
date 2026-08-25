namespace TheLaw.Core
{
    /// <summary>
    /// 教程总开关（2026-08-25：测试/临时关闭用——仿 Diagnostics 开关先例"配置开关→属性留联动口子"）。
    /// Enabled = false 时：TutorialSystem.TryShow 直接返回 false（**不标记、不保存、不发事件**——tutorial.json 记录不受污染）；
    /// Bootstrap 不创建前端 TutorialManager（**防 TutorialPanel 面板未注册时 Awake 崩溃**）；
    /// TutorialSystem.LoadTutorials 跳过（不读盘）。
    /// ⚠️ **发布前置回 true**（与 Diagnostics.VerboseEnabled 同纪律）。
    /// </summary>
    public static class Tutorials
    {
        public static bool Enabled = true;
    }
}