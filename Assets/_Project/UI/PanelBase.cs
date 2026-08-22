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

        public virtual bool IsPausing => false; // 默认非暂停型；设置/确认等子类覆写 true

        public void Show()
        {
            gameObject.SetActive(true);
            EnsureBgClick(); // 点击背景（Img_Bg 压暗层）关闭——非全屏面板通用协议
            OnShow();
        }

        public void Hide()
        {
            OnHide();
            gameObject.SetActive(false);
        }

        protected virtual void OnShow() { }
        protected virtual void OnHide() { }

        /// <summary>点击背景（面板根节点 Image——全屏压暗层）自动关闭（确认/获取物品等非全屏面板覆写 true）。</summary>
        protected virtual bool CloseOnBgClick => false;

        /// <summary>
        /// 面板根挂/复用 Button：点击背景（根全屏 Image 露出的部分）= 关闭。
        /// 内容区（约定 Grp_ 子节点）加透明阻挡层——点内容不穿透到背景（不误关）。
        /// transition=None 防颜色过渡出戏。
        /// </summary>
        void EnsureBgClick()
        {
            if (!CloseOnBgClick) return;
            var btn = GetComponent<Button>();
            if (btn == null) btn = gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None; // ⚠️ 无颜色/图片过渡（点击背景不出戏）
            btn.onClick.RemoveAllListeners(); // 每次显示重新绑定（防重复）
            btn.onClick.AddListener(OnBgClicked);
            // 内容区阻挡：点击内容区被消费（不触发背景关闭）；内容区外（背景露出）点击才关
            // ⚠️ 递归查找 Grp_（可能嵌套在 Img_Bg 下——transform.Find 只找直接子会误中 Img_Bg 兜底 → 变透明）
            Transform content = null;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Grp_") { content = t; break; }
            }
            if (content != null)
            {
                var img = content.GetComponent<Image>();
                if (img == null) img = content.gameObject.AddComponent<Image>();
                img.color = new Color(1f, 1f, 1f, 0f); // 透明阻挡（不改视觉）
                img.raycastTarget = true;
                // ⚠️ 仅 raycastTarget 不够——事件冒泡会穿透到根 Button——必须消费点击（空 handler 截断）
                if (content.GetComponent<BgClickBlocker>() == null)
                {
                    content.gameObject.AddComponent<BgClickBlocker>();
                }
            }
        }

        /// <summary>背景点击回调（默认关闭面板——子类可覆写如 PopOverlay）。</summary>
        protected virtual void OnBgClicked()
        {
            Hide();
        }

        /// <summary>查找可复用的 UI 根 Canvas，忽略运行时子 Canvas 与 BackgroundCanvas。</summary>
        static Canvas FindRootCanvas()
        {
            var uiLayer = LayerMask.NameToLayer("UI");
            var all = Object.FindObjectsOfType<Canvas>();
            foreach (var c in all)
            {
                if (c.transform.parent == null &&
                    c.gameObject.layer == uiLayer &&
                    c.gameObject.name != "BackgroundCanvas")
                {
                    return c;
                }
            }
            return null;
        }

        /// <summary>确保 UI 根 Canvas 存在（ScreenSpaceCamera + 16:9 UI 摄像机；复用合格 UI Canvas 时自动升级）。</summary>
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

            // 仅复用已验证的 UI 根 Canvas；保留其 CanvasScaler 与 GraphicRaycaster 的现有配置。
            if (canvas.renderMode != RenderMode.ScreenSpaceCamera || canvas.worldCamera == null)
            {
                var uiCam = FindOrCreateUICamera();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = uiCam;
                canvas.planeDistance = 10f;
                if (canvas.GetComponent<CanvasScaler>() == null)
                {
                    canvas.gameObject.AddComponent<CanvasScaler>();
                }
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
                // 实例已脱离 prefab asset handle；释放本次加载引用，避免每次创建面板累积。
                Addressables.Release(op);
                onReady?.Invoke(panel);
            };
        }
    }

    /// <summary>内容区点击消费（2026-08-14）：空 handler 截断事件冒泡——点内容区不触发根背景 Button（关闭）。</summary>
    public class BgClickBlocker : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
    {
        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData eventData) { }
    }
}
