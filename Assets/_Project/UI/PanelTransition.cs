using System;
using DG.Tweening;
using TheLaw.Core;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 面板切换过渡（2026-08-25 重构——覆盖优先）：
    /// 不变量①：旧面板在 loading 完全盖住前绝不移除（淡入全程旧面板保持完整显示——无闪断、无裸场景）；
    /// 不变量②：新面板在 loading 揭开前必须已显示（弹栈只做淡出揭示）。
    /// 时序：push loading → 淡入（旧面板保持）→ 渐入完毕延时 SwitchDelaySeconds → 隐藏旧+显示新（完全遮挡下）→ 淡出。
    /// 同步切换：ShowWithLoading(ui, key)（内部 Begin + End）。
    /// 异步切换（面板需先 Addressables 加载）：Begin(ui, onCovered)——onCovered 在遮挡就绪后执行
    /// （调用方隐藏旧面板/启动加载）；面板显示点调用 ShowWithLoading 自动走 busy 分支（ShowPanel + End）。
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

        private static bool _busy; // 过渡进行中（Begin 已开始未 End）

        public static void ShowWithLoading(UIManager ui, string key)
        {
            if (ui == null || string.IsNullOrEmpty(key)) return;

            // 同面板重复 Show（幂等刷新）不需要过渡
            if (ui.CurrentKey == key)
            {
                ui.ShowPanel(key);
                return;
            }

            if (_busy)
            {
                // Begin 流程中的面板显示：遮挡下显示即结束过渡（旧面板已卸载——延时淡出）
                ui.ShowPanel(key);
                ScheduleEnd(ui);
                return;
            }

            Begin(ui, () =>
            {
                ui.ShowPanel(key); // 遮挡下隐藏旧+显示新（新面板已就绪——后台加载完成后才进入）
                ScheduleEnd(ui);   // 旧面板卸载后延时淡出
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
                lp.OnFadeInComplete -= handler;
                DOVirtual.DelayedCall(SwitchDelaySeconds, Fire); // ② 渐入完毕后延时 0.5s
            };
            lp.OnFadeInComplete += handler;
            // 兜底①：渐入完成事件丢失（tween 被外部清理等）——超时强制继续，防卡死
            DOVirtual.DelayedCall(FadeInSeconds + SwitchDelaySeconds + 1f, Fire);
            // 兜底②：onCovered 后长时间未 End（流程异常）——自动弹栈防 loading 永久遮挡
            DOVirtual.DelayedCall(FadeInSeconds + SwitchDelaySeconds + 10f, () => End(ui));
        }

        /// <summary>结束过渡：弹栈淡出（新面板已显示，不变量②）。幂等——无进行中过渡时 no-op。</summary>
        public static void End(UIManager ui)
        {
            if (!_busy) return;
            if (ui != null) ui.PopOverlay(restoreCurrent: false); // ③ 弹栈淡出（LoadingPanel.Hide 负责）
            _busy = false;
        }

        /// <summary>切换完成后延时淡出（旧面板卸载 → 新面板进入 → 保持 PostSwitchHoldSeconds → 弹栈）。</summary>
        private static void ScheduleEnd(UIManager ui)
        {
            DOVirtual.DelayedCall(PostSwitchHoldSeconds, () => End(ui));
        }
    }
}
