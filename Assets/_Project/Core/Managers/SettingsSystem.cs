using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 设置系统：只存值 + 发事件（引擎级设置由监听者直接应用，Core 不知道游戏内容）。
    /// </summary>
    public class SettingsSystem : BaseManager<SettingsSystem>, ISnapshot
    {
        private int _bgmVolumePercent = 80;
        private int _sfxVolumePercent = 100;
        private bool _fullscreen = true;
        private int _resolutionWidth = 1920;
        private int _resolutionHeight = 1080;

        public string Key => "SettingsSystem";

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
        }

        public void SetSFXVolumePercent(int percent)
        {
            _sfxVolumePercent = Mathf.Clamp(percent, 0, 100);
            SettingsChanged?.Invoke();
        }

        public void SetFullscreen(bool fullscreen)
        {
            _fullscreen = fullscreen;
            SettingsChanged?.Invoke();
        }

        public void SetResolution(int width, int height)
        {
            _resolutionWidth = width;
            _resolutionHeight = height;
            SettingsChanged?.Invoke();
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

        // ---- ISnapshot ----

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
