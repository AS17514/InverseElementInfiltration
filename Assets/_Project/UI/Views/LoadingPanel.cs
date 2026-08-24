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
        [SerializeField] private float fadeOutSeconds = 0.2f;
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
                .OnComplete(() => OnFadeInComplete?.Invoke());
            StartDots();
        }

        public override void Hide()
        {
            if (_fadingOut || !gameObject.activeSelf) return;
            _fadingOut = true;
            StopDots();
            if (_fade != null) _fade.Kill();
            _fade = DOTween.To(() => _cg.alpha, v => _cg.alpha = v, 0f, fadeOutSeconds).OnComplete(() =>
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
