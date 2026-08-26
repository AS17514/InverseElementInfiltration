using DG.Tweening;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>敌方升变预告视觉：独立材质实例驱动橙色 alpha 轮廓呼吸，并负责升变闪光。</summary>
    public sealed class PromotionOutlineView : MonoBehaviour
    {
        static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
        static readonly int OutlineSizeId = Shader.PropertyToID("_OutlineSize");
        static readonly int PulseId = Shader.PropertyToID("_Pulse");
        static readonly int FlashId = Shader.PropertyToID("_Flash");

        SpriteRenderer _renderer;
        Material _material;
        Tween _pulseTween;
        Tween _flashTween;
        bool _warningActive;      // 升变预告呼吸中——期间元素静态描边不得覆盖
        bool _hasElementColor;    // 已记录元素色（预告结束/隐藏后恢复静态描边）
        Color _elementColor = Color.white;

        public void Initialize(SpriteRenderer target)
        {
            if (_renderer == target && _material != null) return;
            CleanupMaterial();
            _renderer = target;
            if (_renderer == null) return;

            var shader = Resources.Load<Shader>("Shaders/SpriteOutlinePulse");
            if (shader == null) shader = Shader.Find("TheLaw/SpriteOutlinePulse");
            if (shader == null)
            {
                Debug.LogWarning("[PromotionOutline] 找不到 SpriteOutlinePulse shader");
                return;
            }

            _material = new Material(shader) { name = $"PromotionOutline_{gameObject.name}" };
            _material.SetColor(OutlineColorId, Color.red);
            // 采样半径按贴图 texel 计；棋子立绘缩放为 1/6，1.35 会降到亚像素不可见。
            _material.SetFloat(OutlineSizeId, 10f);
            _material.SetFloat(PulseId, 0f);
            _material.SetFloat(FlashId, 0f);
            _renderer.material = _material;
        }

        public void ShowWarning()
        {
            if (_material == null) Initialize(GetComponent<SpriteRenderer>());
            if (_material == null) return;
            _warningActive = true;
            KillPulse();
            _material.SetFloat(PulseId, 0.25f);
            _pulseTween = _material.DOFloat(1f, PulseId, 0.65f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo)
                .SetTarget(this);
        }

        public void HideWarning()
        {
            _warningActive = false;
            KillPulse();
            if (_material == null) return;
            if (_hasElementColor)
            {
                _material.SetColor(OutlineColorId, _elementColor);
                _material.SetFloat(PulseId, 1f); // 静态描边：常量满幅（Pulse=0 会被 shader 抹除轮廓）
            }
            else
            {
                _material.SetFloat(PulseId, 0f);
            }
        }

        /// <summary>五行静态描边（2026-08-25）：设置轮廓色 + 停止呼吸（不闪烁）——与升变预告共用组件。</summary>
        public void SetElementColor(Color color)
        {
            _elementColor = color;
            _hasElementColor = true;
            if (_material == null) Initialize(GetComponent<SpriteRenderer>());
            if (_material == null) return;
            if (_warningActive) return; // 升变预告优先——元素刷新不得覆盖红框呼吸（BuffsChanged 紧随 PromoteAnnounced 到达）
            KillPulse();
            _material.SetColor(OutlineColorId, color);
            _material.SetFloat(PulseId, 1f); // 静态描边：常量满幅（原 0 会被 shader 抹除轮廓）
        }

        public void PlayPromotionFlash()
        {
            HideWarning();
            if (_material == null) return;
            if (_flashTween != null) _flashTween.Kill();
            _material.SetFloat(FlashId, 0f);
            _flashTween = DOTween.Sequence()
                .Append(_material.DOFloat(1f, FlashId, 0.08f).SetEase(Ease.OutQuad))
                .Append(_material.DOFloat(0f, FlashId, 0.16f).SetEase(Ease.InQuad))
                .SetTarget(this);
        }

        public void SetSprite(Sprite sprite)
        {
            if (_renderer != null) _renderer.sprite = sprite;
        }

        void KillPulse()
        {
            if (_pulseTween != null) _pulseTween.Kill();
            _pulseTween = null;
        }

        void OnDestroy()
        {
            KillPulse();
            if (_flashTween != null) _flashTween.Kill();
            _flashTween = null;
            DOTween.Kill(this);
            CleanupMaterial();
        }

        void CleanupMaterial()
        {
            if (_material == null) return;
            DOTween.Kill(_material);
            if (Application.isPlaying) Destroy(_material);
            else DestroyImmediate(_material);
            _material = null;
        }
    }
}
