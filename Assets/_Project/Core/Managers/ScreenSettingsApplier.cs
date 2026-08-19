using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 显示设置应用器（前端职责）：监听 SettingsSystem.SettingsChanged → 把全屏/分辨率落地到 Screen。
    /// 由 Bootstrap 创建常驻；启动 LoadAll 读档触发 SettingsChanged 时也一并应用（设置跨启动生效）。
    /// </summary>
    public class ScreenSettingsApplier : MonoBehaviour
    {
        private void OnEnable()
        {
            SettingsSystem.Instance.SettingsChanged += Apply;
            Apply(); // 首次按当前值同步一次（默认/读档后都刷到屏幕）
        }

        private void OnDisable()
        {
            var s = SettingsSystem.Instance;
            if (s != null) s.SettingsChanged -= Apply;
        }

        private void Apply()
        {
            var s = SettingsSystem.Instance;
            if (s == null) return;
            Screen.fullScreen = s.Fullscreen;
            Screen.SetResolution(s.ResolutionWidth, s.ResolutionHeight, s.Fullscreen);
        }
    }
}
