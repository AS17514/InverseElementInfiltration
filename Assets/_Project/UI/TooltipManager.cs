using System;
using System.Collections;
using TheLaw.UI;
using TMPro;
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
        private TMP_Text _descText;
        private bool _loading;

        /// <summary>显示描述浮窗（世界坐标——行为块/棋子旁；用主相机换算）。</summary>
        public void Show(string text, Vector3 worldPos)
        {
            Show(text, worldPos, Camera.main);
        }

        /// <summary>显示描述浮窗（世界坐标 + 显式相机——⚠️ UI 元素必须传 canvas.worldCamera（UICamera），主相机斜俯视投影会错位）。</summary>
        public void Show(string text, Vector3 worldPos, Camera cam)
        {
            StartCoroutine(EnsureLoaded(() =>
            {
                if (cam == null) return;
                PlaceAt(RectTransformUtility.WorldToScreenPoint(cam, worldPos));
                _descText.text = text;
                _tipGo.SetActive(true);
            }));
        }

        /// <summary>显示描述浮窗（直接屏幕坐标——按钮旁）。</summary>
        public void ShowAtScreen(string text, Vector2 screenPos)
        {
            StartCoroutine(EnsureLoaded(() =>
            {
                PlaceAt(screenPos);
                _descText.text = text;
                _tipGo.SetActive(true);
            }));
        }

        /// <summary>隐藏浮窗。</summary>
        public void Hide()
        {
            if (_tipGo != null) _tipGo.SetActive(false);
        }

        /// <summary>加载 TipPanel 预制体（一次缓存）→ 就绪后回调；加载中重复调用忽略（下次再显示）。</summary>
        IEnumerator EnsureLoaded(Action onReady)
        {
            if (_tipGo != null)
            {
                onReady();
                yield break;
            }
            if (_loading)
            {
                yield break; // 加载中：忽略（防重复加载——下次 Show 自然触发）
            }
            _loading = true;
            var handle = Addressables.LoadAssetAsync<GameObject>("TipPanel");
            yield return handle;
            _loading = false;
            if (handle.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                Debug.LogWarning("[Tooltip] TipPanel 加载失败——描述浮窗不可用");
                yield break;
            }
            var canvas = FindRootCanvas(); // 根 Canvas（ScreenSpaceCamera——PanelBase 确保；无则跳过）
            if (canvas == null)
            {
                Debug.LogWarning("[Tooltip] 根 Canvas 不存在——描述浮窗不可用");
                yield break;
            }
            var go = Instantiate(handle.Result, canvas.transform);
            go.name = "Tooltip";
            _tipGo = go;
            _tipRect = go.GetComponent<RectTransform>();
            _descText = go.transform.Find("Txt_Desc")?.GetComponent<TMP_Text>();
            go.SetActive(false);
            onReady();
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
