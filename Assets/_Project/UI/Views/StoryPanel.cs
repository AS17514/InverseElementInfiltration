using System.Collections;
using System.Collections.Generic;
using System.IO;
using DG.Tweening;
using Newtonsoft.Json.Linq;
using TheLaw.Core;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 开场剧情播放器（StoryPanel 挂载脚本——prefab 无脚本，PanelBase.CreateAsync 运行时 AddComponent，prefab 不改）。
    /// 触发：主菜单"新游戏"→ Bootstrap 先播本面板 → 播完（含长按跳过）→ 继续原 StartNewGame 流程进第一个事件。
    /// 交互：任意点击/键盘任意键 = 下一句（打字中按下 = 立即显示整句）；
    ///       长按（按下持续约 0.8s）= 跳过整段剧情。
    /// 数据：Assets/Data/Configs/story_opening.json（运行时缺失 → LogWarning 并跳过剧情直接进流程）。
    /// 立绘：Img_Character_L（Xeon 主位）/ Img_Character_R（测试员右位）；出场渐入、未说话在场角色半透明（DOTween 缓动）；
    ///       cue.xeonDiff 切换 L 立绘 sprite（Addressables 按地址；缺失保留占位）。
    /// 音效：cue.sfx → AudioRefs.SfxStory*（进句播；AudioManager 无 StopSFX——短音不显式停上一句）。
    /// 背景：Img_Bg 常显背景图（prefab 主菜单同款）；黑屏阶段由 Img_BlackOverlay 叠层盖住，cue.bg=true 时叠层淡出。
    /// </summary>
    public class StoryPanel : PanelBase
    {
        public override string Key => "StoryPanel";

        /// <summary>剧情播放结束（全部播完或长按跳过）——Bootstrap 订阅后收尾并进入新局。</summary>
        public event System.Action Finished;

        // ====== 可调参数（Inspector 可见——打字机速度常量可调）======
        [Header("开场剧情")]
        [SerializeField, Tooltip("逐字间隔（秒）——打字机速度常量")] private float _charInterval = 0.03f;
        [SerializeField, Tooltip("长按跳过阈值（秒）")] private float _skipHoldSeconds = 1.5f;
        [SerializeField, Tooltip("立绘上下抖动幅度（DOTween punch）")] private float _shakeStrength = 10f;
        [SerializeField, Range(0f, 1f), Tooltip("未说话在场角色透明度（0.9 = 略降）")] private float _dimAlpha = 0.9f;
        [SerializeField, Tooltip("未说话在场角色颜色（偏深灰）")] private Color _dimColor = new Color(0.75f, 0.75f, 0.75f, 1f);
        [SerializeField, Tooltip("出场渐入/淡出时长（秒）")] private float _fadeDuration = 0.5f;
        [SerializeField, Tooltip("剧情结束退出前缓慢黑屏时长（秒）")] private float _exitFadeSeconds = 1.0f;
        [SerializeField, Range(0.98f, 1f), Tooltip("黑影缩放（0.98 = 微缩；亮相还原到 1）")] private float _silhouetteScale = 0.98f;
        [SerializeField, Tooltip("黑影颜色（0.9 半透明黑——亮相时还原纯白）")] private Color _silhouetteColor = new Color(0f, 0f, 0f, 0.9f);

        // ====== 节点引用（按名字 Find——prefab 层级变动容错；缺失判空跳过）======
        private TMP_Text _nameText;   // Txt_Name（说话人）
        private TMP_Text _contentText; // Txt_Content（对白——逐字打字机）
        private Image _charLeft;      // Img_Character_L（Xeon 主位）
        private Image _charRight;     // Img_Character_R（测试员右位）

        private List<StoryCue> _cues; // 当前局剧情
        private int _cueIndex;
        private bool _playing;
        private bool _typing;         // 打字中（按下 = 立即显示整句）
        private bool _stopTyping;     // 打断打字（整句立即显示）
        private Coroutine _playRoutine;
        private Coroutine _typeRoutine;
        private Tween _shakeTween;
        private Tween _leftAlphaTween;
        private Tween _rightAlphaTween;

        // 舞台状态（角色在场/说话）——未说话半透明 + 出场渐入
        private bool _xeonOnStage;
        private bool _testerOnStage;
        private bool _xeonSilhouette;   // Xeon 黑影态（纯黑+缩小；亮相时 DOTween 还原）
        private bool _testerSilhouette;
        private Tween _leftRevealTween;
        private Tween _rightRevealTween;
        private Coroutine _revealRoutineL; // 亮相兜底协程（DOTween 不推进时保证还原到纯白+缩放1）
        private Coroutine _revealRoutineR;
        private Coroutine _silhouetteRoutineL; // 黑影渐显协程（alpha 0 → 0.9）
        private Coroutine _silhouetteRoutineR;

        // 退出黑屏
        private Image _exitFade;
        private bool _exiting;

        // 输入/长按
        private bool _pressing;          // 当前有按下
        private float _pressStartTime;   // 按下起始（unscaled）
        private bool _pressCompletedTyping; // 本次按下已用于"整句显示"——释放短按时不再推进下一句
        private bool _awaitingNext;      // 当前句完整显示，等待"下一句"输入
        private bool _skipAll;           // 长按达成——跳过整段

        private readonly HashSet<string> _missingDiffLogged = new HashSet<string>(); // 缺失差分防刷屏
        private bool _missingRightLogged;
        private bool _missingLeftLogged;

        private void Awake()
        {
            // prefab 路径：CreateAsync 加载含完整布局的 prefab（AddComponent 后 Awake 触发）——有子节点则跳过代码构建（防双份 UI）
            if (transform.childCount == 0)
            {
                Build();
            }
            CacheRefs();
        }

        private void CacheRefs()
        {
            _nameText = FindDeep<TMP_Text>(transform, "Txt_Name");
            _contentText = FindDeep<TMP_Text>(transform, "Txt_Content");
            _charLeft = FindDeep<Image>(transform, "Img_Character_L");
            _charRight = FindDeep<Image>(transform, "Img_Character_R");
            if (_charRight == null) Debug.Log("[StoryPanel] 未找到 Img_Character_R（李毕拼图后自动生效——缺失期间测试员句跳过右立绘）");
            if (_charLeft == null) Debug.LogWarning("[StoryPanel] 未找到 Img_Character_L——Xeon 立绘不可用");
            if (_contentText == null) Debug.LogWarning("[StoryPanel] 未找到 Txt_Content——对白无法显示");
            // 判别日志：L/R 节点是否找到 = 加载的是真 prefab（true）还是代码兜底 Build（false）
            Debug.Log($"[StoryPanel] 节点判定：L={_charLeft != null} R={_charRight != null} 子节点数={transform.childCount}");
        }

        // ====== 对外 API ======

        /// <summary>加载开场剧情配置；返回 false = 配置缺失/为空（已 LogWarning——调用方跳过剧情直接进流程）。</summary>
        public bool PlayOpening()
        {
            _cues = null;
            if (!TryLoadCues(out var loaded) || loaded == null || loaded.Count == 0)
            {
                Debug.LogWarning("[StoryPanel] 开场剧情配置缺失或为空——跳过剧情直接进入流程");
                return false;
            }
            _cues = loaded;
            Debug.Log($"[StoryPanel] 开场剧情就绪：{loaded.Count} 句");
            return true;
        }

        protected override void OnShow()
        {
            _cueIndex = 0;
            _skipAll = false;
            _awaitingNext = false;
            _pressing = false;
            // 舞台复位：立绘全隐藏、黑影/缩放/颜色硬复位（每局播放干净起点）
            _xeonOnStage = false;
            _testerOnStage = false;
            _xeonSilhouette = false;
            _testerSilhouette = false;
            _exiting = false;
            ResetPortraits();
            if (_cues != null && _cues.Count > 0 && !_playing)
            {
                _playRoutine = StartCoroutine(PlayRoutine());
            }
        }

        protected override void OnHide()
        {
            StopPlayback();
        }

        private void StopPlayback()
        {
            _playing = false;
            _typing = false;
            _awaitingNext = false;
            _skipAll = false;
            if (_playRoutine != null) { StopCoroutine(_playRoutine); _playRoutine = null; }
            if (_typeRoutine != null) { StopCoroutine(_typeRoutine); _typeRoutine = null; }
            if (_shakeTween != null && _shakeTween.IsActive()) { _shakeTween.Kill(); _shakeTween = null; }
            if (_leftAlphaTween != null && _leftAlphaTween.IsActive()) { _leftAlphaTween.Kill(); _leftAlphaTween = null; }
            if (_rightAlphaTween != null && _rightAlphaTween.IsActive()) { _rightAlphaTween.Kill(); _rightAlphaTween = null; }
            if (_leftRevealTween != null && _leftRevealTween.IsActive()) { _leftRevealTween.Kill(); _leftRevealTween = null; }
            if (_rightRevealTween != null && _rightRevealTween.IsActive()) { _rightRevealTween.Kill(); _rightRevealTween = null; }
            if (_revealRoutineL != null) { StopCoroutine(_revealRoutineL); _revealRoutineL = null; }
            if (_revealRoutineR != null) { StopCoroutine(_revealRoutineR); _revealRoutineR = null; }
            if (_silhouetteRoutineL != null) { StopCoroutine(_silhouetteRoutineL); _silhouetteRoutineL = null; }
            if (_silhouetteRoutineR != null) { StopCoroutine(_silhouetteRoutineR); _silhouetteRoutineR = null; }
        }

        // ====== 播放主流程 ======

        private IEnumerator PlayRoutine()
        {
            _playing = true;
            for (_cueIndex = 0; _cueIndex < _cues.Count; _cueIndex++)
            {
                var cue = _cues[_cueIndex];
                ApplyCue(cue);
                if (!string.IsNullOrEmpty(cue.text))
                {
                    yield return _typeRoutine = StartCoroutine(TypeLine(cue.text));
                }
                if (_skipAll) break;

                // 等待"下一句"输入（短按；长按已置 _skipAll 跳过整段）
                _awaitingNext = true;
                while (_awaitingNext && !_skipAll)
                {
                    yield return null;
                }
                if (_skipAll) break;
            }
            // 正常播完：缓慢黑屏后退出（长按跳过不走黑屏）
            if (!_skipAll && _exitFadeSeconds > 0f)
            {
                _exiting = true;
                yield return StartCoroutine(ExitFadeRoutine());
            }
            _playing = false;
            yield return null; // 等一帧：回调栈外销毁面板安全（Bootstrap 收尾）
            Finished?.Invoke();
        }

        /// <summary>退出前缓慢黑屏：全屏黑 Image（临时创建，随面板销毁）alpha 0→1 缓入。</summary>
        private System.Collections.IEnumerator ExitFadeRoutine()
        {
            if (_exitFade == null)
            {
                var go = new GameObject("Img_ExitFade", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(transform, false);
                go.transform.SetAsLastSibling(); // 最顶层
                _exitFade = go.GetComponent<Image>();
                _exitFade.color = new Color(0f, 0f, 0f, 0f);
                _exitFade.raycastTarget = false;
                Stretch(go.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            }
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.001f, _exitFadeSeconds);
                var c = _exitFade.color; c.a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t)); _exitFade.color = c;
                yield return null;
            }
            var cf = _exitFade.color; cf.a = 1f; _exitFade.color = cf;
        }

        /// <summary>进入一句：背景/说话人/立绘/差分/抖动/音效一次性应用（文本由打字机逐字上屏）。</summary>
        private void ApplyCue(StoryCue cue)
        {
            // 背景/黑屏完全交给 prefab 与场景转场（2026-08-24 李毕定：脚本不再生成叠层）
            if (_nameText != null) _nameText.text = cue.speaker ?? string.Empty;
            if (_contentText != null) _contentText.text = string.Empty;

            ApplyCharacter(cue, out var activeChar);
            if (cue.shake && activeChar != null) Shake(activeChar);

            // Xeon 差分：切换 Img_Character_L 的 sprite（Addressables 按地址；缺失保留占位并仅提示一次）
            if (!string.IsNullOrEmpty(cue.xeonDiff) && _charLeft != null)
            {
                ApplyXeonDiff(cue.xeonDiff);
            }
            if (!string.IsNullOrEmpty(cue.sfx)) PlaySfx(cue.sfx);
        }

        // ====== 打字机 ======

        private IEnumerator TypeLine(string full)
        {
            if (_contentText == null || string.IsNullOrEmpty(full))
            {
                _typing = false;
                yield break;
            }
            _typing = true;
            _stopTyping = false;
            _contentText.text = string.Empty;
            for (int i = 1; i <= full.Length; i++)
            {
                if (_stopTyping || _skipAll) break; // 按下打断 → 立即整句；长按跳过 → 同样整句后收尾
                _contentText.text = full.Substring(0, i);
                yield return new WaitForSecondsRealtime(Mathf.Max(0.001f, _charInterval)); // Realtime：暂停型面板也不冻结打字
            }
            _contentText.text = full; // 打断/正常结束都落完整句
            _typing = false;
        }

        /// <summary>打字中按下 → 立即显示整句（不推进下一句——释放短按时由 _pressCompletedTyping 拦截）。</summary>
        private void CompleteTyping()
        {
            _stopTyping = true;
        }

        // ====== 立绘/差分/抖动 ======

        /// <summary>
        /// 角色舞台状态（出场渐入 + 未说话半透明）：
        /// - cue.showXeon/showTester → 对应角色入场（首次渐入，之后保持在场）
        /// - 说话方全亮；另一在场角色半透明（DOTween 缓动）
        /// - 旁白（无说话方）→ 在场角色全部半透明
        /// - 两角色均未入场 → 两侧隐藏（黑屏阶段）
        /// 返回当前说话立绘（供抖动）。
        /// </summary>
        private void ApplyCharacter(StoryCue cue, out Image active)
        {
            active = null;
            bool justRevealedL = false;
            bool justRevealedR = false;

            // 黑影（揭示前暗场登场）：纯黑 + 缩小（瞬时，不带动画；亮相时 DOTween 还原）
            string sil = (cue.silhouette ?? string.Empty).ToLowerInvariant();
            if (sil.Contains("xeon") && _charLeft != null)
            {
                _xeonOnStage = true;
                _xeonSilhouette = true;
                _charLeft.gameObject.SetActive(true);
                ApplySilhouette(_charLeft);
            }
            if ((sil.Contains("tester") || sil.Contains("test")) && _charRight != null)
            {
                _testerOnStage = true;
                _testerSilhouette = true;
                _charRight.gameObject.SetActive(true);
                EnsureTesterSprite();
                ApplySilhouette(_charRight);
            }

            // 亮相（showXeon/showTester）：黑影 → DOTween 还原（缩放 1 + 纯白）；非黑影首登场 → 渐入
            if (cue.showXeon && _charLeft != null)
            {
                _xeonOnStage = true;
                _charLeft.gameObject.SetActive(true);
                if (_xeonSilhouette) { _xeonSilhouette = false; RevealChar(_charLeft, true); justRevealedL = true; }
                else FadeChar(_charLeft, 1f, _fadeDuration);
            }
            if (cue.showTester && _charRight != null)
            {
                _testerOnStage = true;
                _charRight.gameObject.SetActive(true);
                EnsureTesterSprite();
                if (_testerSilhouette) { _testerSilhouette = false; RevealChar(_charRight, false); justRevealedR = true; }
                else FadeChar(_charRight, 1f, _fadeDuration);
            }

            if (!_xeonOnStage && !_testerOnStage)
            {
                HidePortraits();
                return;
            }
            int speaking = ResolveSpeakingSide(cue);
            if (speaking == 0)
            {
                // 旁白：在场且已亮相的角色半透明（黑影保持黑影；刚亮相这句不立刻调暗）
                if (_xeonOnStage && _charLeft != null && !_xeonSilhouette && !justRevealedL) FadeChar(_charLeft, _dimAlpha, _fadeDuration);
                if (_testerOnStage && _charRight != null && !_testerSilhouette && !justRevealedR) FadeChar(_charRight, _dimAlpha, _fadeDuration);
                return;
            }
            var target = speaking == 2 ? _charRight : _charLeft;
            if (target == null)
            {
                if (speaking == 2 && !_missingRightLogged) { _missingRightLogged = true; Debug.LogWarning("[StoryPanel] Img_Character_R 缺失——测试员句跳过右立绘"); }
                if (speaking == 1 && !_missingLeftLogged) { _missingLeftLogged = true; Debug.LogWarning("[StoryPanel] Img_Character_L 缺失——Xeon 句跳过左立绘"); }
                return;
            }
            bool targetSilhouette = speaking == 2 ? _testerSilhouette : _xeonSilhouette;
            bool firstSpeak = (speaking == 1 && !_xeonOnStage) || (speaking == 2 && !_testerOnStage);
            if (speaking == 1) _xeonOnStage = true;
            else _testerOnStage = true;
            target.gameObject.SetActive(true);
            active = target;
            // 说话方亮起（带动画——从不说话→说话可见过渡）；黑影未亮相则保持黑影
            if (!targetSilhouette) FadeChar(target, 1f, _fadeDuration);
            var other = speaking == 2 ? _charLeft : _charRight;
            if (other != null)
            {
                bool otherOnStage = speaking == 2 ? _xeonOnStage : _testerOnStage;
                bool otherSilhouette = speaking == 2 ? _xeonSilhouette : _testerSilhouette;
                if (otherOnStage && !otherSilhouette) FadeChar(other, _dimAlpha, _fadeDuration);
                else if (!otherOnStage) other.gameObject.SetActive(false);
            }
        }

        /// <summary>说话侧解析：显式 character/showTester/showXeon > speaker 推断（？？？在测试员入场后归右位）> 在场单人 > 缺省 Xeon。</summary>
        private int ResolveSpeakingSide(StoryCue cue)
        {
            string type = (cue.type ?? string.Empty).ToLowerInvariant();
            if (type.Contains("narration") || type.Contains("narrator") || type.Contains("旁白")) return 0; // 旁白无说话方
            string sp = (cue.speaker ?? string.Empty).Trim();
            if (sp == "？？？")
            {
                // ？？？= 测试员：黑影期不点亮任何人（Xeon 调暗、黑影保持暗）；测试员亮相后归右位
                if (_testerOnStage) return _testerSilhouette ? 0 : 2;
                if (_xeonOnStage) return 1;
                return 0;
            }
            int side = ResolveSide(cue);
            if (side != 0) return side;
            if (_xeonOnStage && !_testerOnStage) return 1;
            if (_testerOnStage && !_xeonOnStage) return 2;
            return 1; // 双人在场且无信息——缺省 Xeon
        }

        private void HidePortraits()
        {
            if (_charLeft != null) _charLeft.gameObject.SetActive(false);
            if (_charRight != null) _charRight.gameObject.SetActive(false);
        }

        /// <summary>硬复位立绘（面板重开起点）：隐藏 + 缩放 1 + 纯白。</summary>
        private void ResetPortraits()
        {
            if (_charLeft != null)
            {
                KillSideTweens(_charLeft);
                _charLeft.rectTransform.localScale = Vector3.one;
                _charLeft.color = Color.white;
                _charLeft.gameObject.SetActive(false);
            }
            if (_charRight != null)
            {
                KillSideTweens(_charRight);
                _charRight.rectTransform.localScale = Vector3.one;
                _charRight.color = Color.white;
                _charRight.gameObject.SetActive(false);
            }
        }

        /// <summary>黑影：深黑 + 略缩小 + 渐显（alpha 0 → 0.9，协程确定性动画）。</summary>
        private void ApplySilhouette(Image img)
        {
            if (img == null) return;
            KillSideTweens(img);
            img.rectTransform.localScale = Vector3.one * _silhouetteScale;
            var c0 = img.color;
            c0.r = _silhouetteColor.r; c0.g = _silhouetteColor.g; c0.b = _silhouetteColor.b; c0.a = 0f;
            img.color = c0;
            Coroutine r = ReferenceEquals(img, _charLeft) ? _silhouetteRoutineL : _silhouetteRoutineR;
            if (r != null) StopCoroutine(r);
            r = StartCoroutine(SilhouetteFadeIn(img));
            if (ReferenceEquals(img, _charLeft)) _silhouetteRoutineL = r; else _silhouetteRoutineR = r;
        }

        /// <summary>黑影渐显：alpha 0 → 黑影目标透明度（unscaled + SmoothStep）。</summary>
        private System.Collections.IEnumerator SilhouetteFadeIn(Image img)
        {
            float targetA = _silhouetteColor.a;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.001f, _fadeDuration);
                var c = img.color;
                c.a = Mathf.SmoothStep(0f, targetA, Mathf.Clamp01(t));
                img.color = c;
                yield return null;
            }
            var cf = img.color; cf.a = targetA; img.color = cf;
            if (ReferenceEquals(img, _charLeft)) _silhouetteRoutineL = null; else _silhouetteRoutineR = null;
        }

        /// <summary>
        /// 亮相还原：缩放 → 1 + 颜色 → 纯白。
        /// DOTween 缓动参与（用户要求）；并行协程兜底（DOTween 未初始化/不推进时保证还原——确定性）。
        /// </summary>
        private void RevealChar(Image img, bool left)
        {
            if (img == null) return;
            KillSideTweens(img);
            // 兜底协程（确定性动画，DOTween 死掉也能还原）
            Coroutine c = left ? _revealRoutineL : _revealRoutineR;
            if (c != null) { StopCoroutine(c); }
            c = StartCoroutine(RevealRoutine(img, left));
            if (left) _revealRoutineL = c; else _revealRoutineR = c;
            // DOTween 缓动（与协程同向收敛；DOTween 可用时提供平滑）
            var rt = img.rectTransform;
            Vector3 fromS = rt.localScale;
            Color fromC = img.color;
            var ts = DOTween.To(() => fromS, v => rt.localScale = v, Vector3.one, _fadeDuration).SetEase(Ease.OutQuad).SetUpdate(true);
            var tc = DOTween.To(() => fromC, v => img.color = v, Color.white, _fadeDuration).SetEase(Ease.OutQuad).SetUpdate(true);
            var seq = DOTween.Sequence().Join(ts).Join(tc);
            seq.OnComplete(() => { if (left) _leftRevealTween = null; else _rightRevealTween = null; });
            if (left) _leftRevealTween = seq; else _rightRevealTween = seq;
        }

        /// <summary>亮相兜底：unscaled 时间 + SmoothStep 手动插值缩放/颜色到纯白+1（不依赖 DOTween 更新机制）。</summary>
        private System.Collections.IEnumerator RevealRoutine(Image img, bool left)
        {
            var rt = img.rectTransform;
            Vector3 fromS = rt.localScale;
            Color fromC = img.color;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.001f, _fadeDuration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                rt.localScale = Vector3.Lerp(fromS, Vector3.one, k);
                img.color = Color.Lerp(fromC, Color.white, k);
                yield return null;
            }
            rt.localScale = Vector3.one;
            img.color = Color.white;
            if (left) _revealRoutineL = null; else _revealRoutineR = null;
        }

        /// <summary>杀某侧立绘的透明度/亮相 tween（黑影/重置前调用）。</summary>
        private void KillSideTweens(Image img)
        {
            if (img == null) return;
            if (ReferenceEquals(img, _charLeft))
            {
                if (_leftAlphaTween != null && _leftAlphaTween.IsActive()) { _leftAlphaTween.Kill(); _leftAlphaTween = null; }
                if (_leftRevealTween != null && _leftRevealTween.IsActive()) { _leftRevealTween.Kill(); _leftRevealTween = null; }
                if (_revealRoutineL != null) { StopCoroutine(_revealRoutineL); _revealRoutineL = null; }
                if (_silhouetteRoutineL != null) { StopCoroutine(_silhouetteRoutineL); _silhouetteRoutineL = null; }
            }
            else if (ReferenceEquals(img, _charRight))
            {
                if (_rightAlphaTween != null && _rightAlphaTween.IsActive()) { _rightAlphaTween.Kill(); _rightAlphaTween = null; }
                if (_rightRevealTween != null && _rightRevealTween.IsActive()) { _rightRevealTween.Kill(); _rightRevealTween = null; }
                if (_revealRoutineR != null) { StopCoroutine(_revealRoutineR); _revealRoutineR = null; }
                if (_silhouetteRoutineR != null) { StopCoroutine(_silhouetteRoutineR); _silhouetteRoutineR = null; }
            }
        }

        /// <summary>立绘状态缓动：说话=纯白全亮；未说话=浅灰+_dimAlpha。颜色+alpha 一起动（core DOTween）。</summary>
        private void FadeChar(Image img, float to, float duration)
        {
            if (img == null) return;
            KillSideTweens(img); // 先停该侧全部状态动画（含亮相 tween/协程）——防亮相收尾写白覆盖调暗
            Color target = to >= 0.999f
                ? Color.white
                : new Color(_dimColor.r, _dimColor.g, _dimColor.b, _dimAlpha);
            Color from = img.color;
            if (duration <= 0f || from == target)
            {
                img.color = target;
                return;
            }
            Tween tw = DOTween.To(() => from, v => img.color = v, target, duration)
                .SetEase(Ease.InOutQuad).SetUpdate(true).OnComplete(() =>
                {
                    if (ReferenceEquals(img, _charLeft)) _leftAlphaTween = null;
                    else _rightAlphaTween = null;
                });
            if (ReferenceEquals(img, _charLeft)) _leftAlphaTween = tw; else _rightAlphaTween = tw;
        }

        /// <summary>测试员立绘兜底：prefab 未挂 sprite 时按 Addressables 地址加载（Tester_Default）。</summary>
        private void EnsureTesterSprite()
        {
            if (_charRight == null || _charRight.sprite != null) return;
            var s = LoadSpriteOrNull("Tester_Default");
            if (s == null) s = LoadSpriteOrNull("Tester");
            if (s != null) _charRight.sprite = s;
            else Debug.LogWarning("[StoryPanel] Tester 立绘缺失（Addressables 无 Tester_Default）——右位保留占位");
        }

        /// <summary>角色侧位解析：显式 character/char/side > showTester/showXeon > speaker 推断 > type（旁白=0）> 缺省左主位。</summary>
        private static int ResolveSide(StoryCue cue)
        {
            string c = (cue.character ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(c))
            {
                if (c == "r" || c == "right" || c.Contains("测试") || c.Contains("tester") || c.Contains("test")) return 2;
                if (c == "l" || c == "left" || c.Contains("xeon")) return 1;
            }
            // ⚠️ showTester/showXeon 是入场/亮相标记，不参与说话侧判定（曾导致 #8 Xeon 句被判成测试员说话）
            string sp = (cue.speaker ?? string.Empty).ToLowerInvariant();
            if (sp.Contains("测试") || sp.Contains("tester") || sp == "r") return 2;
            if (sp.Contains("xeon") || sp == "l") return 1;
            string type = (cue.type ?? string.Empty).ToLowerInvariant();
            if (type.Contains("narration") || type.Contains("narrator") || type.Contains("旁白")) return 0; // 旁白无立绘
            return 1; // 缺省 = Xeon 主位
        }

        private void ApplyXeonDiff(string diffKey)
        {
            var sprite = LoadSpriteOrNull(diffKey);
            if (sprite == null)
            {
                if (_missingDiffLogged.Add(diffKey))
                {
                    Debug.LogWarning($"[StoryPanel] Xeon 差分立绘缺失（Addressables 无 {diffKey}）——保留当前立绘");
                }
                return;
            }
            _charLeft.sprite = sprite;
        }

        /// <summary>Addressables 按地址加载差分立绘；候选 = 原 key + "Xeon_" + key；缺失返回 null（不抛断流程）。</summary>
        private static Sprite LoadSpriteOrNull(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var candidates = new List<string> { key };
            if (!key.StartsWith("Xeon", System.StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("Xeon_" + key);
            }
            foreach (var addr in candidates)
            {
                try
                {
                    // 先查地址存在（LoadResourceLocations 无效 key 返回空且不刷 InvalidKeyException）
                    var locHandle = Addressables.LoadResourceLocationsAsync(addr, typeof(Sprite));
                    locHandle.WaitForCompletion();
                    int count = locHandle.Result == null ? 0 : locHandle.Result.Count;
                    Addressables.Release(locHandle);
                    if (count == 0) continue; // 地址不存在——试下一候选
                    var handle = Addressables.LoadAssetAsync<Sprite>(addr);
                    var sprite = handle.WaitForCompletion();
                    Addressables.Release(handle);
                    if (sprite != null) return sprite;
                }
                catch
                {
                    // 异常——试下一候选
                }
            }
            return null;
        }

        /// <summary>立绘上下抖动（DOTween core 实现 punch 语义——anchoredPosition 保持布局；SetUpdate 时间静止也不冻结）。
        /// ⚠️ 不用 DOTweenModuleUI.DOPunchAnchorPos：该模块编译进 Assembly-CSharp-firstpass，asmdef 无法引用（CS1061）——
        /// 用核心 API DOTween.To + Yoyo 双循环模拟上→回→上→回。</summary>
        private void Shake(Image img)
        {
            if (img == null || img.rectTransform == null) return;
            if (_shakeTween != null && _shakeTween.IsActive()) _shakeTween.Kill();
            var rt = img.rectTransform;
            Vector2 basePos = rt.anchoredPosition;
            _shakeTween = DOTween.To(
                    () => rt.anchoredPosition,
                    v => rt.anchoredPosition = v,
                    basePos + new Vector2(0f, _shakeStrength),
                    0.18f)
                .SetEase(Ease.OutQuad)
                .SetLoops(2, LoopType.Yoyo) // 上→回（一次上下抖动）
                .SetUpdate(true)
                .OnComplete(() =>
                {
                    rt.anchoredPosition = basePos; // 兜底归位（防 tween 被打断残留）
                    _shakeTween = null;
                });
        }

        // ====== 音效 ======

        /// <summary>进句播 cue.sfx（对应 AudioRefs.SfxStory*）。AudioManager 无 StopSFX（PlayOneShot 短音）——不显式停上一句。</summary>
        private void PlaySfx(string raw)
        {
            string addr = MapSfxKey(raw);
            if (string.IsNullOrEmpty(addr)) return;
            AudioManager.Instance.PlaySFX(addr);
        }

        /// <summary>cue.sfx → AudioRefs.SfxStory* 常量（兼容多种写法；未知值原样当 Addressables 地址——缺失仅 LogWarning 不中断）。</summary>
        private static string MapSfxKey(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim();
            if (s.StartsWith("SFX/", System.StringComparison.OrdinalIgnoreCase)) return s;
            string low = s.ToLowerInvariant();
            if (low.Contains("wall")) return AudioRefs.SfxStoryWallBreak;   // 墙壁碎裂
            if (low.Contains("scrape") || low.Contains("friction")) return AudioRefs.SfxStoryScrape; // 摩擦沙沙
            if (low.Contains("static") || low.Contains("snow") || low.Contains("noise")) return AudioRefs.SfxStoryStatic; // 雪花屏
            string compact = low.Replace("_", string.Empty).Replace("-", string.Empty);
            if (compact == "sfxstorywallbreak") return AudioRefs.SfxStoryWallBreak;
            if (compact == "sfxstoryscrape") return AudioRefs.SfxStoryScrape;
            if (compact == "sfxstorystatic") return AudioRefs.SfxStoryStatic;
            return s;
        }

        // ====== 输入（纯 Input System：activeInputHandler=2）======

        private void Update()
        {
            if (!_playing || _skipAll || _exiting) return;
            bool down = AnyPressDown();
            bool held = AnyPressHeld();

            if (down && !_pressing)
            {
                _pressing = true;
                _pressStartTime = Time.unscaledTime;
                _pressCompletedTyping = false;
                if (_typing)
                {
                    CompleteTyping(); // 打字中按下 = 立即显示整句
                    _pressCompletedTyping = true;
                }
            }
            if (!_pressing) return;

            if (!held)
            {
                // 释放：按住 ≥ 阈值 = 跳过整段；短按 = 下一句（打字中按下已消费——不推进）
                float duration = Time.unscaledTime - _pressStartTime;
                _pressing = false;
                if (duration >= _skipHoldSeconds)
                {
                    _skipAll = true;
                }
                else if (!_pressCompletedTyping && !_typing)
                {
                    _awaitingNext = false;
                }
                _pressCompletedTyping = false;
            }
            else if (Time.unscaledTime - _pressStartTime >= _skipHoldSeconds)
            {
                _skipAll = true; // 长按达成——跳过整段剧情
                _pressing = false;
                _pressCompletedTyping = false;
            }
        }

        private static bool AnyPressDown()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame) return true;
            var mouse = Mouse.current;
            if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame)) return true;
            var touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.wasPressedThisFrame) return true;
            return false;
        }

        private static bool AnyPressHeld()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.isPressed) return true;
            var mouse = Mouse.current;
            if (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed)) return true;
            var touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.isPressed) return true;
            return false;
        }

        // ====== 数据（读 Assets/Data/Configs/story_opening.json——另一个 agent 解析工具产物；容错字段名）======

        /// <summary>尝试加载开场剧情；失败（文件缺失/为空/解析异常）返回 false——调用方跳过剧情直接进流程。</summary>
        public static bool TryLoadCues(out List<StoryCue> cues)
        {
            cues = null;
            string json = ReadJson();
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning("[StoryPanel] 未找到 Assets/Data/Configs/story_opening.json（Resources/Configs/story_opening 亦无）——跳过开场剧情直接进流程");
                return false;
            }
            try
            {
                var root = JToken.Parse(json);
                JArray array = null;
                if (root is JArray arr)
                {
                    array = arr;
                }
                else if (root is JObject obj)
                {
                    // 容错：根对象可能包一层 cues/lines/story/opening/clips/scenes
                    foreach (var key in new[] { "cues", "lines", "story", "opening", "clips", "scenes", "entries" })
                    {
                        var t = obj[key];
                        if (t is JArray ja) { array = ja; break; }
                        if (t is JObject jo && jo["cues"] is JArray inner) { array = inner; break; }
                    }
                }
                if (array == null)
                {
                    Debug.LogWarning("[StoryPanel] story_opening.json 缺少 cues 数组——跳过剧情直接进流程");
                    return false;
                }
                var list = new List<StoryCue>();
                foreach (var token in array)
                {
                    if (!(token is JObject o)) continue;
                    var cue = new StoryCue();
                    cue.type = GetString(o, "type", "kind") ?? string.Empty;
                    cue.text = GetString(o, "text", "dialogue", "content", "line", "txt") ?? string.Empty;
                    cue.speaker = GetString(o, "speaker", "name", "who") ?? string.Empty;
                    cue.character = GetString(o, "character", "char", "side") ?? string.Empty;
                    // ⚠️ cue 字段在子对象 "cue" 里（entry 根只有 type/text/speaker）——读错层级 = 全部失效（历史根因：立绘/bg/音效/差分全不生效）
                    var cueObj = o["cue"] as JObject ?? o;
                    cue.silhouette = GetString(cueObj, "silhouette", "shadow") ?? string.Empty;
                    cue.showXeon = GetBool(cueObj, "showXeon", "xeon");
                    cue.showTester = GetBool(cueObj, "showTester", "tester");
                    cue.xeonDiff = GetString(cueObj, "xeonDiff", "diff", "expression", "pose", "portrait") ?? string.Empty;
                    cue.shake = GetBool(cueObj, "shake", "shaking");
                    cue.sfx = GetString(cueObj, "sfx", "sound", "audio") ?? string.Empty;
                    cue.bg = GetBool(cueObj, "bg", "showBg", "background");
                    list.Add(cue);
                }
                if (list.Count == 0)
                {
                    Debug.LogWarning("[StoryPanel] story_opening.json cues 为空——跳过剧情直接进流程");
                    return false;
                }
                cues = list;
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[StoryPanel] story_opening.json 解析失败：{e.Message}——跳过剧情直接进流程");
                return false;
            }
        }

        private static string ReadJson()
        {
            // 优先 Resources（另一个 agent 的解析工具可能把产物拷进 Resources）；其次 Assets/Data/Configs 原文件
            var ta = Resources.Load<TextAsset>("Configs/story_opening");
            if (ta != null) return ta.text;
            ta = Resources.Load<TextAsset>("story_opening");
            if (ta != null) return ta.text;
            try
            {
                var path = Path.Combine(Application.dataPath, "Data/Configs/story_opening.json");
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[StoryPanel] 读取 story_opening.json 失败：{e.Message}");
            }
            return null;
        }

        private static string GetString(JObject o, params string[] keys)
        {
            foreach (var key in keys)
            {
                var t = o[key];
                if (t != null && t.Type != JTokenType.Null) return t.ToString();
            }
            return null;
        }

        private static bool GetBool(JObject o, params string[] keys)
        {
            foreach (var key in keys)
            {
                var t = o[key];
                if (t != null && t.Type == JTokenType.Boolean) return (bool)t;
            }
            return false;
        }

        // ====== 代码兜底 UI（prefab 缺失/未入 Addressables 时 Build——正式路径不会走到）======

        private void Build()
        {
            var bg = new GameObject("Img_Bg", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            bg.transform.SetParent(transform, false);
            var bgImage = bg.GetComponent<Image>();
            bgImage.color = Color.black;
            Stretch(bg.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            var txtBg = new GameObject("Img_TxtBg", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            txtBg.transform.SetParent(transform, false);
            var txtBgImage = txtBg.GetComponent<Image>();
            txtBgImage.color = new Color(0f, 0f, 0f, 0.8f);
            Stretch(txtBg.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 40f), new Vector2(-600f, 220f));

            var nameGo = new GameObject("Txt_Name", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            nameGo.transform.SetParent(txtBg.transform, false);
            var nameText = nameGo.GetComponent<TextMeshProUGUI>();
            nameText.fontSize = 48;
            nameText.alignment = TextAlignmentOptions.Left;
            Stretch(nameGo.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -10f), new Vector2(500f, 60f));

            var contentGo = new GameObject("Txt_Content", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            contentGo.transform.SetParent(txtBg.transform, false);
            var contentText = contentGo.GetComponent<TextMeshProUGUI>();
            contentText.fontSize = 36;
            contentText.alignment = TextAlignmentOptions.TopLeft;
            contentText.enableWordWrapping = true;
            Stretch(contentGo.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(-60f, -90f));
        }

        private static void Stretch(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPos, Vector2 sizeDelta)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;
        }

        /// <summary>递归按名查找（容错 prefab 层级嵌套，同 BattleResultPanel 模式）。</summary>
        private static T FindDeep<T>(Transform root, string name) where T : Component
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name)
                {
                    var c = t.GetComponent<T>();
                    if (c != null) return c;
                }
            }
            return null;
        }
    }

    /// <summary>开场剧情单句（story_opening.json cue 结构；解析容错——字段缺失走默认）。</summary>
    [System.Serializable]
    public class StoryCue
    {
        /// <summary>类型（dialogue 对白 / narration 旁白——旁白隐藏立绘）。</summary>
        public string type;
        /// <summary>对白文本（打字机逐字上屏）。</summary>
        public string text;
        /// <summary>说话人（显示在 Txt_Name；兼作左右立绘推断）。</summary>
        public string speaker;
        /// <summary>显式立绘位置/角色（可选："L"/"R"/"Xeon"/"测试员"——优先于 speaker 推断）。</summary>
        public string character;
        /// <summary>黑影登场标记（"xeon"/"tester"——揭示前纯黑+缩小，亮相时 DOTween 还原）。</summary>
        public string silhouette;
        /// <summary>显式 Xeon（左主位）标记（另一个 agent 的解析工具字段）。</summary>
        public bool showXeon;
        /// <summary>显式测试员（右位）标记（另一个 agent 的解析工具字段）。</summary>
        public bool showTester;
        /// <summary>Xeon 差分立绘 key（Addressables 地址；加载失败保留当前立绘）。</summary>
        public string xeonDiff;
        /// <summary>立绘上下抖动（DOTween punch）。</summary>
        public bool shake;
        /// <summary>音效 key（对应 AudioRefs.SfxStory*：wall_break / scrape / static…）。</summary>
        public string sfx;
        /// <summary>true=显示背景；false/缺省=黑屏（Img_Bg 纯黑兜底）。</summary>
        public bool bg;
    }
}
