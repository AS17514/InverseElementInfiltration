using System;
using DG.Tweening;
using TheLaw.Core;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 面板切换过渡（2026-08-25 用户定稿时序）：
    /// ① 收到切换指令 → loading 渐入（PushOverlay——LoadingPanel.Show 负责）
    /// ② 渐入动画【完毕】（OnFadeInComplete 事件，非同步）后延时 0.5s → 切换面板（ShowPanel）
    /// ③ 切换后 → loading 渐出（PopOverlay——LoadingPanel.Hide 负责）
    /// 豁免：主界面 → 剧情面板（调用点直显）；启动首个面板（LoadMainMenu 直显）；同面板重复 Show 直显。
    /// </summary>
    public static class PanelTransition
    {
        public const string LoadingKey = "Loading";

        /// <summary>渐入动画完毕后到切换面板的延时（秒）。</summary>
        public const float SwitchDelaySeconds = 0.5f;

        /// <summary>loading 淡入时长（秒）——与 LoadingPanel.fadeInSeconds 默认一致（兜底超时用）。</summary>
        public const float FadeInSeconds = 0.2f;

        private static bool _busy; // 过渡进行中防重（连续快速切换时后续切换直显，不被卡）

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
                ui.ShowPanel(key); // 过渡中再切换：直显（目标面板不被卡）
                return;
            }

            _busy = true;

            // ① 压栈 loading（LoadingPanel.Show 负责黑底快进 + 灰层淡入）
            ui.PushOverlay(LoadingKey);
            FadeOutCurrent(ui); // 上一个面板缓出（与黑底/灰层交叉淡出——切换时旧面板不瞬间消失）
            var lp = ui.GetPanel(LoadingKey) as LoadingPanel;
            bool switched = false;
            Action handler = null;

            void DoSwitch()
            {
                if (switched) return;
                switched = true;
                if (lp != null) lp.OnFadeInComplete -= handler;
                if (ui == null)
                {
                    _busy = false;
                    return;
                }
                ui.ShowPanel(key);                 // ② 渐入完毕 + 延时后切换面板
                ui.PopOverlay(restoreCurrent: false); // ③ 切换后立即渐出
                _busy = false;
            }

            if (lp == null)
            {
                DoSwitch(); // 面板缺失防御：直切（无过渡可做）
                return;
            }

            // 渐入动画完毕事件 → 延时 0.5s → 切换（非同步：等动画完成）
            handler = () =>
            {
                lp.OnFadeInComplete -= handler;
                if (switched) return;
                DOVirtual.DelayedCall(SwitchDelaySeconds, DoSwitch);
            };
            lp.OnFadeInComplete += handler;

            // 兜底：渐入完成事件丢失（tween 被外部清理等）——超时强制切换，防 loading 卡死
            DOVirtual.DelayedCall(FadeInSeconds + SwitchDelaySeconds + 1f, DoSwitch);
        }

        /// <summary>旧面板缓出：当前切换型面板 CanvasGroup alpha 1→0（0.2s，与 loading 淡入交叉）。
        /// 下次 Show 时 PanelBase.Show 复位 alpha=1。</summary>
        private static void FadeOutCurrent(UIManager ui)
        {
            if (ui == null || string.IsNullOrEmpty(ui.CurrentKey)) return; // 无当前面板（如剧情→首事件）——无可缓出
            var cur = ui.GetPanel(ui.CurrentKey);
            if (cur == null) return;
            var mb = cur as MonoBehaviour;
            if (mb == null || mb.gameObject == null) return;
            var cg = mb.GetComponent<CanvasGroup>();
            if (cg == null) cg = mb.gameObject.AddComponent<CanvasGroup>();
            DOTween.To(() => cg.alpha, v => cg.alpha = v, 0f, 0.2f);
        }
    }
}
