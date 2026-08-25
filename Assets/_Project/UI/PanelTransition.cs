using System;
using System.Collections;
using TheLaw.Core;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 面板切换过渡（2026-08-25 覆盖优先 + 2026-08-26 泄漏修复）：
    /// 不变量①：旧面板在 loading 完全盖住前绝不移除；不变量②：新面板在 loading 揭开前必须已显示。
    /// 时序：push loading → 淡入 → 延时 SwitchDelaySeconds → 隐藏旧+显示新（完全遮挡下）→ 面板就绪（ContentReady 检查点）→ 淡出。
    /// ⚠️ 2026-08-26：所有延时改用【协程 + WaitForSecondsRealtime】——不依赖 DOVirtual.DelayedCall（可能被外部 DOTween.Kill*
    /// 清掉或 timeScale=0 冻结导致 End 永不执行 → Loading 滞留遮挡）；End 用 UIManager.PopOverlay(key) 定向弹 Loading——
    /// 过渡期间 Tutorial 等 overlay 压栈在 Loading 之上时不会弹错对象（否则 Loading 残留遮挡事件面板）。
    /// </summary>
    public static class PanelTransition
    {
        public const string LoadingKey = "Loading";

        /// <summary>loading 淡入时长（秒）——与 LoadingPanel.fadeInSeconds 默认一致。</summary>
        public const float FadeInSeconds = 0.2f;

        /// <summary>渐入完毕后到切换面板的延时（秒）。</summary>
        public const float SwitchDelaySeconds = 0.5f;

        /// <summary>切换完成后遮挡保持时长（秒）——旧面板卸载后 loading 延时淡出；≥1s 给图片/资源加载留足时间（用户定稿）。</summary>
        public const float PostSwitchHoldSeconds = 1f;

        /// <summary>收到面板 ContentReady 通知后到淡出的延时（秒）——内容已加载完，无需 1s 留白。</summary>
        public const float RevealDelaySeconds = 0.3f;

        /// <summary>ContentReady 信号丢失（面板销毁/异常）时的超时兜底（秒）——防 loading 卡死。</summary>
        public const float ReadyTimeoutSeconds = 8f;

        private static bool _busy; // 过渡进行中（Begin 已开始未 End）

        public static void ShowWithLoading(UIManager ui, string key, bool force = false)
        {
            if (ui == null || string.IsNullOrEmpty(key)) return;

            // 同面板重复 Show（幂等刷新）不需要过渡；force=true 强制走过渡（事件→事件内容切换——旧面板保持到遮挡就绪）
            if (!force && ui.CurrentKey == key)
            {
                ui.ShowPanel(key);
                return;
            }

            if (_busy)
            {
                // Begin 流程中的面板显示：遮挡下显示即结束过渡（旧面板已卸载——面板就绪后再淡出）
                ui.ShowPanel(key);
                EndWhenReady(ui, key); // 面板 ContentReady 通知后淡出（检查点——事件等异步面板加载完才揭盖）
                return;
            }

            Begin(ui, () =>
            {
                ui.ShowPanel(key); // 遮挡下隐藏旧+显示新（新面板已就绪——后台加载完成后才进入）
                EndWhenReady(ui, key); // 面板 ContentReady 通知后淡出（检查点）
            });
        }

        /// <summary>开始过渡：push loading → 淡入（旧面板保持完整显示）→ 延时 → onCovered（完全遮挡下执行）。</summary>
        public static void Begin(UIManager ui, Action onCovered)
        {
            if (ui == null)
            {
                onCovered?.Invoke();
                return;
            }
            if (_busy)
            {
                onCovered?.Invoke(); // 过渡中再触发：直接执行切换动作（不叠 loading）
                return;
            }
            _busy = true;
            ui.PushOverlay(LoadingKey); // ① 压栈 loading（淡入——旧面板保持，不变量①）
            var lp = ui.GetPanel(LoadingKey) as LoadingPanel;
            if (lp == null)
            {
                onCovered?.Invoke(); // 面板缺失防御：直切
                End(ui);
                return;
            }
            bool fired = false;
            void Fire()
            {
                if (fired) return;
                fired = true;
                onCovered?.Invoke();
            }
            Action handler = null;
            handler = () =>
            {
                if (lp != null) lp.OnFadeInComplete -= handler;
                DelayOn(ui, SwitchDelaySeconds, Fire); // ② 渐入完毕后延时 0.5s
            };
            lp.OnFadeInComplete += handler;
            // 兜底①：渐入完成事件丢失（tween 被外部清理等）——超时强制继续，防卡死
            DelayOn(ui, FadeInSeconds + SwitchDelaySeconds + 1f, Fire);
            // 兜底②：onCovered 后长时间未 End（流程异常）——自动弹栈防 loading 永久遮挡
            DelayOn(ui, FadeInSeconds + SwitchDelaySeconds + 10f, () => End(ui));
        }

        /// <summary>结束过渡：定向弹掉 Loading 并淡出（幂等——Loading 不在栈上时 no-op）。
        /// ⚠️ 用 PopOverlay(key) 而非弹栈顶：过渡期间 Tutorial 等 overlay 可能压到 Loading 之上——弹顶会弹错对象致 Loading 滞留。</summary>
        public static void End(UIManager ui)
        {
            if (ui != null) ui.PopOverlay(LoadingKey); // ③ 定向弹栈淡出（LoadingPanel.Hide 负责淡出）
            _busy = false;
        }

        /// <summary>目标面板就绪后延时淡出：面板有异步内容（IsContentReady=false）→ 协程轮询就绪（realtime，不受 DOTween Kill/timeScale 影响）→ 淡出；
        /// 无异步内容（或已就绪）→ 保持原 PostSwitchHoldSeconds 延时。超时兜底必弹栈。</summary>
        private static void EndWhenReady(UIManager ui, string key)
        {
            if (ui == null) return;
            var pb = ui.GetPanel(key) as PanelBase;
            if (pb == null || pb.IsContentReady)
            {
                ScheduleEnd(ui); // 无异步内容 → 原延时（≥1s 图片留白）
                return;
            }
            // 兜底计时（常驻宿主——目标面板销毁/协程中断也必弹栈）
            DelayOn(ui, ReadyTimeoutSeconds + RevealDelaySeconds, () => End(ui));
            var host = pb as MonoBehaviour;
            if (host != null && host.gameObject != null && host.isActiveAndEnabled)
            {
                host.StartCoroutine(EndWhenReadyRoutine(ui, pb));
            }
        }

        static IEnumerator EndWhenReadyRoutine(UIManager ui, PanelBase pb)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!pb.IsContentReady && Time.realtimeSinceStartup - t0 < ReadyTimeoutSeconds)
            {
                yield return null;
            }
            float hold = Time.realtimeSinceStartup + RevealDelaySeconds;
            while (Time.realtimeSinceStartup < hold)
            {
                yield return null;
            }
            End(ui); // 幂等——重复调用无副作用
        }

        /// <summary>切换完成后延时淡出（旧面板卸载 → 新面板进入 → 保持 PostSwitchHoldSeconds → 弹栈）。</summary>
        private static void ScheduleEnd(UIManager ui)
        {
            DelayOn(ui, PostSwitchHoldSeconds, () => End(ui));
        }

        /// <summary>常驻宿主上的 realtime 延时（LoadingPanel 优先、Bootstrap 兜底）——不受 DOTween Kill/timeScale 影响（End 保底路径）。</summary>
        private static void DelayOn(UIManager ui, float seconds, Action action)
        {
            var host = GetRunner(ui);
            if (host != null)
            {
                host.StartCoroutine(DelayRoutine(seconds, action));
            }
            else
            {
                action?.Invoke(); // 无宿主（防御）：直接执行
            }
        }

        private static MonoBehaviour GetRunner(UIManager ui)
        {
            var lp = ui != null ? ui.GetPanel(LoadingKey) as MonoBehaviour : null;
            if (lp != null && lp.gameObject != null && lp.isActiveAndEnabled) return lp;
            return UnityEngine.Object.FindObjectOfType<Bootstrap>(); // Bootstrap 常驻（DontDestroyOnLoad）——兜底宿主
        }

        static IEnumerator DelayRoutine(float seconds, Action action)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
            }
            action?.Invoke();
        }
    }
}
