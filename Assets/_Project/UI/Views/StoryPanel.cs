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
    /// 立绘：Img_Character_L（Xeon 主位）/ Img_Character_R（测试员右位——待李毕拼图，代码按名 Find，缺失判空跳过）；
    ///       cue.xeonDiff 切换 L 立绘 sprite（Addressables 按地址；美术未交付时缺失判空保留占位）。
    /// 音效：cue.sfx → AudioRefs.SfxStory*（进句播；AudioManager 无 StopSFX——短音不显式停上一句）。
    /// 背景：cue.bg=true 显示背景；黑屏阶段 Img_Bg 代码控 color 纯黑兜底。
    /// </summary>
    public class StoryPanel : PanelBase
    {
        public override string Key => "StoryPanel";

        /// <summary>剧情播放结束（全部播完或长按跳过）——Bootstrap 订阅后收尾并进入新局。</summary>
        public event System.Action Finished;

        // ====== 可调参数（Inspector 可见——打字机速度常量可调）======
        [Header("开场剧情")]
        [SerializeField, Tooltip("逐字间隔（秒）——打字机速度常量")] private float _charInterval = 0.03f;
        [SerializeField, Tooltip("长按跳过阈值（秒）")] private float _skipHoldSeconds = 0.8f;
        [SerializeField, Tooltip("立绘上下抖动幅度（DOTween punch）")] private float _shakeStrength = 10f;

        // ====== 节点引用（按名字 Find——prefab 层级变动容错；缺失判空跳过）======
        private Image _bgImage;       // Img_Bg（背景/黑屏——代码控 color 兜底）
        private TMP_Text _nameText;   // Txt_Name（说话人）
        private TMP_Text _contentText; // Txt_Content（对白——逐字打字机）
        private Image _charLeft;      // Img_Character_L（Xeon 主位）
        private Image _charRight;     // Img_Character_R（测试员右位——李毕拼图，缺失判空跳过）

        private List<StoryCue> _cues; // 当前局剧情
        private int _cueIndex;
        private bool _playing;
        private bool _typing;         // 打字中（按下 = 立即显示整句）
        private bool _stopTyping;     // 打断打字（整句立即显示）
        private Coroutine _playRoutine;
        private Coroutine _typeRoutine;
        private Tween _shakeTween;

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
            _bgImage = FindDeep<Image>(transform, "Img_Bg");
            _nameText = FindDeep<TMP_Text>(transform, "Txt_Name");
            _contentText = FindDeep<TMP_Text>(transform, "Txt_Content");
            _charLeft = FindDeep<Image>(transform, "Img_Character_L");
            _charRight = FindDeep<Image>(transform, "Img_Character_R");
            if (_charRight == null) Debug.Log("[StoryPanel] 未找到 Img_Character_R（李毕拼图后自动生效——缺失期间测试员句跳过右立绘）");
            if (_charLeft == null) Debug.LogWarning("[StoryPanel] 未找到 Img_Character_L——Xeon 立绘不可用");
            if (_contentText == null) Debug.LogWarning("[StoryPanel] 未找到 Txt_Content——对白无法显示");
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
            _playing = false;
            yield return null; // 等一帧：回调栈外销毁面板安全（Bootstrap 收尾）
            Finished?.Invoke();
        }

        /// <summary>进入一句：背景/说话人/立绘/差分/抖动/音效一次性应用（文本由打字机逐字上屏）。</summary>
        private void ApplyCue(StoryCue cue)
        {
            // 背景：bg=true 显示背景；黑屏阶段 Img_Bg 纯黑（代码控 color 兜底——不依赖美术给黑图）
            if (_bgImage != null)
            {
                _bgImage.gameObject.SetActive(true);
                _bgImage.color = cue.bg ? Color.white : Color.black;
            }
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

        /// <summary>按 cue 选左右立绘（Xeon=左主位；测试员=右位；旁白=隐藏两侧）；目标缺失判空跳过。返回当前激活立绘（供抖动）。</summary>
        private void ApplyCharacter(StoryCue cue, out Image active)
        {
            active = null;
            int side = ResolveSide(cue); // 0=旁白无立绘 / 1=左 Xeon / 2=右测试员
            if (side == 0)
            {
                if (_charLeft != null) _charLeft.gameObject.SetActive(false);
                if (_charRight != null) _charRight.gameObject.SetActive(false);
                return;
            }
            bool tester = side == 2;
            Image target = tester ? _charRight : _charLeft;
            if (target == null)
            {
                if (tester && !_missingRightLogged) { _missingRightLogged = true; Debug.LogWarning("[StoryPanel] Img_Character_R 缺失（李毕拼图未交付）——测试员句跳过右立绘"); }
                if (!tester && !_missingLeftLogged) { _missingLeftLogged = true; Debug.LogWarning("[StoryPanel] Img_Character_L 缺失——Xeon 句跳过左立绘"); }
                return; // 缺失判空跳过：保持现状，不隐藏另一侧
            }
            target.gameObject.SetActive(true);
            active = target;
            var other = tester ? _charLeft : _charRight;
            if (other != null) other.gameObject.SetActive(false);
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
            if (cue.showTester) return 2;
            if (cue.showXeon) return 1;
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
                    var handle = Addressables.LoadAssetAsync<Sprite>(addr);
                    var sprite = handle.WaitForCompletion();
                    Addressables.Release(handle);
                    if (sprite != null) return sprite;
                }
                catch
                {
                    // 地址不存在——试下一候选
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
            if (!_playing || _skipAll) return;
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
                    cue.showXeon = GetBool(o, "showXeon", "xeon");
                    cue.showTester = GetBool(o, "showTester", "tester");
                    cue.xeonDiff = GetString(o, "xeonDiff", "diff", "expression", "pose", "portrait") ?? string.Empty;
                    cue.shake = GetBool(o, "shake", "shaking");
                    cue.sfx = GetString(o, "sfx", "sound", "audio") ?? string.Empty;
                    cue.bg = GetBool(o, "bg", "showBg", "background");
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
