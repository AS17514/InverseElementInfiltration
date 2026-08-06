using TheLaw.Core;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 面板基类：MonoBehaviour + IPanel。
    /// 子类实现 Key 与 OnShow/OnHide；用 Create&lt;T&gt; 创建实例（自动挂 UIRoot Canvas 下）。
    /// </summary>
    public abstract class PanelBase : MonoBehaviour, IPanel
    {
        public abstract string Key { get; }

        public void Show()
        {
            gameObject.SetActive(true);
            OnShow();
        }

        public void Hide()
        {
            OnHide();
            gameObject.SetActive(false);
        }

        protected virtual void OnShow() { }
        protected virtual void OnHide() { }

        /// <summary>确保根 Canvas 存在（ScreenSpaceCamera + 16:9 UI 摄像机）。</summary>
        protected static Canvas EnsureCanvas()
        {
            var canvas = Object.FindObjectOfType<Canvas>();
            if (canvas != null) return canvas;

            var uiCam = FindOrCreateUICamera();
            var root = new GameObject("UIRoot");
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = uiCam;
            canvas.planeDistance = 10f;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            root.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        /// <summary>查找场景 UI 摄像机；不存在则运行时创建（Editor 工具创建的是正式路径）。</summary>
        static Camera FindOrCreateUICamera()
        {
            var vp = Object.FindObjectOfType<UICameraViewport>();
            if (vp != null) return vp.GetComponent<Camera>();

            var go = new GameObject("UICamera");
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = LayerMask.GetMask("UI");
            cam.depth = 1;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.orthographicSize = 5.4f;
            go.AddComponent<UICameraViewport>();
            return cam;
        }

        /// <summary>创建面板实例并挂到 UIRoot 下。</summary>
        public static T Create<T>(string name = null) where T : PanelBase
        {
            var canvas = EnsureCanvas();
            var go = new GameObject(name ?? typeof(T).Name);
            go.transform.SetParent(canvas.transform, false);
            return go.AddComponent<T>();
        }

        /// <summary>
        /// Addressables 异步创建面板：加载 Assets/_Project/UI/Prefabs/ 下同名 prefab 实例化；
        /// 加载失败回退纯代码创建。
        /// </summary>
        public static void CreateAsync<T>(System.Action<T> onReady) where T : PanelBase
        {
            var address = typeof(T).Name;
            var handle = Addressables.LoadAssetAsync<GameObject>(address);
            handle.Completed += op =>
            {
                T panel;
                if (op.Status == AsyncOperationStatus.Succeeded && op.Result != null)
                {
                    var go = Object.Instantiate(op.Result);
                    go.name = address;
                    panel = go.GetComponent<T>();
                    if (panel == null) panel = go.AddComponent<T>();
                    var canvas = EnsureCanvas();
                    go.transform.SetParent(canvas.transform, false);
                }
                else
                {
                    panel = Create<T>();
                }
                onReady?.Invoke(panel);
            };
        }
    }
}
