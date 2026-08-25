using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 教程遮罩高亮：全屏压暗 + 挖孔（shader UI/TutorialMask）。
    /// 实现 = 教程面板内的一枚普通全屏 Image（同 Canvas、首子节点）：
    ///   - 不建独立 Canvas → 无跨 Canvas 排序变量，压暗必然在基础 UI 之上、教程内容之下
    ///   - 挖孔 = 目标在【屏幕像素】坐标系的矩形（center+size，含可调 padding）
    /// 支持任意目标（UI RectTransform / 场景 Renderer / 多目标联合），padding 可 DOTween 缓动。
    /// </summary>
    public class TutorialMask : MonoBehaviour
    {
        public const string ShaderName = "UI/TutorialMask";
        public const int SortingOrder = 20000; // 保留：极端兜底（无面板时挂 UI 根）用

        const string PropDarkColor = "_DarkColor";
        const string PropHoleCenter = "_HoleCenter";
        const string PropHoleSize = "_HoleSize";
        const string PropHoleEnabled = "_HoleEnabled";

        Image _image;
        Material _material;
        readonly List<Transform> _targets = new List<Transform>();
        float _padding = 20f;
        float _darkAlpha = 0.9f; // 压暗强度（用户定案 0.9）
        bool _holeEnabled = true;

        public float Padding => _padding;
        public float DarkAlpha => _darkAlpha;

        public bool IsReady => _material != null && _image != null;

        /// <summary>整层显隐（无高亮步骤时整层隐藏，避免误压暗全屏）。</summary>
        public void SetVisible(bool on)
        {
            if (gameObject != null) gameObject.SetActive(on);
        }

        /// <summary>创建全屏遮罩元素（无独立 Canvas——由 EnsureLayered 挂到教程面板下作首子节点）。</summary>
        public static TutorialMask Create(Camera uiCamera)
        {
            var go = new GameObject("TutorialMaskOverlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.layer = LayerMask.NameToLayer("UI");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = go.GetComponent<Image>();
            img.raycastTarget = false; // 不挡点击

            var mask = go.AddComponent<TutorialMask>();
            mask._image = img;
            mask._material = new Material(Shader.Find(ShaderName));
            if (mask._material == null)
            {
                Debug.LogWarning("[TutorialMask] 找不到 shader " + ShaderName + "（检查是否加入 Always Included / 构建包含）——遮罩降级为无效果。");
            }
            else
            {
                img.material = mask._material;
                mask._material.SetColor(PropDarkColor, new Color(0f, 0f, 0f, mask._darkAlpha));
                mask._material.SetVector(PropHoleCenter, Vector2.zero);
                mask._material.SetVector(PropHoleSize, Vector2.zero);
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

        /// <summary>多目标联合挖孔（如 手牌区+前两排）：挖孔 = 全部目标矩形并集。</summary>
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

        /// <summary>挖孔是否覆盖 ≥97% 屏幕（全屏级目标 = 无高亮意义）。</summary>
        public bool CoversMostOfScreen()
        {
            Rect r = ComputeUnionScreenRect();
            float total = (float)Screen.width * Screen.height;
            if (total <= 0f) return false;
            return (r.width * r.height) / total >= 0.97f;
        }

        void LateUpdate()
        {
            EnsureLayered(); // 层级自愈：挂教程面板下作首子节点 + 铺满（同一 Canvas，杜绝跨层变量）
            if (_material == null) return;
            _material.SetColor(PropDarkColor, new Color(0f, 0f, 0f, _darkAlpha));
            if (!_holeEnabled || _targets.Count == 0)
            {
                _material.SetFloat(PropHoleEnabled, 0f);
                return;
            }
            _material.SetFloat(PropHoleEnabled, 1f);
            Rect r = ComputeUnionScreenRect();
            if (r.width <= 0f || r.height <= 0f)
            {
                _material.SetFloat(PropHoleEnabled, 0f); // 目标不可见（屏幕外）→ 全屏压暗
                return;
            }
            r = new Rect(r.xMin - _padding, r.yMin - _padding, r.width + _padding * 2f, r.height + _padding * 2f);
            _material.SetVector(PropHoleCenter, r.center);
            _material.SetVector(PropHoleSize, r.size);
        }

        /// <summary>全部目标在屏幕像素坐标系的并集矩形。</summary>
        Rect ComputeUnionScreenRect()
        {
            if (_targets.Count == 0) return Rect.zero;
            Camera uiCam = FindUICamera();
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool any = false;
            foreach (var t in _targets)
            {
                Rect one = ComputeOneScreenRect(t, uiCam);
                if (one.width <= 0f || one.height <= 0f) continue;
                any = true;
                minX = Mathf.Min(minX, one.xMin); maxX = Mathf.Max(maxX, one.xMax);
                minY = Mathf.Min(minY, one.yMin); maxY = Mathf.Max(maxY, one.yMax);
            }
            return any ? Rect.MinMaxRect(minX, minY, maxX, maxY) : Rect.zero;
        }

        static Camera FindUICamera()
        {
            var vp = Object.FindObjectOfType<UICameraViewport>();
            if (vp != null)
            {
                var cam = vp.GetComponent<Camera>();
                if (cam != null) return cam;
            }
            return Camera.main;
        }

        /// <summary>单个目标 → 屏幕像素矩形（世界→屏幕，兼容 UI 与场景对象）。</summary>
        Rect ComputeOneScreenRect(Transform target, Camera uiCam)
        {
            var targetRect = target != null ? target.GetComponent<RectTransform>() : null;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool any = false;

            void AddScreenPoint(Vector2 sp)
            {
                any = true;
                minX = Mathf.Min(minX, sp.x); maxX = Mathf.Max(maxX, sp.x);
                minY = Mathf.Min(minY, sp.y); maxY = Mathf.Max(maxY, sp.y);
            }

            if (targetRect != null)
            {
                var corners = new Vector3[4];
                targetRect.GetWorldCorners(corners);
                foreach (var c in corners)
                {
                    AddScreenPoint(RectTransformUtility.WorldToScreenPoint(uiCam, c));
                }
                return any ? Rect.MinMaxRect(minX, minY, maxX, maxY) : Rect.zero;
            }

            var renderer = target != null ? target.GetComponentInChildren<Renderer>() : null;
            if (renderer != null)
            {
                Camera main = Camera.main;
                if (main == null) return Rect.zero;
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
                foreach (var p in pts)
                {
                    Vector3 sp = main.WorldToScreenPoint(p);
                    if (sp.z < 0f) continue; // 相机背后
                    AddScreenPoint(new Vector2(sp.x, sp.y));
                }
                return any ? Rect.MinMaxRect(minX, minY, maxX, maxY) : Rect.zero;
            }
            return Rect.zero;
        }

        /// <summary>
        /// 层级自愈：挂到教程面板 transform 下作首子节点（同 Canvas：基础 UI 之上、教程内容之下）。
        /// 面板不存在时退回 UI 根 Canvas。每帧纠正，杜绝任何顺序/残留问题。
        /// </summary>
        void EnsureLayered()
        {
            var panel = Object.FindObjectOfType<TutorialPanel>();
            Transform host = panel != null ? panel.transform : null;
            if (host == null)
            {
                var root = FindRootCanvas();
                if (root != null) host = root.transform;
            }
            if (host == null) return;
            if (transform.parent != host) transform.SetParent(host, false);
            transform.SetAsFirstSibling(); // 遮罩最下、教程内容最上
            var rt = transform as RectTransform;
            if (rt != null && (rt.anchorMin != Vector2.zero || rt.anchorMax != Vector2.one))
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
        }

        /// <summary>找 UI 根 Canvas（无父 + UI 层 + 非 BackgroundCanvas/非遮罩自身）。</summary>
        static Canvas FindRootCanvas()
        {
            foreach (var c in Object.FindObjectsOfType<Canvas>(true))
            {
                if (c.transform.parent == null &&
                    c.gameObject.layer == LayerMask.NameToLayer("UI") &&
                    c.gameObject.name != "BackgroundCanvas" &&
                    c.gameObject.name != "TutorialMaskOverlay")
                {
                    return c;
                }
            }
            return null;
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
