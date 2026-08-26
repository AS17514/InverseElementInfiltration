using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// UI/主相机 16:9 视口保持器：
    /// - 始终把主相机和 UI 相机限制在 16:9 居中区域
    /// - 非 16:9 屏幕（全屏/窗口）外侧由黑底相机补黑
    /// - 窗口化时自动把窗口比例纠正回 16:9
    /// </summary>
    public class UICameraViewport : MonoBehaviour
    {
        const float TargetAspect = 16f / 9f;
        const float Tolerance = 0.001f;
        const float ResolutionRetryInterval = 0.5f; // SetResolution 节流（窗口管理器强制回非 16:9 时不再每帧重建窗口）

        Camera _cam;
        Camera _blackCam;
        int _lastScreenW, _lastScreenH; // 上次已应用的屏幕尺寸（未变则跳过 rect 写入）
        float _lastResolutionAttempt = -999f; // 上次 SetResolution 尝试时间（realtimeSinceStartup）

        void OnEnable()
        {
            _cam = GetComponent<Camera>();
            Apply();
        }

        void LateUpdate()
        {
            if (_cam == null) _cam = GetComponent<Camera>();
            Apply();
        }

        void Apply()
        {
            if (_cam == null) return;
            if (Screen.width <= 0 || Screen.height <= 0) return;

            EnsureBlackCamera();

            // 屏幕尺寸未变 → rect/aspect 未变：跳过相机 rect 写入（AA2-02）
            if (Screen.width == _lastScreenW && Screen.height == _lastScreenH)
            {
                LockWindowAspect(); // 节流后仅窗口比例不符时重试
                return;
            }
            _lastScreenW = Screen.width;
            _lastScreenH = Screen.height;

            var rect = GetLetterboxRect();
            _cam.rect = rect;
            if (Camera.main != null) Camera.main.rect = rect;
            // 背景相机（全屏实色/背景层）同步收进 16:9——否则会盖掉黑边区
            var bgCam = GameObject.Find("BackgroundCamera");
            if (bgCam != null)
            {
                var bg = bgCam.GetComponent<Camera>();
                if (bg != null) bg.rect = rect;
            }
            LockWindowAspect();
        }

        Rect GetLetterboxRect()
        {
            float screenAspect = (float)Screen.width / Screen.height;
            float scale = TargetAspect / screenAspect;
            if (scale >= 1f)
            {
                // 屏幕比 16:9 更高：左右满，上下黑边
                float h = 1f / scale;
                return new Rect(0f, (1f - h) * 0.5f, 1f, h);
            }
            else
            {
                // 屏幕比 16:9 更宽：上下满，左右黑边
                float w = scale;
                return new Rect((1f - w) * 0.5f, 0f, w, 1f);
            }
        }

        void EnsureBlackCamera()
        {
            if (_blackCam != null) return;
            var go = new GameObject("BlackBackgroundCamera");
            go.transform.SetParent(transform.parent);
            _blackCam = go.AddComponent<Camera>();
            _blackCam.clearFlags = CameraClearFlags.SolidColor;
            _blackCam.backgroundColor = Color.black;
            _blackCam.cullingMask = 0;
            _blackCam.depth = -100;
            _blackCam.rect = new Rect(0f, 0f, 1f, 1f);
        }

        void LockWindowAspect()
        {
            if (Screen.fullScreen) return;
            float screenAspect = (float)Screen.width / Screen.height;
            if (Mathf.Abs(screenAspect - TargetAspect) < Tolerance) return;

            // 节流：窗口管理器强制回非 16:9 时不再每帧 SetResolution（可能反复触发窗口重建）
            if (Time.realtimeSinceStartup - _lastResolutionAttempt < ResolutionRetryInterval) return;
            _lastResolutionAttempt = Time.realtimeSinceStartup;

            int w, h;
            if (screenAspect > TargetAspect)
            {
                h = Screen.height;
                w = Mathf.Max(1, Mathf.RoundToInt(h * TargetAspect));
            }
            else
            {
                w = Screen.width;
                h = Mathf.Max(1, Mathf.RoundToInt(w / TargetAspect));
            }
            Screen.SetResolution(w, h, false);
        }
    }
}