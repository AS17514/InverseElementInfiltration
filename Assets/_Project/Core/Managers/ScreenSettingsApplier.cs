using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 显示设置应用器（前端职责）：监听 SettingsSystem.SettingsChanged → 把全屏/分辨率落地到 Screen。
    /// 规则：仅允许 16:9；全屏使用无边框窗口模式，非 16:9 屏幕由 UICameraViewport 补黑边；窗口化强制 16:9 分辨率。
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

            var r = ToNearest16To9(s.ResolutionWidth, s.ResolutionHeight);
            if (s.Fullscreen)
            {
                Screen.fullScreenMode = FullScreenMode.FullScreenWindow;
                var res = Screen.currentResolution;
                Screen.SetResolution(res.width, res.height, FullScreenMode.FullScreenWindow);
            }
            else
            {
                Screen.fullScreenMode = FullScreenMode.Windowed;
                Screen.SetResolution(r.x, r.y, false);
            }
        }

        static Vector2Int ToNearest16To9(int width, int height)
        {
            const float TargetAspect = 16f / 9f;
            if (width > 0 && height > 0 && Mathf.Abs((float)width / height - TargetAspect) < 0.01f)
            {
                return new Vector2Int(width, height);
            }

            if (height > 0)
            {
                int w = Mathf.Max(1, Mathf.RoundToInt(height * TargetAspect));
                return new Vector2Int(w, height);
            }

            if (width > 0)
            {
                int h = Mathf.Max(1, Mathf.RoundToInt(width / TargetAspect));
                return new Vector2Int(width, h);
            }

            return new Vector2Int(1920, 1080);
        }
    }
}