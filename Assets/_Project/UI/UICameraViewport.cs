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

        Camera _cam;
        Camera _blackCam;

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

            var rect = GetLetterboxRect();
            _cam.rect = rect;
            if (Camera.main != null) Camera.main.rect = rect;
            EnsureBlackCamera();
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
