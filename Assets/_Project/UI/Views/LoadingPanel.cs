using System;
using System.Collections;
using DG.Tweening;
using TMPro;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 加载过渡面板（overlay 压栈——面板切换过渡用）。
    /// 背景 = prefab 原图（不透明灰底——原背景色正好，不叠加任何颜色层）；
    /// 淡入淡出用根节点 CanvasGroup（运行时补挂，不改 prefab）。
    /// 文字点号循环：Loading → Loading . → Loading . . → Loading . . .（WaitForSecondsRealtime）。
    /// Hide 覆写为异步淡出后再 SetActive(false)（PanelBase.Hide 已 virtual 化）。
    /// </summary>
    public class LoadingPanel : PanelBase
    {
        public override string Key => "Loading";

        [SerializeField] private float fadeInSeconds = 0.2f;
        [SerializeField] private float fadeOutSeconds = 0.5f; // 淡出加长——0.2s 在 1s 保持后几乎不可感知（用户反馈"直接隐藏"）
        [SerializeField] private float dotIntervalSeconds = 0.4f;

        private CanvasGroup _cg;
        private TMP_Text _text;
        private Coroutine _dots;
        private Tween _fade;
        private bool _fadingOut;

        /// <summary>渐入动画完成事件——PanelTransition 等待此事件后才延时切换面板（非同步关系）。</summary>
        public event Action OnFadeInComplete;

        private void Awake()
        {
            _cg = GetComponent<CanvasGroup>();
            if (_cg == null) _cg = gameObject.AddComponent<CanvasGroup>();
            _text = GetComponentInChildren<TMP_Text>(true);
            if (_text == null)
            {
                _text = CreateFallbackText(); // Addressables 缺失兜底（正常路径 prefab 已注册）
            }
        }

        public override void Show()
        {
            _fadingOut = false;
            _cg.alpha = 0f; // 激活前置 0——避免激活瞬间闪出
            base.Show();    // SetActive(true) + RefreshLayout + OnShow + 面板打开碰撞音
            if (_fade != null) _fade.Kill();
            _fade = DOTween.To(() => _cg.alpha, v => _cg.alpha = v, 1f, fadeInSeconds)
                .SetUpdate(true) // realtime——不受 timeScale=0 冻结（防淡入卡半透明滞留）
                .OnComplete(() => OnFadeInComplete?.Invoke());
            StartDots();
        }

        public override void Hide()
        {
            if (!gameObject.activeSelf) return;
            // ⚠️ 不用 _fadingOut 早退：上次淡出若被中断（tween 被清）残留 true 会让后续 Hide 静默跳过 → 面板被瞬时隐藏；改为重启淡出
            StopDots();
            if (_fade != null) _fade.Kill();
            _fadingOut = true;
            _fade = DOTween.To(() => _cg.alpha, v => _cg.alpha = v, 0f, fadeOutSeconds).SetUpdate(true) // realtime
                .OnComplete(() =>
            {
                _fadingOut = false;
                gameObject.SetActive(false);
            });
        }

        private void StartDots()
        {
            StopDots();
            _dots = StartCoroutine(DotsRoutine());
        }

        private void StopDots()
        {
            if (_dots != null)
            {
                StopCoroutine(_dots);
                _dots = null;
            }
        }

        private IEnumerator DotsRoutine()
        {
            while (true)
            {
                for (int n = 0; n <= 3; n++)
                {
                    if (_text != null) _text.text = "Loading" + new string('.', n);
                    yield return new WaitForSecondsRealtime(dotIntervalSeconds);
                }
            }
        }

        /// <summary>代码兜底：Addressables 加载失败（纯代码创建空物体）时自建居中 TMP 文本。</summary>
        private TMP_Text CreateFallbackText()
        {
            var go = new GameObject("Text (TMP)");
            go.transform.SetParent(transform, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = 48f;
            text.color = Color.white;
            text.text = "Loading";
            if (TMP_Settings.defaultFontAsset != null) text.font = TMP_Settings.defaultFontAsset;
            return text;
        }

        private void OnDestroy()
        {
            StopDots();
            if (_fade != null) _fade.Kill();
        }
    }
}
