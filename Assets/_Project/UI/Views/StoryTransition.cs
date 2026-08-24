using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 主菜单 → 开场剧情 运镜过渡（2026-08-25）：
    /// 前提：主菜单与剧情面板背景为同一张图、同一颜色叠加（0.5 灰）——过渡 = 剧情背景矩形从主菜单背景矩形
    /// 缓动到剧情 prefab 定义的矩形（运镜），主菜单根 CanvasGroup 缓出、剧情根缓入。
    /// 运镜终点 = 剧情 prefab 实例 Img_Bg 的初始矩形（改 prefab 即可控制终点）。
    /// 全程可跳过：任意键/鼠标点击 → 直接置终态（剧情面板加载完毕状态）。
    /// 挂点：Bootstrap.PlayOpeningStoryThenStartNewGame（主菜单→新局首播剧情）。
    /// 独立脚本：不依赖 PanelBase/UIManager 内部结构（仅按名找 Img_Bg / 根 CanvasGroup）。
    /// </summary>
    public static class StoryTransition
    {
        /// <summary>运镜时长（秒）——用户拍板 1.2s，OutQuad。</summary>
        public const float MoveSeconds = 1.2f;

        /// <summary>主菜单缓出/剧情缓入交叉时长（秒）。</summary>
        public const float FadeSeconds = 0.3f;

        private struct BgRect
        {
            public Vector2 anchorMin;
            public Vector2 anchorMax;
            public Vector2 anchoredPosition;
            public Vector2 sizeDelta;

            public static BgRect From(RectTransform rt)
            {
                return new BgRect
                {
                    anchorMin = rt.anchorMin,
                    anchorMax = rt.anchorMax,
                    anchoredPosition = rt.anchoredPosition,
                    sizeDelta = rt.sizeDelta,
                };
            }

            public void ApplyTo(RectTransform rt)
            {
                rt.anchorMin = anchorMin;
                rt.anchorMax = anchorMax;
                rt.anchoredPosition = anchoredPosition;
                rt.sizeDelta = sizeDelta;
            }

            public static BgRect Lerp(BgRect a, BgRect b, float t)
            {
                return new BgRect
                {
                    anchorMin = Vector2.Lerp(a.anchorMin, b.anchorMin, t),
                    anchorMax = Vector2.Lerp(a.anchorMax, b.anchorMax, t),
                    anchoredPosition = Vector2.Lerp(a.anchoredPosition, b.anchoredPosition, t),
                    sizeDelta = Vector2.Lerp(a.sizeDelta, b.sizeDelta, t),
                };
            }
        }

        /// <summary>执行主菜单→剧情运镜过渡（协程）。任一节点缺失 → 直接跳过过渡（面板照常切换）。</summary>
        public static IEnumerator Play(Transform menuRoot, Transform storyRoot)
        {
            if (menuRoot == null || storyRoot == null) yield break;

            var menuBg = FindDeep<Image>(menuRoot, "Img_Bg");
            var storyBg = FindDeep<Image>(storyRoot, "Img_Bg");
            if (menuBg == null || storyBg == null) yield break; // 背景节点缺失——跳过运镜

            // 剧情背景 home 矩形 = prefab 实例初始值（改 prefab 即改运镜终点）
            var storyHome = BgRect.From(storyBg.rectTransform);
            // 运镜起点 = 主菜单背景当前矩形（同图同色 → 无缝衔接）
            var menuRect = BgRect.From(menuBg.rectTransform);
            menuRect.ApplyTo(storyBg.rectTransform); // 剧情背景先落在主菜单背景位置（视觉上完全一致）

            var menuCg = menuRoot.GetComponent<CanvasGroup>();
            if (menuCg == null) menuCg = menuRoot.gameObject.AddComponent<CanvasGroup>();
            var storyCg = storyRoot.GetComponent<CanvasGroup>();
            if (storyCg == null) storyCg = storyRoot.gameObject.AddComponent<CanvasGroup>();

            menuCg.alpha = 1f;
            menuCg.blocksRaycasts = false; // 过渡期间主菜单按钮不可点（跳过由输入轮询负责）
            storyCg.alpha = 0f;
            storyCg.blocksRaycasts = false;

            bool skipped = false;

            // 阶段A：交叉淡入淡出（主菜单缓出 + 剧情缓入——同图同位，视觉无跳变）
            float t = 0f;
            while (t < FadeSeconds)
            {
                if (AnyInputDown()) { skipped = true; break; }
                t += Time.unscaledDeltaTime;
                float e = EaseOutQuad(Mathf.Clamp01(t / FadeSeconds));
                menuCg.alpha = 1f - e;
                storyCg.alpha = e;
                yield return null;
            }

            // 阶段B：运镜（剧情背景 主菜单矩形 → prefab 矩形）
            float mt = 0f;
            while (!skipped && mt < MoveSeconds)
            {
                if (AnyInputDown()) { skipped = true; break; }
                mt += Time.unscaledDeltaTime;
                float e = EaseOutQuad(Mathf.Clamp01(mt / MoveSeconds));
                BgRect.Lerp(menuRect, storyHome, e).ApplyTo(storyBg.rectTransform);
                yield return null;
            }

            // 终态（跳过 = 直接到这里）：剧情背景到位、双面板透明度终值
            storyHome.ApplyTo(storyBg.rectTransform);
            menuCg.alpha = 1f; // 复原（紧接着由调用方 HidePanel 隐藏——隐藏后 alpha 无关，避免下次显示时不可见）
            menuCg.blocksRaycasts = true;
            storyCg.alpha = 1f;
            storyCg.blocksRaycasts = true;
        }

        private static float EaseOutQuad(float t)
        {
            return 1f - (1f - t) * (1f - t);
        }

        /// <summary>任意键/鼠标/触摸按下（转场可跳过）。</summary>
        private static bool AnyInputDown()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame) return true;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame)) return true;
            var touch = UnityEngine.InputSystem.Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.wasPressedThisFrame) return true;
            return false;
        }

        private static T FindDeep<T>(Transform root, string name) where T : Component
        {
            foreach (var tr in root.GetComponentsInChildren<Transform>(true))
            {
                if (tr.name == name)
                {
                    var c = tr.GetComponent<T>();
                    if (c != null) return c;
                }
            }
            return null;
        }
    }
}
