using System;
using System.Collections;
using TheLaw.UI;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace TheLaw.UI
{
    /// <summary>
    /// 通用描述浮窗（2026-08-13 重构：单实例——hover 类提示一次一个，收敛各消费方加载/定位逻辑）。
    /// 智能定位：显示在目标旁、朝屏幕中心方向偏移（四边防出屏）；根 Canvas = ScreenSpaceCamera（PanelBase 确保）。
    /// 用法：TooltipManager.Instance.Show(text, worldPos) / ShowAtScreen(text, screenPos) / Hide()
    /// 预制体：TipPanel（Txt_Desc 描述文本）——Addressables 加载一次缓存。
    /// </summary>
    public class TooltipManager : MonoBehaviour
    {
        private static TooltipManager _instance;
        public static TooltipManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("TooltipManager");
                    DontDestroyOnLoad(go); // 常驻（跨局可用）
                    _instance = go.AddComponent<TooltipManager>();
                }
                return _instance;
            }
        }

        private GameObject _tipGo;
        private RectTransform _tipRect;
        private TooltipView _tipView;
        private bool _loading;
        private int _requestGeneration;
        private bool _requestedVisible;
        private TooltipViewData _requestedData;
        private Vector2 _requestedScreenPosition;

        /// <summary>显示描述浮窗（世界坐标——行为块/棋子旁；用主相机换算）。</summary>
        public void Show(TooltipViewData data, Vector3 worldPos)
        {
            Show(data, worldPos, Camera.main);
        }

        /// <summary>显示描述浮窗（世界坐标 + 显式相机——UI 元素必须传 canvas.worldCamera）。</summary>
        public void Show(TooltipViewData data, Vector3 worldPos, Camera cam)
        {
            if (cam == null) return;
            RequestShow(data, RectTransformUtility.WorldToScreenPoint(cam, worldPos));
        }

        /// <summary>显示描述浮窗（直接屏幕坐标——按钮旁）。</summary>
        public void ShowAtScreen(TooltipViewData data, Vector2 screenPos)
        {
            RequestShow(data, screenPos);
        }

        /// <summary>兼容字符串调用；统一转换为 TooltipViewData。</summary>
        public void Show(string text, Vector3 worldPos)
        {
            Show(new TooltipViewData(text), worldPos);
        }

        public void Show(string text, Vector3 worldPos, Camera cam)
        {
            Show(new TooltipViewData(text), worldPos, cam);
        }

        public void ShowAtScreen(string text, Vector2 screenPos)
        {
            ShowAtScreen(new TooltipViewData(text), screenPos);
        }

        void RequestShow(TooltipViewData data, Vector2 screenPos)
        {
            _requestGeneration++;
            _requestedVisible = true;
            _requestedData = data;
            _requestedScreenPosition = screenPos;
            StartCoroutine(EnsureLoaded(_requestGeneration));
        }

        /// <summary>隐藏浮窗，并废弃仍在加载中的旧 hover 请求。</summary>
        public void Hide()
        {
            _requestGeneration++;
            _requestedVisible = false;
            if (_tipGo != null) _tipGo.SetActive(false);
        }

        /// <summary>加载 TipPanel 一次；完成后只响应最新且仍有效的请求。</summary>
        IEnumerator EnsureLoaded(int requestGeneration)
        {
            if (_tipGo == null && !_loading)
            {
                _loading = true;
                var handle = Addressables.LoadAssetAsync<GameObject>("TipPanel");
                yield return handle;
                _loading = false;
                if (handle.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    Debug.LogWarning("[Tooltip] TipPanel 加载失败——描述浮窗不可用");
                    yield break;
                }
                var canvas = FindRootCanvas();
                if (canvas == null)
                {
                    Debug.LogWarning("[Tooltip] 根 Canvas 不存在——描述浮窗不可用");
                    yield break;
                }
                var go = Instantiate(handle.Result, canvas.transform);
                go.name = "Tooltip";
                var tipCanvas = go.GetComponent<Canvas>();
                if (tipCanvas == null) tipCanvas = go.AddComponent<Canvas>();
                tipCanvas.renderMode = canvas.renderMode;
                tipCanvas.worldCamera = canvas.worldCamera;
                tipCanvas.planeDistance = canvas.planeDistance;
                tipCanvas.overrideSorting = true;
                tipCanvas.sortingOrder = 1000;
                go.transform.SetAsLastSibling();
                _tipGo = go;
                _tipRect = go.GetComponent<RectTransform>();
                _tipView = go.GetComponent<TooltipView>();
                if (_tipView == null) _tipView = go.AddComponent<TooltipView>();
                go.SetActive(false);
                Addressables.Release(handle); // 实例已持有自身资源，不保留模板加载句柄
            }
            else if (_loading)
            {
                // 另一请求负责加载；它完成后会读取最新请求。
                yield break;
            }

            // 加载完成后应服务“最新请求”，不是发起加载的旧请求。
            if (_tipGo == null || !_requestedVisible) yield break;
            if (_tipView != null) _tipView.Bind(_requestedData);
            PlaceAt(_requestedScreenPosition);
            bool wasVisible = _tipGo.activeSelf;
            _tipGo.SetActive(true);
            // Tooltip 新浮窗出现碰撞音（2026-08-24 音频挂点方案；已显示时换内容/连续刷新不重复播）
            if (!wasVisible) UiSfx.Play();
        }

        /// <summary>顶层 Canvas（FindObjectOfType 会误命中运行时子 Canvas——手牌区 overrideSorting 等）。</summary>
        static Canvas FindRootCanvas()
        {
            var all = UnityEngine.Object.FindObjectsOfType<Canvas>();
            foreach (var c in all)
            {
                if (c.transform.parent == null) return c;
            }
            return null;
        }

        /// <summary>定位：朝屏幕中心方向偏移（四边防出屏）+ 12px 间隙。ScreenSpaceCamera 下 ScreenPointToLocalPoint 换算。</summary>
        void PlaceAt(Vector2 screenPos)
        {
            var canvas = _tipGo.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var canvasRT = (RectTransform)canvas.transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRT, screenPos, canvas.worldCamera, out var local)) return;

            float w = _tipRect.rect.width;
            float h = _tipRect.rect.height;
            float halfW = canvasRT.rect.width * 0.5f;
            float halfH = canvasRT.rect.height * 0.5f;
            const float gap = 12f;

            // 朝屏幕中心方向偏移（目标在右半屏 → 浮窗靠左；上半屏 → 靠下）——任意位置不出屏
            float x = local.x + (local.x > 0 ? -(w * 0.5f + gap) : (w * 0.5f + gap));
            float y = local.y + (local.y > 0 ? -(h * 0.5f + gap) : (h * 0.5f + gap));

            // 边界钳制（极端位置仍保完整可见）
            x = Mathf.Clamp(x, -halfW + w * 0.5f, halfW - w * 0.5f);
            y = Mathf.Clamp(y, -halfH + h * 0.5f, halfH - h * 0.5f);

            _tipRect.anchoredPosition = new Vector2(x, y);
        }
    }
}
