using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace TheLaw.Core
{
    /// <summary>
    /// 音频管理器（适合本项目规模的轻量架构）：
    /// - BGM：双 AudioSource + 交叉淡化（Addressables 加载、缓存、同曲不重复切）
    /// - SFX：8 路 AudioSource 对象池 + 轮转（PlayOneShot），支持音量/音高缩放与轻微随机（防重复疲劳）
    /// - 音量：监听 SettingsSystem.SettingsChanged（BGM/SFX 分路控制）
    /// 加载方式 Addressables；剪辑缺失只 LogWarning，绝不中断游戏（Null 安全）。
    /// </summary>
    public class AudioManager : SingletonAutoMono<AudioManager>
    {
        private const int SfxPoolSize = 8;          // SFX 并发路数（战斗规模足够）
        private const float BgmFadeDuration = 0.8f; // BGM 交叉淡化时长
        private const float SfxPitchJitter = 0.03f; // SFX 轻微随机音高（±3%）

        private AudioSource _bgmA;   // BGM 通道 A
        private AudioSource _bgmB;   // BGM 通道 B（交叉淡化用）
        private bool _bgmUseA = true; // 当前承载新曲的通道
        private Coroutine _bgmFadeRoutine;

        private readonly List<AudioSource> _sfxPool = new List<AudioSource>();
        private readonly List<string> _sfxPlayingAddress = new List<string>(); // 每路当前播放的 SFX 地址（restartIfPlaying 查重用）
        private int _nextSfxIndex;
        private readonly Dictionary<string, AudioClip> _clipCache = new Dictionary<string, AudioClip>();

        private float _bgmVolume = 1f;
        private float _sfxVolume = 1f;
        private AudioSource _currentBgmSource; // 当前承载新曲的通道（CompleteBGMCrossfade 直落用）
        private bool _bgmFadeSkip;             // 开场跳过标记：下一次/进行中淡化直落满音量

        protected override void OnDestroy()
        {
            if (SettingsSystem.Instance != null)
            {
                SettingsSystem.Instance.SettingsChanged -= ApplySettings;
            }
            base.OnDestroy();
        }

        private void Start()
        {
            CreateSources();
            SettingsSystem.Instance.SettingsChanged += ApplySettings;
            ApplySettings();
        }

        private void CreateSources()
        {
            _bgmA = CreateSource("BGM_A", loop: true);
            _bgmB = CreateSource("BGM_B", loop: true);

            for (int i = 0; i < SfxPoolSize; i++)
            {
                _sfxPool.Add(CreateSource($"SFX_{i}", loop: false));
                _sfxPlayingAddress.Add("");
            }
        }

        private AudioSource CreateSource(string name, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = loop;
            src.spatialBlend = 0f; // 2D（UI/战斗 UI 音）
            return src;
        }

        // ========== BGM ==========

        /// <summary>播放/切换 BGM（Addressables 地址）。同曲不重复切；异步加载完成后交叉淡化。</summary>
        /// <param name="fadeSeconds">淡化时长（秒）；&lt;0 = 默认 BgmFadeDuration（0.8s）——开场动画长渐入传正值。</param>
        public void PlayBGM(string address, float fadeSeconds = -1f)
        {
            if (string.IsNullOrEmpty(address)) return;
            GetOrLoadClip(address, clip =>
            {
                if (clip == null) return;
                var active = _bgmUseA ? _bgmA : _bgmB;
                if (active.isPlaying && active.clip == clip) return; // 同曲不重复切
                CrossfadeTo(clip, fadeSeconds);
            });
        }

        /// <summary>停掉 BGM。</summary>
        public void StopBGM()
        {
            if (_bgmFadeRoutine != null)
            {
                StopCoroutine(_bgmFadeRoutine);
                _bgmFadeRoutine = null;
            }
            _bgmA.Stop();
            _bgmB.Stop();
            _bgmA.clip = null;
            _bgmB.clip = null;
            _bgmFadeSkip = false;
        }

        private void CrossfadeTo(AudioClip clip, float fadeSeconds)
        {
            if (_bgmFadeRoutine != null) StopCoroutine(_bgmFadeRoutine);
            _bgmFadeRoutine = StartCoroutine(CrossfadeRoutine(clip, fadeSeconds));
        }

        private IEnumerator CrossfadeRoutine(AudioClip clip, float fadeSeconds)
        {
            float duration = fadeSeconds >= 0f ? Mathf.Max(0.05f, fadeSeconds) : BgmFadeDuration;
            // 新曲走空闲通道，旧曲淡出新曲淡入
            var newSrc = _bgmUseA ? _bgmB : _bgmA;
            var oldSrc = _bgmUseA ? _bgmA : _bgmB;
            _bgmUseA = !_bgmUseA;
            _currentBgmSource = newSrc;

            newSrc.clip = clip;
            newSrc.volume = 0f;
            newSrc.Play();

            // 开场动画跳过（CompleteBGMCrossfade）后启动的淡化：直接满音量，不再渐入
            if (_bgmFadeSkip)
            {
                _bgmFadeSkip = false;
                oldSrc.Stop();
                oldSrc.clip = null;
                newSrc.volume = _bgmVolume;
                _bgmFadeRoutine = null;
                yield break;
            }

            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime; // unscaled——暂停型（设置/确认）时 BGM 不冻结
                float k = Mathf.Clamp01(t / duration);
                oldSrc.volume = _bgmVolume * (1f - k);
                newSrc.volume = _bgmVolume * k;
                yield return null;
            }
            oldSrc.Stop();
            oldSrc.clip = null;
            newSrc.volume = _bgmVolume;
            _bgmFadeRoutine = null;
        }

        /// <summary>立即完成当前/下一次 BGM 交叉淡化（开场动画跳过用——直落满音量，不等渐入）。</summary>
        public void CompleteBGMCrossfade()
        {
            _bgmFadeSkip = true; // 未开始的淡化直接满音量起步
            if (_bgmFadeRoutine != null)
            {
                StopCoroutine(_bgmFadeRoutine);
                _bgmFadeRoutine = null;
            }
            var keep = _currentBgmSource;
            foreach (var src in new[] { _bgmA, _bgmB })
            {
                if (src == null) continue;
                if (src == keep && src.isPlaying)
                {
                    src.volume = _bgmVolume; // 新通道直落满音量
                }
                else
                {
                    src.Stop();
                    src.clip = null;
                }
            }
        }

        // ========== SFX ==========

        /// <summary>
        /// 播放一次性音效（Addressables 地址）。volumeScale/pitch 可选覆盖；内置轻微随机音高。
        /// restartIfPlaying=true（防重叠标记）：若同一地址音效正在播放，先停掉旧实例再从头重播——
        /// 用于 UI 碰撞音等高频同类音（连点/同帧触发时"盖掉旧声"而非叠音）。
        /// </summary>
        public void PlaySFX(string address, float volumeScale = 1f, float pitch = 1f, bool restartIfPlaying = false)
        {
            if (string.IsNullOrEmpty(address)) return;
            GetOrLoadClip(address, clip =>
            {
                if (clip == null) return;
                int index = -1;
                if (restartIfPlaying)
                {
                    // 查正在播放同地址的路：命中则停旧重播（重新计时）；未命中走轮转
                    for (int i = 0; i < _sfxPool.Count; i++)
                    {
                        if (_sfxPool[i].isPlaying && i < _sfxPlayingAddress.Count && _sfxPlayingAddress[i] == address)
                        {
                            index = i;
                            break;
                        }
                    }
                }
                if (index < 0)
                {
                    index = _nextSfxIndex;
                    _nextSfxIndex = (_nextSfxIndex + 1) % _sfxPool.Count;
                }
                if (index < _sfxPlayingAddress.Count) _sfxPlayingAddress[index] = address;

                var src = _sfxPool[index];
                if (src.isPlaying) src.Stop(); // 重播路径先停旧实例；轮转路径顺带清理（防同一路叠音）
                float jitter = 1f + Random.Range(-SfxPitchJitter, SfxPitchJitter);
                src.pitch = Mathf.Max(0.1f, pitch * jitter);
                src.PlayOneShot(clip, _sfxVolume * Mathf.Clamp01(volumeScale));
            });
        }

        // ========== 加载与音量 ==========

        private void GetOrLoadClip(string address, System.Action<AudioClip> onReady)
        {
            if (_clipCache.TryGetValue(address, out var cached))
            {
                onReady(cached);
                return;
            }
            var handle = Addressables.LoadAssetAsync<AudioClip>(address);
            handle.Completed += op =>
            {
                if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
                {
                    _clipCache[address] = handle.Result;
                    onReady(handle.Result);
                }
                else
                {
                    Debug.LogWarning($"[AudioManager] 音频缺失（Addressables 无此地址）：{address}——请补 Assets/Audio 下资源并入 Addressables");
                    onReady(null);
                }
            };
        }

        private void ApplySettings()
        {
            var s = SettingsSystem.Instance;
            if (s == null) return;
            _bgmVolume = s.BgmVolumePercent / 100f;
            _sfxVolume = s.SfxVolumePercent / 100f;
            if (_bgmA != null)
            {
                _bgmA.volume = _bgmVolume;
                _bgmB.volume = _bgmVolume;
            }
            foreach (var src in _sfxPool)
            {
                if (src != null) src.volume = _sfxVolume;
            }
        }
    }
}