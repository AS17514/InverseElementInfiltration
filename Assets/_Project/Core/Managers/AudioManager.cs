using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 音频管理器（SingletonAutoMono）：监听设置事件应用音量。
    /// 骨架：实际播放（AudioSource/音效资源）由 UI/资源同学补充。
    /// </summary>
    public class AudioManager : SingletonAutoMono<AudioManager>
    {
        private AudioSource _bgmSource;
        private AudioSource _sfxSource;

        protected override void OnDestroy()
        {
            base.OnDestroy();
        }

        private void Start()
        {
            _bgmSource = gameObject.AddComponent<AudioSource>();
            _bgmSource.loop = true;
            _sfxSource = gameObject.AddComponent<AudioSource>();
            SettingsSystem.Instance.SettingsChanged += ApplySettings;
            ApplySettings();
        }

        /// <summary>播放背景音乐（clipPath 为资源路径，加载由资源同学补）。</summary>
        public void PlayBGM(string clipPath)
        {
            // TODO: Addressables 加载后播放
            _bgmSource.Play();
        }

        /// <summary>播放音效（clipPath 为资源路径，加载由资源同学补）。</summary>
        public void PlaySFX(string clipPath)
        {
            // TODO: Addressables 加载后播放
            _sfxSource.Play();
        }

        public void SetVolume(float volume)
        {
            _bgmSource.volume = volume;
            _sfxSource.volume = volume;
        }

        private void ApplySettings()
        {
            var settings = SettingsSystem.Instance;
            _bgmSource.volume = settings.BgmVolumePercent / 100f;
            _sfxSource.volume = settings.SfxVolumePercent / 100f;
        }
    }
}
