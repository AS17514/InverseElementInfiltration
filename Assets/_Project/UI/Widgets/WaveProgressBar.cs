using System.Collections;
using System.Collections.Generic;
using TheLaw.Data;
using TheLaw.Gameplay;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 心电图式波次进度条（2026-08-24，李毕拼版方案）：
    /// - 挂在主场景 BackgroundCanvas 上，操作其直接子节点 Grp_WaveNodes（已挂 HorizontalLayoutGroup）
    /// - 进层后按 FloorConfig.waveDefs 生成等宽 WaveNode 段：准备段(开始波 battle_progress) + 每回合 1 段
    /// - 段类型（段索引=TurnCount）：大波=部署回合（startTurn-1==t）；中波=升变执行回合（startTurn-2==t）；小波=末波后倒计时（t>=末波 startTurn）；其余空波
    /// - 图：pivot.y 按类型校准（共线），SetNativeSize 后等比缩放到段宽（段宽优先）
    /// - 进度驱动：透明度硬切——已到达段（nodeIndex ≤ TurnCount）alpha=1，未到达 alpha=UnreachedAlpha
    /// - 若场景未手动挂载本组件，运行时会自动附加（AfterSceneLoad 兜底）
    /// </summary>
    public class WaveProgressBar : MonoBehaviour
    {
        [Header("素材地址（工具/战斗美术进 Addressables 注册，地址=文件名）")]
        public string StartWaveAddress = "battle_progress";
        public string BigWaveAddress = "battle_wave_big";
        public string EmptyWaveAddress = "battle_wave_empty";
        public string MidWaveAddress = "battle_wave_mid";
        public string SmallWaveAddress = "battle_wave_small";

        [Header("pivot.y 校准（美术给定，保证各图共线）")]
        public float PivotStart = 0.5f;
        public float PivotBig = 0.41f;
        public float PivotEmpty = 0.45f;
        public float PivotMid = 0.32f;
        public float PivotSmall = 0.53f;

        [Header("表现")]
        [Range(0f, 1f)] public float UnreachedAlpha = 0.25f;

        RectTransform _root;
        readonly List<RectTransform> _segments = new List<RectTransform>();
        readonly List<Image> _images = new List<Image>();
        readonly List<AsyncOperationHandle<Sprite>> _handles = new List<AsyncOperationHandle<Sprite>>();
        int _builtFloor = -1;
        int _lastTurn = -1;
        bool _building;
        bool _warnedNoRoot;

        void Start()
        {
            _root = transform.Find("Grp_WaveNodes") as RectTransform;
            if (_root == null)
            {
                var bg = GameObject.Find("BackgroundCanvas");
                if (bg != null) _root = bg.transform.Find("Grp_WaveNodes") as RectTransform;
            }
        }

        void Update()
        {
            if (_root == null)
            {
                if (!_warnedNoRoot)
                {
                    _warnedNoRoot = true;
                    Debug.LogWarning("[WaveProgressBar] 找不到 Grp_WaveNodes（BackgroundCanvas 直接子节点）——波次进度条不生效");
                }
                return;
            }
            var state = GameState.Instance;
            if (state == null) return;
            var cfg = state.CurrentFloorConfig;
            if (cfg == null || cfg.waveDefs == null || cfg.waveDefs.Count == 0) return;

            if (_builtFloor != state.CurrentFloor && !_building)
            {
                _building = true;
                StartCoroutine(BuildCoroutine(cfg));
            }
            if (_lastTurn != state.TurnCount)
            {
                _lastTurn = state.TurnCount;
                ApplyAlpha(state.TurnCount);
            }
        }

        IEnumerator BuildCoroutine(FloorConfig cfg)
        {
            _builtFloor = -1; // 构建失败可重试
            ClearSegments();

            var startH = Addressables.LoadAssetAsync<Sprite>(StartWaveAddress);
            var bigH = Addressables.LoadAssetAsync<Sprite>(BigWaveAddress);
            var emptyH = Addressables.LoadAssetAsync<Sprite>(EmptyWaveAddress);
            var midH = Addressables.LoadAssetAsync<Sprite>(MidWaveAddress);
            var smallH = Addressables.LoadAssetAsync<Sprite>(SmallWaveAddress);
            _handles.Add(startH); _handles.Add(bigH); _handles.Add(emptyH); _handles.Add(midH); _handles.Add(smallH);
            yield return startH; yield return bigH; yield return emptyH; yield return midH; yield return smallH;

            if (_root == null) { _building = false; yield break; }

            var waveDefs = cfg.waveDefs;
            int totalTurns = 0;
            if (waveDefs.Count > 0)
            {
                var last = waveDefs[waveDefs.Count - 1];
                totalTurns = Mathf.Max(1, last.startTurn - 1 + Mathf.Max(0, last.endCountdown) - 1);
            }
            int nodeCount = totalTurns + 1; // 准备段 + 每回合 1 段
            float segW = _root.rect.width > 1f ? _root.rect.width / nodeCount : 100f / nodeCount;

            for (int i = 0; i < nodeCount; i++)
            {
                WaveKind kind = i == 0 ? WaveKind.Start : KindForTurn(i, waveDefs);
                var sprite = SpriteFor(kind, startH, bigH, emptyH, midH, smallH);
                var pivotY = PivotFor(kind);
                CreateSegment(i, kind, sprite, pivotY, segW);
            }
            _builtFloor = GameState.Instance != null ? GameState.Instance.CurrentFloor : -1;
            _building = false;
            ApplyAlpha(GameState.Instance != null ? GameState.Instance.TurnCount : 0);
        }

        void CreateSegment(int index, WaveKind kind, Sprite sprite, float pivotY, float segW)
        {
            var nodeGo = new GameObject($"WaveNode_{index}", typeof(RectTransform), typeof(LayoutElement));
            var nodeRt = (RectTransform)nodeGo.transform;
            nodeRt.SetParent(_root, false);
            nodeRt.anchorMin = new Vector2(0, 0.5f);
            nodeRt.anchorMax = new Vector2(1, 0.5f);
            nodeRt.sizeDelta = new Vector2(0, 0);
            var le = nodeGo.GetComponent<LayoutElement>();
            le.preferredWidth = segW;

            var imgGo = new GameObject("img", typeof(RectTransform), typeof(Image));
            var imgRt = (RectTransform)imgGo.transform;
            imgRt.SetParent(nodeRt, false);
            imgRt.anchorMin = new Vector2(0.5f, 0.5f);
            imgRt.anchorMax = new Vector2(0.5f, 0.5f);
            imgRt.anchoredPosition = Vector2.zero;
            imgRt.pivot = new Vector2(0.5f, pivotY);
            var img = imgGo.GetComponent<Image>();
            img.sprite = sprite;
            img.raycastTarget = false;
            if (sprite != null)
            {
                img.SetNativeSize();
                var sz = imgRt.sizeDelta;
                float scale = sz.x > 0.001f ? segW / sz.x : 1f; // 段宽优先：等比缩放宽度到段宽，高度按比例
                imgRt.sizeDelta = new Vector2(segW, sz.y * scale);
            }
            else
            {
                imgRt.sizeDelta = new Vector2(segW, 0);
                Debug.LogWarning($"[WaveProgressBar] 素材缺失（{kind}）——段 {index} 透明");
            }

            _segments.Add(nodeRt);
            _images.Add(img);
        }

        WaveKind KindForTurn(int t, List<WaveDef> waveDefs)
        {
            // 段索引 = TurnCount（敌方回合结束次数）；波次部署回合 s ↔ TurnCount=s-1（后端 2026-08-24 时序：预告 TC=s-3、升变 TC=s-2、部署 TC=s-1）
            // 大波：该段有波次部署（startTurn-1 == t；startTurn=1 部署在 TurnCount=0 准备段已覆盖）
            foreach (var w in waveDefs)
            {
                if (w.startTurn - 1 == t) return WaveKind.Big;
            }
            // 中波：升变执行回合（startTurn-2 == t）——有升变配置（旧 promotions 或 autoPromote）的波
            foreach (var w in waveDefs)
            {
                bool hasPromo = w.autoPromote || (w.promotions != null && w.promotions.Count > 0);
                if (w.startTurn - 2 == t) return WaveKind.Mid;
            }
            // 小波：末波部署后的倒计时段（t >= 末波 startTurn）
            var last = waveDefs[waveDefs.Count - 1];
            if (t >= last.startTurn) return WaveKind.Small;
            return WaveKind.Empty;
        }

        Sprite SpriteFor(WaveKind kind,
            AsyncOperationHandle<Sprite> start, AsyncOperationHandle<Sprite> big,
            AsyncOperationHandle<Sprite> empty, AsyncOperationHandle<Sprite> mid, AsyncOperationHandle<Sprite> small)
        {
            switch (kind)
            {
                case WaveKind.Start: return start.IsValid() ? start.Result : null;
                case WaveKind.Big: return big.IsValid() ? big.Result : null;
                case WaveKind.Mid: return mid.IsValid() ? mid.Result : null;
                case WaveKind.Small: return small.IsValid() ? small.Result : null;
                default: return empty.IsValid() ? empty.Result : null;
            }
        }

        float PivotFor(WaveKind kind)
        {
            switch (kind)
            {
                case WaveKind.Start: return PivotStart;
                case WaveKind.Big: return PivotBig;
                case WaveKind.Mid: return PivotMid;
                case WaveKind.Small: return PivotSmall;
                default: return PivotEmpty;
            }
        }

        void ApplyAlpha(int turnCount)
        {
            for (int i = 0; i < _images.Count; i++)
            {
                var img = _images[i];
                if (img == null) continue;
                var c = img.color;
                c.a = i <= turnCount ? 1f : UnreachedAlpha;
                img.color = c;
            }
        }

        void ClearSegments()
        {
            foreach (var h in _handles) if (h.IsValid()) Addressables.Release(h);
            _handles.Clear();
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i] != null) Destroy(_segments[i].gameObject);
            }
            _segments.Clear();
            _images.Clear();
        }

        void OnDestroy()
        {
            foreach (var h in _handles) if (h.IsValid()) Addressables.Release(h);
            _handles.Clear();
        }

        enum WaveKind { Start, Big, Empty, Mid, Small }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoAttach()
        {
            var bg = GameObject.Find("BackgroundCanvas");
            if (bg == null) return;
            if (bg.GetComponent<WaveProgressBar>() == null) bg.AddComponent<WaveProgressBar>();
        }
    }
}
