using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// UI 摄像机视口：全屏模式（UI 铺满，弹性布局随分辨率重排）。
    /// 挂在 UI 摄像机上。
    /// </summary>
    public class UICameraViewport : MonoBehaviour
    {
        Camera _cam;

        void OnEnable()
        {
            _cam = GetComponent<Camera>();
            Apply();
        }

        void Apply()
        {
            if (_cam != null) _cam.rect = new Rect(0f, 0f, 1f, 1f); // 全屏
        }
    }
}
