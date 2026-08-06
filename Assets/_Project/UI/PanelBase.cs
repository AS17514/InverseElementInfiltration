using TheLaw.Core;
using UnityEngine;
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

        /// <summary>确保根 Canvas 存在（ScreenSpaceOverlay + 适配）。</summary>
        protected static Canvas EnsureCanvas()
        {
            var canvas = Object.FindObjectOfType<Canvas>();
            if (canvas != null) return canvas;
            var root = new GameObject("UIRoot");
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            root.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        /// <summary>创建面板实例并挂到 UIRoot 下。</summary>
        public static T Create<T>(string name = null) where T : PanelBase
        {
            var canvas = EnsureCanvas();
            var go = new GameObject(name ?? typeof(T).Name);
            go.transform.SetParent(canvas.transform, false);
            return go.AddComponent<T>();
        }
    }
}
