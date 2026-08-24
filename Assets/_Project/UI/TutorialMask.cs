using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 教程遮罩高亮：全屏压暗 + 挖孔（shader UI/TutorialMask）。
    /// 支持任意目标——场景对象（Renderer bounds）或 UI 对象（RectTransform）；
    /// 挖孔默认刚好框住目标，框与对象间距可调（Padding，支持 DOTween 缓动）。
    /// 全屏 Canvas 按 UI 根同款 ScreenSpaceCamera 创建，sortingOrder 顶层。
    /// </summary>
    public class TutorialMask : MonoBehaviour
    {
        public const string ShaderName = "UI/TutorialMask";
        public const int SortingOrder = 20000;

        const string PropDarkColor = "_DarkColor";
        const string PropHoleRect = "_HoleRect";
        const string PropPadding = "_Padding";
        const string PropHoleEnabled = "_HoleEnabled";

        Canvas _canvas;
        Material _material;
        readonly List<Transform> _targets = new List<Transform>();
        float _padding = 20f;
        float _darkAlpha = 0.62f;
        bool _holeEnabled = true;

        /// <summary>挖孔外扩边距（像素）。默认=刚框住目标。</summary>
        public float Padding => _padding;
        /// <summary>四周压暗强度（0=不压暗）。</summary>
        public float DarkAlpha => _darkAlpha;

        public bool IsReady => _material != null && _canvas != null;

        /// <summary>整层显隐（无高亮步骤时整层隐藏，避免误压暗全屏）。</summary>
        public void SetVisible(bool on)
        {
            if (_canvas != null) _canvas.gameObject.SetActive(on);
        }

        /// <summary>创建全屏遮罩（挂到 UI 根 Canvas 同相机下，sortingOrder 顶层）。</summary>
        public static TutorialMask Create(Camera uiCamera)
        {
            var go = new GameObject("TutorialMaskCanvas");
            go.layer = LayerMask.NameToLayer("UI");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = uiCamera;
            canvas.planeDistance = 10f;
            canvas.sortingOrder = SortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            go.AddComponent<GraphicRaycaster>();

            var imgGo = new GameObject("MaskImage");
            imgGo.transform.SetParent(go.transform, false);
            var rt = imgGo.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = imgGo.AddComponent<Image>();
            img.raycastTarget = false; // 不挡点击

            var mask = go.AddComponent<TutorialMask>();
            mask._canvas = canvas;
            mask._material = new Material(Shader.Find(ShaderName));
            if (mask._material == null)
            {
                Debug.LogWarning("[TutorialMask] 找不到 shader " + ShaderName + "（检查是否加入 Always Included / 构建包含）——遮罩降级为无效果。");
            }
            else
            {
                img.material = mask._material;
                mask._material.SetColor(PropDarkColor, new Color(0f, 0f, 0f, mask._darkAlpha));
                mask._material.SetVector(PropHoleRect, new Vector4(0, 0, 0, 0));
                mask._material.SetFloat(PropPadding, mask._padding);
                mask._material.SetFloat(PropHoleEnabled, 1f);
            }
            return mask;
        }

        /// <summary>指定高亮目标（UI RectTransform / 场景 Renderer / 任意 Transform 自动探测）。</summary>
        public void SetTarget(Transform target, float padding = 20f)
        {
            _targets.Clear();
            if (target != null) _targets.Add(target);
            _padding = padding;
            SetHoleEnabled(_targets.Count > 0);
        }

        /// <summary>多目标联合挖孔（如 手牌区+前两排）：挖孔 = 全部目标屏幕矩形并集。</summary>
        public void SetTargets(IList<Transform> targets, float padding = 20f)
        {
            _targets.Clear();
            if (targets != null)
            {
                foreach (var t in targets)
                {
                    if (t != null) _targets.Add(t);
                }
            }
            _padding = padding;
            SetHoleEnabled(_targets.Count > 0);
        }

        /// <summary>清除目标：全屏压暗（无挖孔）。</summary>
        public void ClearTarget()
        {
            _targets.Clear();
            SetHoleEnabled(false);
        }

        public void SetHoleEnabled(bool on)
        {
            _holeEnabled = on;
            if (_material != null) _material.SetFloat(PropHoleEnabled, on ? 1f : 0f);
        }

        public void SetDarkAlpha(float alpha)
        {
            _darkAlpha = alpha;
            if (_material != null) _material.SetColor(PropDarkColor, new Color(0f, 0f, 0f, alpha));
        }

        /// <summary>外扩边距缓动（入场放大/出场收缩）。</summary>
        public Tween TweenPadding(float to, float duration)
        {
            return DOTween.To(() => _padding, v => _padding = v, to, duration).SetEase(Ease.OutCubic);
        }

        public void SetPaddingImmediate(float padding)
        {
            _padding = padding;
        }

        void LateUpdate()
        {
            if (_material == null) return;
            _material.SetColor(PropDarkColor, new Color(0f, 0f, 0f, _darkAlpha));
            _material.SetFloat(PropPadding, _padding);
            if (!_holeEnabled || _targets.Count == 0)
            {
                _material.SetFloat(PropHoleEnabled, 0f);
                return;
            }
            _material.SetFloat(PropHoleEnabled, 1f);
            Rect r = ComputeScreenRect();
            if (r.width <= 0f || r.height <= 0f)
            {
                _material.SetFloat(PropHoleEnabled, 0f); // 目标不可见（屏幕外）→ 全屏压暗
                return;
            }
            _material.SetVector(PropHoleRect, new Vector4(r.xMin, r.yMin, r.xMax, r.yMax));
        }

        /// <summary>计算全部目标在屏幕像素空间的并集矩形（UI：世界角→屏幕；场景：Renderer bounds 8 角→屏幕）。</summary>
        Rect ComputeScreenRect()
        {
            if (_targets.Count == 0) return Rect.zero;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool any = false;
            foreach (var t in _targets)
            {
                Rect r = ComputeOneScreenRect(t);
                if (r.width <= 0f || r.height <= 0f) continue;
                any = true;
                minX = Mathf.Min(minX, r.xMin); maxX = Mathf.Max(maxX, r.xMax);
                minY = Mathf.Min(minY, r.yMin); maxY = Mathf.Max(maxY, r.yMax);
            }
            return any ? Rect.MinMaxRect(minX, minY, maxX, maxY) : Rect.zero;
        }

        Rect ComputeOneScreenRect(Transform target)
        {
            var targetRect = target != null ? target.GetComponent<RectTransform>() : null;
            if (targetRect != null)
            {
                var corners = new Vector3[4];
                targetRect.GetWorldCorners(corners);
                var cam = _canvas != null ? _canvas.worldCamera : null;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var c in corners)
                {
                    Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, c);
                    minX = Mathf.Min(minX, sp.x); maxX = Mathf.Max(maxX, sp.x);
                    minY = Mathf.Min(minY, sp.y); maxY = Mathf.Max(maxY, sp.y);
                }
                return Rect.MinMaxRect(minX, minY, maxX, maxY);
            }

            var renderer = target != null ? target.GetComponentInChildren<Renderer>() : null;
            if (renderer != null)
            {
                Camera cam = Camera.main;
                if (cam == null) return Rect.zero;
                Bounds b = renderer.bounds;
                var pts = new Vector3[8];
                pts[0] = b.min;
                pts[1] = b.max;
                pts[2] = new Vector3(b.min.x, b.min.y, b.max.z);
                pts[3] = new Vector3(b.min.x, b.max.y, b.min.z);
                pts[4] = new Vector3(b.max.x, b.min.y, b.min.z);
                pts[5] = new Vector3(b.min.x, b.max.y, b.max.z);
                pts[6] = new Vector3(b.max.x, b.min.y, b.max.z);
                pts[7] = new Vector3(b.max.x, b.max.y, b.min.z);
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var p in pts)
                {
                    Vector3 sp = cam.WorldToScreenPoint(p);
                    if (sp.z < 0f) continue; // 相机背后
                    minX = Mathf.Min(minX, sp.x); maxX = Mathf.Max(maxX, sp.x);
                    minY = Mathf.Min(minY, sp.y); maxY = Mathf.Max(maxY, sp.y);
                }
                if (minX > maxX || minY > maxY) return Rect.zero;
                return Rect.MinMaxRect(minX, minY, maxX, maxY);
            }
            return Rect.zero;
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
