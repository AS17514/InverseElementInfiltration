using System.Collections;
using System.Collections.Generic;
using TheLaw.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 设置面板：音量（BGM/SFX Slider + 数值）、全屏（Toggle）。分辨率 UI 已移除（2026-08-26：下拉损坏不修、prefab 隐藏；窗口尺寸由 settings.json 默认窗口化落地）。
    /// 改动实时生效并写入独立 settings.json；仅 Btn_Close 关闭。
    /// 模态：IsPausing=true；允许点背景关闭（CloseOnBgClick=true——PanleBase 自动加背景点击 + Grp_ 阻挡）。
    /// </summary>
    public class SettingsPanel : PanelBase
    {
        public override string Key => "Settings";
        public override bool IsPausing => true;
        protected override bool CloseOnBgClick => true;

        private Slider _sldBgm;
        private Slider _sldSfx;
        private Toggle _togFullscreen;
        private TMP_Text _txtBgmV;
        private TMP_Text _txtSfxV;
        private Button _btnReset;
        private Button _btnClose;
        private UIManager _uiManager; // overlay(设置盖在主菜单上) 关闭用；空 = 直接 Hide 兜底

        private Coroutine _layoutRefreshRoutine;
        private Coroutine _volumeSaveRoutine; // 音量滑条拖动写盘节流（≥300ms 合并——避免每 tick 全量写 settings.json）
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
                    SettingsSystem.Instance.ApplyBGMVolumePercent(Mathf.RoundToInt(v * 100f));
                    if (_txtBgmV != null) _txtBgmV.text = $"{Mathf.RoundToInt(v * 100f)}";
                    ScheduleVolumeSave();
                });
            }
            if (_sldSfx != null)
            {
                _sldSfx.minValue = 0f;
                _sldSfx.maxValue = 1f;
                _sldSfx.onValueChanged.AddListener(v =>
                {
                    SettingsSystem.Instance.ApplySFXVolumePercent(Mathf.RoundToInt(v * 100f));
                    if (_txtSfxV != null) _txtSfxV.text = $"{Mathf.RoundToInt(v * 100f)}";
                    ScheduleVolumeSave();
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
        }

        void OnReset()
        {
            UiSfx.Play(); // 重置按钮碰撞音（2026-08-24 音频挂点方案）
            var s = SettingsSystem.Instance;
            s.SetBGMVolumePercent(80);
            s.SetSFXVolumePercent(100);
            s.SetFullscreen(false); // ⚠️ 2026-08-26：恢复默认 = 窗口化（SettingsSystem 默认窗口化 1920×1080；全屏由用户主动切换）
            s.SetResolution(1920, 1080); // 音量/显示落地由监听者（AudioManager/ScreenSettingsApplier）统一处理
            InitFromSettings(); // 回填 UI（事件已由 Set* 触发，此处再同步选中态）
        }

        /// <summary>关闭：overlay 弹栈（恢复主菜单 + 解暂停）；无 UIManager 兜底直接隐藏。</summary>
        void Close()
        {
            if (_uiManager != null) _uiManager.PopOverlay(Key); // 定向弹栈（2026-08-27 修复：无参弹栈顶可能弹错对象）
            else Hide();
        }

        /// <summary>音量写盘节流：拖动期间 ≥300ms 合并一次落盘（实时生效由 Apply* 发 SettingsChanged 保证，不因节流受影响）。</summary>
        void ScheduleVolumeSave()
        {
            if (_volumeSaveRoutine != null) StopCoroutine(_volumeSaveRoutine);
            _volumeSaveRoutine = StartCoroutine(SaveSettingsDelayed());
        }

        System.Collections.IEnumerator SaveSettingsDelayed()
        {
            yield return new WaitForSecondsRealtime(0.3f);
            _volumeSaveRoutine = null;
            SettingsSystem.Instance.SaveSettings();
        }

        /// <summary>面板隐藏/关闭时若还有待写音量，立即落盘（防节流窗口内关面板丢最终值）。</summary>
        protected override void OnHide()
        {
            if (_volumeSaveRoutine != null)
            {
                StopCoroutine(_volumeSaveRoutine);
                _volumeSaveRoutine = null;
                SettingsSystem.Instance.SaveSettings();
            }
        }

        protected override void OnBgClicked()
        {
            Close(); // 点背景关闭 = 弹 overlay（同 Btn_Close）
        }


        /// <summary>显示设置落地统一走 ScreenSettingsApplier（监听 SettingsChanged）——此处不直接改 Screen。</summary>
    }
}
