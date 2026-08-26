using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 设置系统：只存值 + 发事件（引擎级设置由监听者直接应用，Core 不知道游戏内容）。
    /// 持久化独立于存档：写入 persistentDataPath/settings.json，改动立即保存。
    /// </summary>
    public class SettingsSystem : BaseManager<SettingsSystem>
    {
        private int _bgmVolumePercent = 80;
        private int _sfxVolumePercent = 100;
        private bool _fullscreen = false; // 2026-08-26：默认窗口化 1920×1080（打包首帧即窗口；全屏由设置面板切换——全屏仍走原生分辨率+16:9 黑边）
        private int _resolutionWidth = 1920;
        private int _resolutionHeight = 1080;

        private string SettingsPath => Path.Combine(Application.persistentDataPath, "settings.json");

        /// <summary>音量/全屏/分辨率变化事件（AudioManager、屏幕管理器监听并应用）。</summary>
        public event Action SettingsChanged;

        public int BgmVolumePercent => _bgmVolumePercent;
        public int SfxVolumePercent => _sfxVolumePercent;
        public bool Fullscreen => _fullscreen;
        public int ResolutionWidth => _resolutionWidth;
        public int ResolutionHeight => _resolutionHeight;

        public void SetBGMVolumePercent(int percent)
        {
            _bgmVolumePercent = Mathf.Clamp(percent, 0, 100);
            SettingsChanged?.Invoke();
            SaveSettings();
        }

        public void SetSFXVolumePercent(int percent)
        {
            _sfxVolumePercent = Mathf.Clamp(percent, 0, 100);
            SettingsChanged?.Invoke();
            SaveSettings();
        }

        /// <summary>仅应用 BGM 音量（发 SettingsChanged 实时生效，不写盘）——设置面板滑条拖动实时生效用；落盘由面板节流合并。</summary>
        public void ApplyBGMVolumePercent(int percent)
        {
            _bgmVolumePercent = Mathf.Clamp(percent, 0, 100);
            SettingsChanged?.Invoke();
        }

        /// <summary>仅应用 SFX 音量（发 SettingsChanged 实时生效，不写盘）——设置面板滑条拖动实时生效用；落盘由面板节流合并。</summary>
        public void ApplySFXVolumePercent(int percent)
        {
            _sfxVolumePercent = Mathf.Clamp(percent, 0, 100);
            SettingsChanged?.Invoke();
        }

        public void SetFullscreen(bool fullscreen)
        {
            _fullscreen = fullscreen;
            SettingsChanged?.Invoke();
            SaveSettings();
        }

        public void SetResolution(int width, int height)
        {
            _resolutionWidth = width;
            _resolutionHeight = height;
            SettingsChanged?.Invoke();
            SaveSettings();
        }

        // ---- 设置序列化（独立 settings.json）----

        public void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                FromJson(File.ReadAllText(SettingsPath));
            }
            catch (Exception e)
            {
                Debug.LogError($"[SettingsSystem] 读取设置失败：{e.Message}");
            }
        }

        public void SaveSettings()
        {
            try
            {
                string tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, ToJson());
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                File.Move(tmp, SettingsPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SettingsSystem] 保存设置失败：{e.Message}");
            }
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(new SettingsState
            {
                Bgm = _bgmVolumePercent,
                Sfx = _sfxVolumePercent,
                Fullscreen = _fullscreen,
                Width = _resolutionWidth,
                Height = _resolutionHeight,
            });
        }

        public void FromJson(string json)
        {
            var state = JsonConvert.DeserializeObject<SettingsState>(json);
            if (state == null) return;
            _bgmVolumePercent = Mathf.Clamp(state.Bgm, 0, 100);
            _sfxVolumePercent = Mathf.Clamp(state.Sfx, 0, 100);
            _fullscreen = state.Fullscreen;
            _resolutionWidth = state.Width > 0 ? state.Width : 1920;
            _resolutionHeight = state.Height > 0 ? state.Height : 1080;
            SettingsChanged?.Invoke();
        }

        [Serializable]
        private class SettingsState
        {
            public int Bgm;
            public int Sfx;
            public bool Fullscreen;
            public int Width;
            public int Height;
        }
    }
}