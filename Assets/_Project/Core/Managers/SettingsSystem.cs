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

        /// <summary>可用分辨率列表（运行时过滤宽高 &gt; 0）。</summary>
        public List<Vector2Int> GetResolutions()
        {
            var list = new List<Vector2Int>();
            foreach (var res in Screen.resolutions)
            {
                var r = new Vector2Int(res.width, res.height);
                if (!list.Contains(r))
                {
                    list.Add(r);
                }
            }
            return list;
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
            _bgmVolumePercent = state.Bgm;
            _sfxVolumePercent = state.Sfx;
            _fullscreen = state.Fullscreen;
            _resolutionWidth = state.Width;
            _resolutionHeight = state.Height;
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