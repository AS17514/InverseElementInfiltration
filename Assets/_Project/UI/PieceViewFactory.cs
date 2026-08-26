using System.Collections.Generic;
using TheLaw.Data;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace TheLaw.UI
{
    /// <summary>
    /// 运行时棋子视觉工厂：美术立绘（Addressables 预载）+ 阴影贴图，创建伪2D 棋子对象。
    /// 命名统一 Piece_{id}，供表现层（移动/死亡）查找。
    /// 立绘 pivot = 底部中心（底座落地），SpriteRenderer billboard 朝向相机（同色 tint 标识阵营/种类）。
    /// </summary>
    public static class PieceViewFactory
    {
        // 2026-08-23：战斗场上棋子按阵营染色——我方浅蓝 B3D5FF / 敌方浅红 FFB4B4（取代原"原色直出"与占位 defId tint）
        static readonly Color PlayerFactionTint = new Color(0.7019608f, 0.8352941f, 1f); // #B3D5FF 我方浅蓝
        static readonly Color EnemyFactionTint = new Color(1f, 0.7058824f, 0.7058824f);   // #FFB4B4 敌方浅红

        // defId → 颜色（测试占位配色；有美术后仅作阵营/种类区分 tint）
        static readonly Color[] Palette =
        {
            new Color(0.29f, 0.56f, 0.85f), // 0 蓝（玩家）
            new Color(0.88f, 0.33f, 0.33f), // 1 红（敌方）
            new Color(0.30f, 0.69f, 0.35f), // 2 绿
            new Color(0.94f, 0.63f, 0.19f), // 3 橙
            new Color(0.61f, 0.35f, 0.71f), // 4 紫
            new Color(0.36f, 0.43f, 0.49f), // 5 灰
        };

        static readonly Dictionary<string, Sprite> _portraits = new Dictionary<string, Sprite>(); // 美术立绘（key=PieceDef 资产名）
        static Sprite _shadowSprite;
        static Sprite _placeholderPortrait; // 占位立绘（缺美术回退——静态缓存一张，避免每棋 new 300×480 纹理）
        static bool _preloading;

        /// <summary>预载全部棋子美术立绘（Addressables 地址 = PieceDef 资产名，如 Soldier）。
        /// 同步 WaitForCompletion：12 张小图启动阻塞可忽略，保证首次进战斗前立绘就绪。</summary>
        public static void PreloadPortraits()
        {
            if (_preloading) return;
            _preloading = true;
            var defs = ConfigTable.All<PieceDef>();
            int loaded = 0, total = 0;
            foreach (var def in defs)
            {
                if (def == null || string.IsNullOrEmpty(def.name) || _portraits.ContainsKey(def.name)) continue;
                total++;
                var handle = Addressables.LoadAssetAsync<Sprite>(def.name);
                var sp = handle.WaitForCompletion();
                if (sp != null && sp.name == def.name)
                {
                    _portraits[def.name] = sp;
                    loaded++;
                }
                else
                {
                    Debug.LogWarning($"[PieceView] 立绘加载失败：{def.name}");
                }
            }
            Debug.Log($"[PieceView] 立绘预载完成：{loaded}/{total}");
            _preloading = false;
        }

        /// <summary>按 PieceDef 资产名读取 Bootstrap 已预载的立绘，不发起新的 Addressables 请求。</summary>
        public static bool TryGetPreloadedPortrait(string portraitKey, out Sprite sprite)
        {
            sprite = null;
            return !string.IsNullOrEmpty(portraitKey)
                && _portraits.TryGetValue(portraitKey, out sprite)
                && sprite != null;
        }

        public static void EnsureSprites()
        {
            if (_shadowSprite != null) return;
            _shadowSprite = CreateShadowSprite();
        }

        /// <summary>在格子定位点创建棋子视觉（立绘 pivot 底部 + 阴影平贴 + 朝向相机）。</summary>
        public static GameObject CreatePieceView(int pieceId, int defId, Side side, Vector2Int cell, Color tint)
        {
            EnsureSprites();
            Vector3 pos = CellToWorld(cell);

            var root = new GameObject($"Piece_{pieceId}");
            root.transform.position = pos;

            // 立绘（美术优先，缺则占位）
            var portrait = new GameObject("Portrait");
            portrait.transform.SetParent(root.transform, false);
            var sr = portrait.AddComponent<SpriteRenderer>();
            sr.sprite = PortraitFor(defId);
            // 2026-08-23：按阵营染色（我方浅蓝 / 敌方浅红）——tint 参数为占位期遗留，此处以 side 为准
            sr.color = side == Side.Player ? PlayerFactionTint : EnemyFactionTint;
            sr.sortingOrder = (int)(-cell.y * 100f) + 400;
            // 描边仅用于敌方升变预告；玩家棋子不创建无效的独立材质实例。
            if (side == Side.Enemy)
            {
                var promotionOutline = portrait.AddComponent<PromotionOutlineView>();
                promotionOutline.Initialize(sr);
            }
            portrait.transform.localScale = new Vector3(1f / 6f, 1f / 6f, 1f / 6f);
            // 伪2D 朝向：相机固定 → 创建时算一次角度（绕 X 倾斜对准相机，锁 Y/Z）
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 toCam = cam.transform.position - portrait.transform.position;
                float horiz = new Vector2(toCam.x, toCam.z).magnitude;
                float angle = Mathf.Atan2(toCam.y, horiz) * Mathf.Rad2Deg;
                portrait.transform.rotation = Quaternion.Euler(angle, 0f, 0f);
            }

            // 阴影（伪阴影平贴）
            var shadow = new GameObject("Shadow");
            shadow.transform.SetParent(root.transform, false);
            var ssr = shadow.AddComponent<SpriteRenderer>();
            ssr.sprite = _shadowSprite;
            // ⚠️ 2026-08-12 修复：原 sortingOrder=0 与高处敌人（cell.y≥4 → -y*100+400 ≤ 0）同层冲突——
            // 同层按 z 排序时阴影(局部 z=0.02 更近相机)后画 → 阴影盖在敌人身上。固定极小值保证永远在所有棋子之下。
            ssr.sortingOrder = -1000;
            shadow.transform.localPosition = new Vector3(0f, 0.02f, 0f);
            shadow.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shadow.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);

            return root;
        }

        /// <summary>defId → 美术立绘（按 PieceDef 资产名查缓存；未预载/缺失回退占位白框）。</summary>
        public static bool UpdatePortrait(GameObject pieceView, int defId)
        {
            if (pieceView == null) return false;
            var portrait = pieceView.transform.Find("Portrait");
            var renderer = portrait != null ? portrait.GetComponent<SpriteRenderer>() : null;
            if (renderer == null) return false;
            renderer.sprite = PortraitFor(defId);
            return true;
        }

        static Sprite PortraitFor(int defId)
        {
            var def = ConfigTable.Find<PieceDef>(defId);
            if (def != null && !string.IsNullOrEmpty(def.name) && _portraits.TryGetValue(def.name, out var sprite) && sprite != null)
            {
                return sprite;
            }
            if (def == null && IconLibrary.TryGet("GoPiece", out var goSprite) && goSprite != null)
            {
                return goSprite; // 围棋立绘（代码内建 def——IconLibrary 预载，2026-08-26）
            }
            if (_placeholderPortrait == null) _placeholderPortrait = CreatePortraitSprite();
            return _placeholderPortrait; // 占位兜底（美术缺失/未预载完——静态缓存复用）
        }

        public static Vector3 CellToWorld(Vector2Int cell)
        {
            return new Vector3(cell.x - 3.5f, 0f, cell.y - 3.5f - 0.2f); // 屏幕下方偏 0.2（伪2D 定位点）
        }

        public static Vector2Int CellFromWorld(Vector3 world)
        {
            return new Vector2Int(Mathf.RoundToInt(world.x + 3.5f), Mathf.RoundToInt(world.z + 3.5f));
        }

        public static Color TintFor(int defId)
        {
            // ⚠️ 2026-08-26 修复：围棋 DefId=-1（GoPiece 代码内建）——负数取模会越界（蓝方部署必炸，视图不创建）；改正模
            int idx = defId % Palette.Length;
            if (idx < 0) idx += Palette.Length;
            return Palette[idx];
        }

        /// <summary>占位立绘贴图（300×480 比例 5:8——scale 1/6 → 0.5×0.8 格，与场景 TestPiece 同规格）。</summary>
        static Sprite CreatePortraitSprite()
        {
            const int w = 300, h = 480;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            Color32 outline = new Color32(28, 28, 32, 255);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool edge = x < 4 || x >= w - 4 || y < 4 || y >= h - 4;
                    px[y * w + x] = edge ? outline : new Color32(255, 255, 255, 255);
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0f), 100f);
        }

        /// <summary>伪阴影椭圆贴图（256×96 同 test 规格，scale 0.3 → 0.768×0.288 单位）。</summary>
        static Sprite CreateShadowSprite()
        {
            const int w = 256, h = 96;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float nx = (x - w * 0.5f) / (w * 0.5f);
                    float ny = (y - h * 0.5f) / (h * 0.5f);
                    float d = Mathf.Sqrt(nx * nx + ny * ny);
                    float a = d < 0.8f ? 0.6f : Mathf.Lerp(0.6f, 0f, (d - 0.8f) / 0.2f);
                    px[y * w + x] = new Color(0f, 0f, 0f, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
