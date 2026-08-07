using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// UI 摄像机动态 viewport：窗口比例 ≠ 16:9 时，视口居中 16:9，其余区域透出主相机（黑边）。
    /// 挂在 UI 摄像机上。
    /// </summary>
    [ExecuteInEditMode]
    public class UICameraViewport : MonoBehaviour
    {
        const float TargetAspect = 16f / 9f;
        Camera _cam;
        int _lastW = -1, _lastH = -1;

        void OnEnable()
        {
            _cam = GetComponent<Camera>();
            Apply();
        }

        void Update()
        {
            if (Screen.width != _lastW || Screen.height != _lastH)
            {
                Apply();
            }
        }

        void Apply()
        {
            _lastW = Screen.width;
            _lastH = Screen.height;
            if (_lastW <= 0 || _lastH <= 0 || _cam == null) return;

            float screenAspect = _lastW / (float)_lastH;
            Rect r;
            if (screenAspect > TargetAspect)
            {
                // 窗口比 16:9 宽：左右留黑边
                float w = TargetAspect / screenAspect;
                r = new Rect((1f - w) * 0.5f, 0f, w, 1f);
            }
            else
            {
                // 窗口比 16:9 窄：上下留黑边
                float h = screenAspect / TargetAspect;
                r = new Rect(0f, (1f - h) * 0.5f, 1f, h);
            }
            _cam.rect = r;
        }
    }
}
