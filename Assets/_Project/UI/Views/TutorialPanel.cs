using System;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 教程角色说话面板（Key="Tutorial"，李毕拼 prefab 后自动复用节点）：
    /// - 头像框 Grp_Portrait / 文本框 Grp_Text（两个独立对象，布局预设保证间距与对齐）
    /// - 任意键/点击 = 下一步（打字中先补全整句），长按 1.5s = 跳过整段，方向键上/左/PageUp = 上一步
    /// - 切换步骤 = 两框 DOTween 移动；位置始终 clamp 在屏幕内
    /// </summary>
    public class TutorialPanel : PanelBase
    {
        public override string Key => "Tutorial";
        public override bool IsPausing => false; // 非阻断：教学期间玩家仍可交互（遮罩只压暗不挡点击）

        public event Action OnAdvanceRequested;
        public event Action OnBackRequested;
        public event Action OnSkipRequested;

        const float SkipHoldSeconds = 1.5f;
        const float CharInterval = 0.03f;
        const float MoveDuration = 0.35f;
        const float ScreenMargin = 16f;

        Image _imgPortrait;
        TMP_Text _txtSpeaker;
        TMP_Text _txtText;
        RectTransform _grpPortrait;
        RectTransform _grpText;

        Coroutine _typeRoutine;
        bool _typing;
        bool _stopTyping;
        bool _built;

        bool _pressing;
        float _pressStartTime;
        bool _pressCompletedTyping;
        bool _skipBackConsumed;

        readonly HashSet<string> _loggedMissingPortrait = new HashSet<string>();

        /// <summary>立绘地址表（Addressables，待美术交付后填）：key → 地址。</summary>
        static readonly Dictionary<string, string> PortraitAddresses = new Dictionary<string, string>
        {
            // TODO: 美术交付后填写（如 "xeon_normal" → "portrait/xeon_normal"）；空表 = 立绘隐藏只显文字
        };

        public bool IsTyping => _typing;

        struct LayoutPreset
        {
            public Vector2 PortraitPos;
            public Vector2 TextPos;
            public LayoutPreset(Vector2 portrait, Vector2 text) { PortraitPos = portrait; TextPos = text; }
        }

        /// <summary>布局预设（中心锚点 + 参考分辨率 1920x1080 偏移；两框间距与对齐在预设中保证）。</summary>
        static readonly Dictionary<string, LayoutPreset> Presets = new Dictionary<string, LayoutPreset>
        {
            { "bottomCenter", new LayoutPreset(new Vector2(-420f, -330f), new Vector2(300f, -330f)) },
            { "bottomLeft",   new LayoutPreset(new Vector2(-760f, -330f), new Vector2(-300f, -330f)) },
            { "bottomRight",  new LayoutPreset(new Vector2(760f, -330f),  new Vector2(300f, -330f)) },
            { "topCenter",    new LayoutPreset(new Vector2(-420f, 330f),  new Vector2(300f, 330f)) },
            { "topLeft",      new LayoutPreset(new Vector2(-760f, 330f),  new Vector2(-300f, 330f)) },
            { "topRight",     new LayoutPreset(new Vector2(760f, 330f),   new Vector2(300f, 330f)) },
            { "leftMid",      new LayoutPreset(new Vector2(-780f, 0f),    new Vector2(-300f, 0f)) },
            { "rightMid",     new LayoutPreset(new Vector2(780f, 0f),     new Vector2(300f, 0f)) },
        };

        protected override void OnShow()
        {
            base.OnShow();
            BuildFallbackIfNeeded();
        }

        // ====== 对外 ======

        /// <summary>展示一步：说话人/立绘/台词（打字机）+ 布局移动（首步无动画）。</summary>
        public void ShowStep(TutorialStep step, bool animate)
        {
            BuildFallbackIfNeeded();
            if (_txtSpeaker != null) _txtSpeaker.text = step.speaker ?? string.Empty;
            if (_txtText != null) _txtText.text = string.Empty;
            SetPortrait(step.portraitKey);
            ApplyLayout(step.layout, animate);
            if (_typeRoutine != null) { StopCoroutine(_typeRoutine); _typeRoutine = null; }
            _typing = false;
            _stopTyping = false;
            if (_txtText != null && !string.IsNullOrEmpty(step.text))
            {
                _typeRoutine = StartCoroutine(TypeLine(step.text));
            }
        }

        public void HideAll()
        {
            if (_typeRoutine != null) { StopCoroutine(_typeRoutine); _typeRoutine = null; }
            _typing = false;
        }

        /// <summary>打字中按任意键 → 立即补全整句（不推进下一步）。</summary>
        public void CompleteTypingImmediate()
        {
            _stopTyping = true;
        }

        // ====== 打字机（Realtime：即使 timeScale=0 也逐字） ======

        IEnumerator TypeLine(string full)
        {
            _typing = true;
            _stopTyping = false;
            _txtText.text = string.Empty;
            for (int i = 1; i <= full.Length; i++)
            {
                if (_stopTyping) break;
                _txtText.text = full.Substring(0, i);
                yield return new WaitForSecondsRealtime(Mathf.Max(0.001f, CharInterval));
            }
            if (_txtText != null) _txtText.text = full;
            _typing = false;
        }

        // ====== 立绘 ======

        void SetPortrait(string portraitKey)
        {
            if (_imgPortrait == null) return;
            if (string.IsNullOrEmpty(portraitKey) || !PortraitAddresses.TryGetValue(portraitKey, out var address))
            {
                _imgPortrait.gameObject.SetActive(false);
                if (!string.IsNullOrEmpty(portraitKey) && _loggedMissingPortrait.Add(portraitKey))
                {
                    Debug.Log("[TutorialPanel] 立绘未配置或地址缺失：" + portraitKey + "（美术交付后填入 PortraitAddresses）——本次隐藏立绘");
                }
                return;
            }
            _imgPortrait.gameObject.SetActive(true);
            var handle = Addressables.LoadAssetAsync<Sprite>(address);
            handle.Completed += op =>
            {
                if (op.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && op.Result != null)
                {
                    if (_imgPortrait != null) _imgPortrait.sprite = op.Result;
                }
                Addressables.Release(op);
            };
        }

        // ====== 布局（DOTween 移动两框 + 屏幕内 clamp） ======

        void ApplyLayout(string presetName, bool animate)
        {
            if (!Presets.TryGetValue(presetName ?? string.Empty, out var p))
            {
                p = Presets["bottomCenter"];
            }
            MoveBox(_grpPortrait, p.PortraitPos, animate);
            MoveBox(_grpText, p.TextPos, animate);
        }

        void MoveBox(RectTransform box, Vector2 targetPos, bool animate)
        {
            if (box == null) return;
            Vector2 clamped = ClampToScreen(box, targetPos);
            if (animate)
            {
                DOTween.To(() => box.anchoredPosition, v => box.anchoredPosition = v, clamped, MoveDuration)
                    .SetEase(Ease.OutCubic)
                    .SetUpdate(true); // 暂停型（timeScale=0）也不冻结移动
            }
            else
            {
                box.anchoredPosition = clamped;
            }
        }

        /// <summary>把目标位置 clamp 进屏幕（参考系 = Canvas 1920x1080 本地坐标；按框实际尺寸留边）。</summary>
        Vector2 ClampToScreen(RectTransform box, Vector2 pos)
        {
            Rect canvasRect = GetCanvasRect();
            Vector2 half = box != null ? box.rect.size * 0.5f : new Vector2(160f, 120f);
            float minX = canvasRect.xMin + half.x + ScreenMargin;
            float maxX = canvasRect.xMax - half.x - ScreenMargin;
            float minY = canvasRect.yMin + half.y + ScreenMargin;
            float maxY = canvasRect.yMax - half.y - ScreenMargin;
            return new Vector2(
                Mathf.Clamp(pos.x, minX, Mathf.Max(minX, maxX)),
                Mathf.Clamp(pos.y, minY, Mathf.Max(minY, maxY)));
        }

        Rect GetCanvasRect()
        {
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null && canvas.transform is RectTransform rt) return rt.rect;
            return new Rect(-960f, -540f, 1920f, 1080f);
        }

        // ====== 输入（纯 Input System 主动查询，同 StoryPanel；方向键上/左/PageUp = 上一步） ======

        void Update()
        {
            if (!gameObject.activeSelf) return;

            bool back = TryBackKeyDown();
            bool down = AnyPressDown();
            bool held = AnyPressHeld();

            if (back)
            {
                _pressing = true;
                _pressStartTime = Time.unscaledTime;
                _pressCompletedTyping = true;
                _skipBackConsumed = true;
                OnBackRequested?.Invoke();
                return;
            }

            if (down && !_pressing)
            {
                _pressing = true;
                _pressStartTime = Time.unscaledTime;
                _pressCompletedTyping = false;
                if (_typing)
                {
                    CompleteTypingImmediate();
                    _pressCompletedTyping = true;
                }
            }
            if (!_pressing) return;

            if (!held)
            {
                float duration = Time.unscaledTime - _pressStartTime;
                _pressing = false;
                if (_skipBackConsumed)
                {
                    _skipBackConsumed = false;
                }
                else if (duration >= SkipHoldSeconds)
                {
                    OnSkipRequested?.Invoke();
                }
                else if (!_pressCompletedTyping && !_typing)
                {
                    OnAdvanceRequested?.Invoke();
                }
                _pressCompletedTyping = false;
            }
            else if (Time.unscaledTime - _pressStartTime >= SkipHoldSeconds)
            {
                _pressing = false;
                _pressCompletedTyping = false;
                OnSkipRequested?.Invoke();
            }
        }

        static bool TryBackKeyDown()
        {
            var kb = Keyboard.current;
            if (kb == null) return false;
            return kb[UnityEngine.InputSystem.Key.UpArrow].wasPressedThisFrame
                || kb[UnityEngine.InputSystem.Key.LeftArrow].wasPressedThisFrame
                || kb[UnityEngine.InputSystem.Key.PageUp].wasPressedThisFrame;
        }

        static bool AnyPressDown()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame) return true;
            var mouse = Mouse.current;
            if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame)) return true;
            var touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.wasPressedThisFrame) return true;
            return false;
        }

        static bool AnyPressHeld()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.isPressed) return true;
            var mouse = Mouse.current;
            if (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed)) return true;
            var touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.isPressed) return true;
            return false;
        }

        // ====== 节点解析 / 代码兜底 ======

        void BuildFallbackIfNeeded()
        {
            if (_built) return;
            _built = true;

            _grpPortrait = FindDeep<RectTransform>(transform, "Grp_Portrait");
            _grpText = FindDeep<RectTransform>(transform, "Grp_Text");
            _imgPortrait = FindDeep<Image>(transform, "Img_Portrait");
            _txtSpeaker = FindDeep<TMP_Text>(transform, "Txt_Speaker");
            _txtText = FindDeep<TMP_Text>(transform, "Txt_Text");

            if (_grpPortrait != null || _grpText != null || _imgPortrait != null || _txtSpeaker != null || _txtText != null)
            {
                // 部分节点缺失告警（李毕拼完即消）
                if (_txtText == null) Debug.LogWarning("[TutorialPanel] 未找到 Txt_Text——台词无法显示");
                return; // 存在 prefab 节点：缺失项按现有结构兜底
            }

            // 纯代码兜底（无 prefab 时也能跑）
            _grpPortrait = CreateBox("Grp_Portrait", new Vector2(320f, 400f), new Color(0.08f, 0.08f, 0.1f, 0.9f), out _);
            _grpText = CreateBox("Grp_Text", new Vector2(800f, 300f), new Color(0.08f, 0.08f, 0.1f, 0.92f), out _);
            var portraitGo = new GameObject("Img_Portrait", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            portraitGo.transform.SetParent(_grpPortrait, false);
            Stretch(portraitGo.GetComponent<RectTransform>(), new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f));
            _imgPortrait = portraitGo.GetComponent<Image>();
            _imgPortrait.raycastTarget = false;

            var speakerGo = new GameObject("Txt_Speaker", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            speakerGo.transform.SetParent(_grpText, false);
            var speakerRt = speakerGo.GetComponent<RectTransform>();
            Stretch(speakerRt, new Vector2(0f, 0.72f), new Vector2(1f, 1f));
            _txtSpeaker = speakerGo.GetComponent<TextMeshProUGUI>();
            _txtSpeaker.fontSize = 30f;
            _txtSpeaker.color = Color.white;
            _txtSpeaker.alignment = TextAlignmentOptions.Left;

            var textGo = new GameObject("Txt_Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(_grpText, false);
            var textRt = textGo.GetComponent<RectTransform>();
            Stretch(textRt, new Vector2(0.04f, 0.04f), new Vector2(0.96f, 0.68f));
            _txtText = textGo.GetComponent<TextMeshProUGUI>();
            _txtText.fontSize = 34f;
            _txtText.color = Color.white;
            _txtText.alignment = TextAlignmentOptions.TopLeft;
            _txtText.enableWordWrapping = true;
        }

        RectTransform CreateBox(string name, Vector2 size, Color color, out Image img)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = size;
            img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false; // 不挡下层交互
            return rt;
        }

        static void Stretch(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>深度优先按名查找组件（兼容嵌套结构；无则 null）。</summary>
        public static T FindDeep<T>(Transform root, string name) where T : Component
        {
            if (root == null) return null;
            if (root.name == name)
            {
                var c = root.GetComponent<T>();
                if (c != null) return c;
            }
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep<T>(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
