using System.Collections.Generic;
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

        /// <summary>内容就绪状态：默认 true（同步面板）。异步加载内容的面板在构建开始时置 false，完成后 NotifyContentReady。</summary>
        public bool IsContentReady { get; protected set; } = true;

        /// <summary>内容完全就绪事件——面板异步加载完成后触发；PanelTransition 等待此信号再淡出 loading（2026-08-25 检查点机制）。</summary>
        public event System.Action ContentReady;

        /// <summary>标记内容就绪并广播（幂等）。公开——Bootstrap/管理器可在异步面板加载完成后显式发就绪契约。</summary>
        public void NotifyContentReady()
        {
            if (!IsContentReady)
            {
                IsContentReady = true;
                ContentReady?.Invoke();
            }
        }

        public virtual void Show()
        {
            bool wasActive = gameObject.activeSelf;
            gameObject.SetActive(true);
            EnsureBgClick(); // 点击背景（Img_Bg 压暗层）关闭——非全屏面板通用协议
            RefreshLayout(); // 2026-08-23：进入面板全量刷新布局（动态内容重建后防错乱——一劳永逸）
            OnShow();
            // 面板打开碰撞音（2026-08-24 音频挂点方案）：仅"隐藏→显示"播——PopOverlay 恢复已显示下层时幂等不重复
            if (!wasActive) UiSfx.Play();
        }

        /// <summary>全量刷新本面板布局：ForceUpdateCanvases + 重建全部 LayoutGroup/ContentSizeFitter（动态添加/重建子节点后调用）。</summary>
        protected virtual void RefreshLayout()
        {
            Canvas.ForceUpdateCanvases();
            foreach (var lg in GetComponentsInChildren<LayoutGroup>(true))
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(lg.transform as RectTransform);
            }
            foreach (var csf in GetComponentsInChildren<ContentSizeFitter>(true))
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(csf.transform as RectTransform);
            }
        }

        public virtual void Hide()
        {
            bool wasActive = gameObject.activeSelf;
            OnHide();
            gameObject.SetActive(false);
            if (wasActive) UiSfx.Play(); // 面板关闭碰撞音（2026-08-24 音频挂点方案）
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
                    c.gameObject.name != "BackgroundCanvas" &&
                    c.gameObject.name != "TutorialMaskCanvas") // 教程遮罩 Canvas 挂根下置顶，不得被误当 UI 根
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
        public static void CreateAsync<T>(System.Action<T> onReady, string addressOverride = null) where T : PanelBase
        {
            var address = addressOverride ?? typeof(T).Name;
            // ⚠️ 2026-08-26 根治：Addressables 缺 key 会**内部** LogError（try/catch 接住也刷屏）——
            // 先探测注册态，未注册直接纯代码创建（prefab 缺失由面板内部兜底）。
            if (!IsAddressableRegistered(address))
            {
                Debug.LogWarning($"[PanelBase] Addressables 未注册 {address}——纯代码创建（无 prefab 布局时由面板内部兜底）");
                onReady?.Invoke(Create<T>());
                return;
            }
            AsyncOperationHandle<GameObject> handle;
            try
            {
                handle = Addressables.LoadAssetAsync<GameObject>(address);
            }
            catch (System.Exception e)
            {
                // ⚠️ 2026-08-26：Addressables 键缺失会**同步**抛 InvalidKeyException（不走 Completed 失败分支）——
                // 不接住会炸掉 Bootstrap.Awake 的整条协程链（实例：DeckLibrary 地址与类名不匹配 → 启动即卡死）。
                // 回退纯代码创建（面板无 prefab 布局时由面板内部兜底/报缺失）。
                Debug.LogWarning($"[PanelBase] Addressables 键缺失 {address}（{e.GetType().Name}）——回退纯代码创建");
                onReady?.Invoke(Create<T>());
                return;
            }
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

        /// <summary>Addressables 是否注册了该 key（Locate 探测——不触发缺 key 内部报错）。</summary>
        static bool IsAddressableRegistered(string address)
        {
            try
            {
                foreach (var locator in Addressables.ResourceLocators)
                {
                    if (locator != null && locator.Locate(address, typeof(GameObject), out var locations)
                        && locations != null && locations.Count > 0)
                    {
                        return true;
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PanelBase] Addressables 探测异常 {address}：{e.Message}——按未注册处理（纯代码创建）");
            }
            return false;
        }
    }

    /// <summary>内容区点击消费（2026-08-14）：空 handler 截断事件冒泡——点内容区不触发根背景 Button（关闭）。</summary>
    public class BgClickBlocker : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
    {
        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData eventData) { }
    }
}