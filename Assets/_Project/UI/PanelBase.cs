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

        public bool IsVisible => gameObject != null && gameObject.activeSelf;

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

        /// <summary>查找根 Canvas（顶层 Canvas）——FindObjectOfType 会误命中运行时子 Canvas（如手牌区 overrideSorting）。</summary>
        static Canvas FindRootCanvas()
        {
            var all = Object.FindObjectsOfType<Canvas>();
            foreach (var c in all)
            {
                if (c.transform.parent == null) return c;
            }
            return null;
        }

        /// <summary>确保根 Canvas 存在（ScreenSpaceCamera + 16:9 UI 摄像机；复用现有 Canvas 时自动升级）。</summary>
        protected static Canvas EnsureCanvas()
        {
            var canvas = FindRootCanvas();
            if (canvas == null)
            {
                var uiCam = FindOrCreateUICamera();
                var root = new GameObject("UIRoot");
                root.layer = LayerMask.NameToLayer("UI"); // UI 层（UICamera cullingMask 只渲染 UI 层——默认 Default 层不可见）
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

            // 复用现有 Canvas：renderMode 不符或未挂 UI 相机 → 强制升级为 ScreenSpaceCamera（Overlay+worldCam 残留也会被纠正）
            // 无条件保证 UI 层（UICamera cullingMask 只渲染 UI 层——非 UI 层 Canvas 相机捕捉不到）
            if (canvas.gameObject.layer != LayerMask.NameToLayer("UI"))
            {
                canvas.gameObject.layer = LayerMask.NameToLayer("UI");
            }
            if (canvas.renderMode != RenderMode.ScreenSpaceCamera || canvas.worldCamera == null)
            {
                var uiCam = FindOrCreateUICamera();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = uiCam;
                canvas.planeDistance = 10f;
                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; // 强制弹性适配（覆盖 ConstantPixelSize 残留）
                scaler.referenceResolution = new Vector2(1920, 1080);
                if (canvas.GetComponent<GraphicRaycaster>() == null)
                {
                    canvas.gameObject.AddComponent<GraphicRaycaster>();
                }
            }
            return canvas;
        }

        /// <summary>查找场景 UI 摄像机；不存在则运行时创建（Editor 工具创建的是正式路径）。</summary>
        static Camera FindOrCreateUICamera()
        {
            var vp = Object.FindObjectOfType<UICameraViewport>();
            if (vp != null)
            {
                var existingCam = vp.GetComponent<Camera>();
                existingCam.clearFlags = CameraClearFlags.Depth; // 强制全屏透明层（场景相机可能是旧 SolidColor）
                return existingCam;
            }

            var go = new GameObject("UICamera");
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.Depth; // 全屏 UI 层：透明区域透出主相机（棋盘）
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
