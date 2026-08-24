using System;
using System.Collections;
using System.Collections.Generic;
using TheLaw.Core;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>主菜单：标题 + 开始/继续/设置/退出。（prefab 布局优先，代码构建兜底——按钮点击一律转发事件）</summary>
    public class MainMenuPanel : PanelBase
    {
        public override string Key => "MainMenu";

        // 按钮事件（Bootstrap 订阅响应——面板只转发输入，不持有规则层引用）
        public event Action OnNewGameClicked;
        public event Action OnContinueClicked;
        public event Action OnSettingsClicked;
        public event Action OnQuitClicked;

        // ====== 冷启动开场动画（2026-08-26 用户定案 v2）======
        // 时序（总 ~7.5s，相邻阶段 0.5s 重叠）：主标题渐入 → 副标题渐入 → 背景缓亮 + 按钮组渐入。
        // 交互：动画期间全屏透明阻射线层（点击/任意键 = 跳过直落终态）；按钮始终可用态（透明度走 CanvasGroup）。
        // 范围：仅冷启动首次进主菜单播放；返回主菜单（战斗/事件退出）不播。
        private bool _introDone;       // 已播标记（冷启动一次）
        private bool _introRunning;    // 动画进行中（输入跳过判定）
        private Coroutine _introRoutine;
        private Image _imgBg;          // 背景图（纯黑 → prefab 现值 ≈ #808080 缓亮）
        private Color _bgTargetColor = new Color(0.5f, 0.5f, 0.5f, 1f); // 终态（Awake 时取 prefab 现值）
        private CanvasGroup _titleGroup;    // 主标题（Txt_Title）
        private CanvasGroup _subtitleGroup; // 副标题（Txt_Subtitle）
        private CanvasGroup _menuGroup;     // 按钮组（Grp_MenuOptions——prefab 已挂 CanvasGroup）
        private Image _introBlocker;        // 全屏透明阻射线层（动画期防点击）

        private void Awake()
        {
            // prefab 路径：CreateAsync 加载含完整布局的 prefab（AddComponent 后 Awake 触发）——有子节点则跳过代码构建（防双份 UI）
            if (transform.childCount == 0)
            {
                Build();
            }
            BindButtons();
            CacheIntroRefs();
        }

        private void BindButtons()
        {
            // lambda 直接引用事件字段（运行时读最新值）——传参数会捕获订阅前的 null 快照，点击时永远不触发
            Bind("Btn_NewGame", () => OnNewGameClicked?.Invoke());
            Bind("Btn_ContinueGame", () => OnContinueClicked?.Invoke());
            Bind("Btn_Settings", () => OnSettingsClicked?.Invoke());
            Bind("Btn_QuitGame", () => OnQuitClicked?.Invoke());
        }

        private void Bind(string buttonName, Action handler)
        {
            Button btn = null;
            foreach (var b in GetComponentsInChildren<Button>(true))
            {
                if (b.name == buttonName) { btn = b; break; } // 按钮可能在分组子级下（如 Grp_MenuOptions/Btn_NewGame）
            }
            if (btn == null)
            {
                Debug.LogWarning($"[MainMenu] 未找到按钮 {buttonName}");
                return;
            }
            btn.onClick.RemoveAllListeners(); // 防重复绑定（面板重建）
            btn.onClick.AddListener(() => { UiSfx.Play(); Debug.Log($"[MainMenu] 点击 {buttonName}"); handler?.Invoke(); });
            Debug.Log($"[MainMenu] 绑定按钮 {buttonName}");
        }

        protected override void OnShow()
        {
            base.OnShow();
            if (_introDone) return; // 仅冷启动首次播放；返回主菜单不再播
            _introDone = true;
            _introRoutine = StartCoroutine(PlayIntro());
        }

        private void Update()
        {
            if (!_introRunning) return;
            if (AnyInputDown())
            {
                SkipIntro();
            }
        }

        private void OnDestroy()
        {
            if (_introRoutine != null)
            {
                StopCoroutine(_introRoutine);
                _introRoutine = null;
            }
            _introRunning = false;
        }

        // ====== 开场动画 ======

        private void CacheIntroRefs()
        {
            _imgBg = FindDeep<Image>(transform, "Img_Bg");
            if (_imgBg != null) _bgTargetColor = _imgBg.color; // 以 prefab 现值（≈#808080）为终态

            var title = FindDeep<TMP_Text>(transform, "Txt_Title") ?? FindDeep<TMP_Text>(transform, "Title");
            if (title != null)
            {
                _titleGroup = title.GetComponent<CanvasGroup>();
                if (_titleGroup == null) _titleGroup = title.gameObject.AddComponent<CanvasGroup>();
            }

            var subtitle = FindDeep<TMP_Text>(transform, "Txt_Subtitle");
            if (subtitle != null)
            {
                _subtitleGroup = subtitle.GetComponent<CanvasGroup>();
                if (_subtitleGroup == null) _subtitleGroup = subtitle.gameObject.AddComponent<CanvasGroup>();
            }

            var menuRoot = FindChild(transform, "Grp_MenuOptions") ?? transform; // 代码兜底路径无 Grp_——退化为整面板
            _menuGroup = menuRoot.GetComponent<CanvasGroup>();
            if (_menuGroup == null) _menuGroup = menuRoot.gameObject.AddComponent<CanvasGroup>();
        }

        private IEnumerator PlayIntro()
        {
            _introRunning = true;
            // 起始纯黑：背景黑 + 标题/副标题/按钮组透明
            if (_imgBg != null) _imgBg.color = Color.black;
            if (_titleGroup != null) _titleGroup.alpha = 0f;
            if (_subtitleGroup != null) _subtitleGroup.alpha = 0f;
            if (_menuGroup != null) _menuGroup.alpha = 0f;
            CreateBlocker(); // 动画期阻射线（按钮保持可用态——点击 = 跳过）

            yield return new WaitForSecondsRealtime(0.5f); // 黑屏静默（BGM 3s 渐入进行中）
            if (!_introRunning) yield break;

            // ① 主标题渐入（2s）
            if (_titleGroup != null) StartCoroutine(FadeGroup(_titleGroup, 0f, 1f, 2f));
            // ② 副标题：1.5s 后起（与标题重叠 0.5s），渐入 2s
            yield return new WaitForSecondsRealtime(1.5f);
            if (!_introRunning) yield break;
            if (_subtitleGroup != null) StartCoroutine(FadeGroup(_subtitleGroup, 0f, 1f, 2f));
            // ③ 背景缓亮（4.5s）：3.0s 后起（与副标题重叠 0.5s）
            yield return new WaitForSecondsRealtime(1.5f);
            if (!_introRunning) yield break;
            var bgRoutine = _imgBg != null ? StartCoroutine(LerpColor(_imgBg, Color.black, _bgTargetColor, 4.5f)) : null;
            // ④ 按钮组渐入（3.5s）：背景起 0.5s 后（与背景同步重叠）
            yield return new WaitForSecondsRealtime(0.5f);
            if (!_introRunning) yield break;
            if (_menuGroup != null) StartCoroutine(FadeGroup(_menuGroup, 0f, 1f, 3.5f));
            if (bgRoutine != null) yield return bgRoutine; // 等背景完成（t≈7.5s）

            FinishIntro();
        }

        /// <summary>跳过动画：直落终态（背景 808080、标题/副标题/按钮可见、BGM 满音量、可点）。</summary>
        private void SkipIntro()
        {
            if (!_introRunning) return;
            _introRunning = false;
            if (_introRoutine != null)
            {
                StopCoroutine(_introRoutine);
                _introRoutine = null;
            }
            if (_imgBg != null) _imgBg.color = _bgTargetColor;
            if (_titleGroup != null) _titleGroup.alpha = 1f;
            if (_subtitleGroup != null) _subtitleGroup.alpha = 1f;
            if (_menuGroup != null) _menuGroup.alpha = 1f;
            FinishIntro();
            AudioManager.Instance.CompleteBGMCrossfade(); // BGM 直落满音量（不等 3s 渐入）
        }

        private void FinishIntro()
        {
            _introRunning = false;
            RemoveBlocker();
        }

        private void CreateBlocker()
        {
            if (_introBlocker != null) return;
            var go = new GameObject("IntroBlocker", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(transform, false);
            go.transform.SetAsLastSibling(); // 最顶层（盖住标题/按钮——透明，黑屏由 Img_Bg 纯黑呈现）
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            _introBlocker = go.GetComponent<Image>();
            _introBlocker.color = new Color(0f, 0f, 0f, 0f);
            _introBlocker.raycastTarget = true; // 透明也拦截点击
        }

        private void RemoveBlocker()
        {
            if (_introBlocker != null)
            {
                Destroy(_introBlocker.gameObject);
                _introBlocker = null;
            }
        }

        private IEnumerator FadeGroup(CanvasGroup group, float from, float to, float duration)
        {
            if (group == null) yield break;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                group.alpha = Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration)));
                yield return null;
            }
            group.alpha = to;
        }

        private IEnumerator LerpColor(Image image, Color from, Color to, float duration)
        {
            if (image == null) yield break;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                image.color = Color.Lerp(from, to, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration)));
                yield return null;
            }
            image.color = to;
        }

        private static bool AnyInputDown()
        {
            if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame) return true;
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) return true;
            if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame) return true;
            return false;
        }

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

        private static Transform FindChild(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

        private void Build()
        {
            // 标题
            var titleGo = new GameObject("Title", typeof(RectTransform));
            titleGo.transform.SetParent(transform, false);
            var title = titleGo.AddComponent<TextMeshProUGUI>();
            title.text = "逆元渗透";
            title.fontSize = 96;
            title.alignment = TextAlignmentOptions.Center;
            title.color = Color.white;
            Stretch(titleGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0.65f), new Vector2(0.5f, 0.65f), new Vector2(0, 0), new Vector2(1200, 160));

            // 开始按钮（代码版兜底——prefab 路径不会走到这）
            var btnGo = new GameObject("StartButton", typeof(RectTransform));
            btnGo.transform.SetParent(transform, false);
            var img = btnGo.AddComponent<Image>();
            img.color = new Color(0.2f, 0.5f, 0.9f);
            var btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = img;
            Stretch(btnGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0.4f), new Vector2(0.5f, 0.4f), new Vector2(0, 0), new Vector2(320, 90));

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(btnGo.transform, false);
            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = "开始游戏";
            label.fontSize = 36;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            Stretch(labelGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            btn.onClick.AddListener(() => OnNewGameClicked?.Invoke());
        }

        private static void Stretch(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPos, Vector2 size)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
        }
    }
}
