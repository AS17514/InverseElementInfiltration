using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 主菜单 → 开场剧情 运镜过渡（2026-08-26 v6）：
    /// 前提（prefab 配合）：剧情面板 Img_Bg 初始位置调成与主菜单 Img_Bg 一致（同取景 → 显示即无缝）。
    /// 时序：主菜单控件淡出（剧情面板未显示——淡出可见）→ 剧情面板显示（同图同位无缝接管）→
    ///       DOTween 运镜到 home → 剧情控件渐入。全程可跳过。
    /// home 为 2026-08-25 记录的剧情 prefab 原值——若以后改剧情终点，同步更新下方常量。
    /// 挂点：Bootstrap.PlayOpeningStoryThenStartNewGame（调用方先 CompleteIntro 落主菜单动画终态）。
    /// </summary>
    public static class StoryTransition
    {
        /// <summary>运镜时长（秒）——2026-08-26 定 3s，OutQuad。</summary>
        public const float MoveSeconds = 3f;

        /// <summary>主菜单控件淡出时长（秒）——2026-08-26 定 1s。</summary>
        public const float FadeSeconds = 1f;

        /// <summary>剧情控件渐入时长（秒）——2026-08-26 定 1s。</summary>
        public const float ContentFadeSeconds = 1f;

        /// <summary>剧情背景 home（2026-08-25 记录自 StoryPanel.prefab Img_Bg——anchor 0,0 / pivot 0.5）。</summary>
        private static readonly Vector2 HomePos = new Vector2(292.26f, 77.092f);
        private static readonly Vector2 HomeSize = new Vector2(3255.475f, 2005.8152f);

        /// <summary>执行转场（协程）。全程可跳过。</summary>
        public static IEnumerator Play(Transform menuRoot, StoryPanel storyPanel)
        {
            if (menuRoot == null || storyPanel == null) yield break;
            var storyRoot = storyPanel.transform;
            var storyBg = FindDeep<Image>(storyRoot, "Img_Bg");
            if (storyBg == null)
            {
                storyPanel.Show(); // 背景缺失——直接显示剧情（跳过运镜）
                yield break;
            }

            // 主菜单非背景对象（标题/副标题/按钮组）——淡出期间剧情面板未显示，淡出可见
            var fadeGroups = new List<CanvasGroup>();
            CollectGroup(menuRoot, "Txt_Title", fadeGroups);
            CollectGroup(menuRoot, "Txt_Subtitle", fadeGroups);
            CollectGroup(menuRoot, "Grp_MenuOptions", fadeGroups);
            foreach (var g in fadeGroups) g.alpha = 1f;
            var contentRoot = FindDeep<Transform>(storyRoot, "Img_TxtBg");

            bool skipped = false;

            // 阶段A：主菜单控件淡出（背景保持）
            float t = 0f;
            while (t < FadeSeconds)
            {
                if (AnyInputDown()) { skipped = true; break; }
                t += Time.unscaledDeltaTime;
                float e = EaseOutQuad(Mathf.Clamp01(t / FadeSeconds));
                foreach (var g in fadeGroups) g.alpha = 1f - e;
                yield return null;
            }

            // 剧情面板显示（背景同图同位 → 无缝接管画面）
            storyPanel.Show();

            // 阶段B：DOTween 运镜（剧情背景 → home；位置+尺寸，初始=主菜单同款 → 纯平移）
            var rt = storyBg.rectTransform;
            var startPos = rt.anchoredPosition;
            var startSize = rt.sizeDelta;
            var twPos = DOTween.To(() => startPos, v => rt.anchoredPosition = v, HomePos, MoveSeconds).SetEase(Ease.OutQuad).SetUpdate(true);
            var twSize = DOTween.To(() => startSize, v => rt.sizeDelta = v, HomeSize, MoveSeconds).SetEase(Ease.OutQuad).SetUpdate(true);

            float mt = 0f;
            while (!skipped && mt < MoveSeconds)
            {
                if (AnyInputDown()) { skipped = true; break; }
                mt += Time.unscaledDeltaTime;
                yield return null;
            }
            if (twPos.IsActive()) twPos.Kill();
            if (twSize.IsActive()) twSize.Kill();
            rt.anchoredPosition = HomePos; // 跳过 = 直接落终态
            rt.sizeDelta = HomeSize;

            // 阶段C：剧情控件渐入
            CanvasGroup contentCg = null;
            if (contentRoot != null)
            {
                contentRoot.gameObject.SetActive(true);
                contentCg = contentRoot.GetComponent<CanvasGroup>();
                if (contentCg == null) contentCg = contentRoot.gameObject.AddComponent<CanvasGroup>();
                contentCg.alpha = 0f;
            }
            float ct = 0f;
            while (ct < ContentFadeSeconds)
            {
                if (AnyInputDown()) { skipped = true; break; }
                ct += Time.unscaledDeltaTime;
                if (contentCg != null) contentCg.alpha = EaseOutQuad(Mathf.Clamp01(ct / ContentFadeSeconds));
                yield return null;
            }

            // 终态
            if (contentCg != null) contentCg.alpha = 1f;
            foreach (var g in fadeGroups) g.alpha = 1f; // 复原（紧接着由调用方 HidePanel 隐藏——避免下次显示时不可见）
        }

        /// <summary>收集主菜单需渐隐的分组（CanvasGroup 不存在则补建）。</summary>
        private static void CollectGroup(Transform root, string name, List<CanvasGroup> list)
        {
            var tr = FindDeep<Transform>(root, name);
            if (tr == null) return;
            var cg = tr.GetComponent<CanvasGroup>();
            if (cg == null) cg = tr.gameObject.AddComponent<CanvasGroup>();
            list.Add(cg);
        }

        private static float EaseOutQuad(float t)
        {
            return 1f - (1f - t) * (1f - t);
        }

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
