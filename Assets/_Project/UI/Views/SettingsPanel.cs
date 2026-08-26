using System.Collections;
using System.Collections.Generic;
using TheLaw.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 设置面板：音量（BGM/SFX Slider + 数值）、全屏（Toggle）、分辨率（TMP_Dropdown 常用项）。
    /// 改动实时生效并写入独立 settings.json；仅 Btn_Close 关闭。
    /// 模态：IsPausing=true；允许点背景关闭（CloseOnBgClick=true——PanleBase 自动加背景点击 + Grp_ 阻挡）。
    /// </summary>
    public class SettingsPanel : PanelBase
    {
        public override string Key => "Settings";
        public override bool IsPausing => true;
        protected override bool CloseOnBgClick => true;

        // ====== 常用分辨率（优先取 Screen 可用，缺失回退全部横向）======
        private static readonly Vector2Int[] CommonResolutions =
        {
            new Vector2Int(1920, 1080),
            new Vector2Int(2560, 1440),
            new Vector2Int(1600, 900),
            new Vector2Int(1366, 768),
            new Vector2Int(1280, 720),
            new Vector2Int(3840, 2160),
        };

        private Slider _sldBgm;
        private Slider _sldSfx;
        private Toggle _togFullscreen;
        private TMP_Dropdown _dpdResolution;
        private TMP_Text _txtBgmV;
        private TMP_Text _txtSfxV;
        private Button _btnReset;
        private Button _btnClose;
        private UIManager _uiManager; // overlay(设置盖在主菜单上) 关闭用；空 = 直接 Hide 兜底

        private readonly List<Vector2Int> _resolutions = new List<Vector2Int>(); // 下拉选项（索引映射）
        private Coroutine _layoutRefreshRoutine;
        private bool _wired;

        public void Init(UIManager uiManager)
        {
            _uiManager = uiManager;
        }

        private void Awake()
        {
            ResolveNodes();
            Wire();
        }

        void ResolveNodes()
        {
            // 音量（路径优先，找不到按名深搜——防 prefab 层级漂移）
            _sldBgm = Get<Slider>("Img_Bg/Grp_/Grp_Volume/Grp_BGM/Sld_BGM", "Sld_BGM");
            _sldSfx = Get<Slider>("Img_Bg/Grp_/Grp_Volume/Grp_SFX/Sld_SFX", "Sld_SFX");
            _txtBgmV = Get<TMP_Text>("Img_Bg/Grp_/Grp_Volume/Grp_BGM/Txt_BGM_V", "Txt_BGM_V");
            _txtSfxV = Get<TMP_Text>("Img_Bg/Grp_/Grp_Volume/Grp_SFX/Txt_SFX_V", "Txt_SFX_V");
            // 显示
            _togFullscreen = Get<Toggle>("Img_Bg/Grp_/Grp_Display/Grp_FullScreen/Tog_FullScreen", "Tog_FullScreen");
            _dpdResolution = Get<TMP_Dropdown>("Img_Bg/Grp_/Grp_Display/Grp_Resolution/Dpd_Resolution", "Dpd_Resolution");
            // 按钮
            _btnReset = Get<Button>("Img_Bg/Grp_/Grp_Btns/Btn_Reset", "Btn_Reset");
            _btnClose = Get<Button>("Img_Bg/Grp_/Grp_Btns/Btn_Close", "Btn_Close");
        }

        /// <summary>路径优先；路径缺失/组件不对时按名深搜（prefab 层级/命名漂移兜底）。</summary>
        T Get<T>(string path, string name) where T : Component
        {
            var t = transform.Find(path);
            if (t != null)
            {
                var c = t.GetComponent<T>();
                if (c != null) return c;
            }
            foreach (var c in GetComponentsInChildren<T>(true))
            {
                if (c.name == name) return c;
            }
            return null;
        }

        void Wire()
        {
            if (_wired) return;
            _wired = true;
            if (_sldBgm != null)
            {
                _sldBgm.minValue = 0f;
                _sldBgm.maxValue = 1f;
                _sldBgm.onValueChanged.AddListener(v =>
                {
                    SettingsSystem.Instance.SetBGMVolumePercent(Mathf.RoundToInt(v * 100f));
                    if (_txtBgmV != null) _txtBgmV.text = $"{Mathf.RoundToInt(v * 100f)}";
                });
            }
            if (_sldSfx != null)
            {
                _sldSfx.minValue = 0f;
                _sldSfx.maxValue = 1f;
                _sldSfx.onValueChanged.AddListener(v =>
                {
                    SettingsSystem.Instance.SetSFXVolumePercent(Mathf.RoundToInt(v * 100f));
                    if (_txtSfxV != null) _txtSfxV.text = $"{Mathf.RoundToInt(v * 100f)}";
                });
            }
            if (_togFullscreen != null)
            {
                _togFullscreen.onValueChanged.AddListener(v =>
                {
                    UiSfx.Play(); // 全屏开关碰撞音（2026-08-24 音频挂点方案）
                    SettingsSystem.Instance.SetFullscreen(v); // 应用由 ScreenSettingsApplier 监听 SettingsChanged 落地
                });
            }
            if (_dpdResolution != null)
            {
                _dpdResolution.onValueChanged.AddListener(idx =>
                {
                    if (idx < 0 || idx >= _resolutions.Count) return;
                    var r = _resolutions[idx];
                    SettingsSystem.Instance.SetResolution(r.x, r.y); // 应用由 ScreenSettingsApplier 监听落地
                });
            }
            if (_btnReset != null)
            {
                _btnReset.onClick.AddListener(OnReset);
            }
            if (_btnClose != null)
            {
                _btnClose.onClick.AddListener(Close);
            }
        }

        protected override void OnShow()
        {
            InitFromSettings();
            if (_layoutRefreshRoutine != null) StopCoroutine(_layoutRefreshRoutine);
            _layoutRefreshRoutine = StartCoroutine(RefreshLayoutNextFrame());
            // ⚠️ 不在打开时 ApplyScreen：打开就 SetResolution 会让窗口跳变（"刚显示 vs 操作后不一致"根因）。
            // 显示设置只在用户改动/恢复默认时落地。
        }

        /// <summary>等待本帧激活与选项更新完成后，只重建面板实际的布局根。</summary>
        IEnumerator RefreshLayoutNextFrame()
        {
            yield return null;

            var group = transform.Find("Img_Bg/Grp_") as RectTransform;
            if (group != null)
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(group);
            }

            _layoutRefreshRoutine = null;
        }

        /// <summary>用当前 SettingsSystem 值刷新 UI（SetValueWithoutNotify 防事件回环）。</summary>
        void InitFromSettings()
        {
            var s = SettingsSystem.Instance;
            if (_sldBgm != null) _sldBgm.SetValueWithoutNotify(s.BgmVolumePercent / 100f);
            if (_sldSfx != null) _sldSfx.SetValueWithoutNotify(s.SfxVolumePercent / 100f);
            if (_txtBgmV != null) _txtBgmV.text = $"{s.BgmVolumePercent}";
            if (_txtSfxV != null) _txtSfxV.text = $"{s.SfxVolumePercent}";
            if (_togFullscreen != null) _togFullscreen.SetIsOnWithoutNotify(s.Fullscreen);
            if (_dpdResolution != null)
            {
                BuildResolutionOptions();
                SelectCurrentResolution();
            }
        }

        void OnReset()
        {
            UiSfx.Play(); // 重置按钮碰撞音（2026-08-24 音频挂点方案）
            var s = SettingsSystem.Instance;
            s.SetBGMVolumePercent(80);
            s.SetSFXVolumePercent(100);
            s.SetFullscreen(true);
            s.SetResolution(1920, 1080); // 音量/显示落地由监听者（AudioManager/ScreenSettingsApplier）统一处理
            InitFromSettings(); // 回填 UI（事件已由 Set* 触发，此处再同步选中态）
        }

        /// <summary>关闭：overlay 弹栈（恢复主菜单 + 解暂停）；无 UIManager 兜底直接隐藏。</summary>
        void Close()
        {
            if (_uiManager != null) _uiManager.PopOverlay(Key); // 定向弹栈（2026-08-27 修复：无参弹栈顶可能弹错对象）
            else Hide();
        }

        protected override void OnBgClicked()
        {
            Close(); // 点背景关闭 = 弹 overlay（同 Btn_Close）
        }

        void BuildResolutionOptions()
        {
            var available = SettingsSystem.Instance.GetResolutions();
            var list = new List<Vector2Int>();
            // 只用 16:9 可用分辨率，优先常用项
            foreach (var c in CommonResolutions)
            {
                if (available.Contains(c)) list.Add(c);
            }
            // 常用项全不可用 → 回退：从可用里只取 16:9 横向项；再兜底硬编码常用项
            if (list.Count == 0)
            {
                foreach (var r in available)
                {
                    if (r.x >= r.y && Is16To9(r.x, r.y)) list.Add(r);
                    if (list.Count >= 6) break;
                }
            }
            if (list.Count == 0)
            {
                foreach (var c in CommonResolutions) list.Add(c);
            }
            // 当前分辨率如果不是 16:9，用最近的 16:9 代替，保证下拉里有且仅 16:9
            var cur = new Vector2Int(SettingsSystem.Instance.ResolutionWidth, SettingsSystem.Instance.ResolutionHeight);
            if (!Is16To9(cur.x, cur.y)) cur = ToNearest16To9(cur.x, cur.y);
            if (!list.Contains(cur)) list.Insert(0, cur);

            _resolutions.Clear();
            _resolutions.AddRange(list);
            var options = new List<string>();
            foreach (var r in _resolutions) options.Add($"{r.x} × {r.y}");
            _dpdResolution.ClearOptions();
            _dpdResolution.AddOptions(options);
        }

        static bool Is16To9(int w, int h)
        {
            return w > 0 && h > 0 && Mathf.Abs((float)w / h - 16f / 9f) < 0.01f;
        }

        static Vector2Int ToNearest16To9(int width, int height)
        {
            const float TargetAspect = 16f / 9f;
            if (width > 0 && height > 0 && Mathf.Abs((float)width / height - TargetAspect) < 0.01f)
            {
                return new Vector2Int(width, height);
            }

            if (height > 0)
            {
                int w = Mathf.Max(1, Mathf.RoundToInt(height * TargetAspect));
                return new Vector2Int(w, height);
            }

            if (width > 0)
            {
                int h = Mathf.Max(1, Mathf.RoundToInt(width / TargetAspect));
                return new Vector2Int(width, h);
            }

            return new Vector2Int(1920, 1080);
        }

        void SelectCurrentResolution()
        {
            var r = ToNearest16To9(SettingsSystem.Instance.ResolutionWidth, SettingsSystem.Instance.ResolutionHeight);
            for (int i = 0; i < _resolutions.Count; i++)
            {
                if (_resolutions[i].x == r.x && _resolutions[i].y == r.y)
                {
                    _dpdResolution.SetValueWithoutNotify(i);
                    return;
                }
            }
        }

        /// <summary>显示设置落地统一走 ScreenSettingsApplier（监听 SettingsChanged）——此处不直接改 Screen。</summary>
    }
}
