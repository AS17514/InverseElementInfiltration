using TheLaw.Data;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 运行时棋子视觉工厂：代码生成立绘/阴影贴图（测试占位），创建伪2D 棋子对象。
    /// 命名统一 Piece_{id}，供表现层（移动/死亡）查找。
    /// </summary>
    public static class PieceViewFactory
    {
        // defId → 颜色（测试占位配色）
        static readonly Color[] Palette =
        {
            new Color(0.29f, 0.56f, 0.85f), // 0 蓝（玩家）
            new Color(0.88f, 0.33f, 0.33f), // 1 红（敌方）
            new Color(0.30f, 0.69f, 0.35f), // 2 绿
            new Color(0.94f, 0.63f, 0.19f), // 3 橙
            new Color(0.61f, 0.35f, 0.71f), // 4 紫
            new Color(0.36f, 0.43f, 0.49f), // 5 灰
        };

        static Sprite _portraitSprite;
        static Sprite _shadowSprite;

        public static void EnsureSprites()
        {
            if (_portraitSprite != null) return;
            _portraitSprite = CreatePortraitSprite(); // 300×480，scale 1/6 → 0.5×0.8 格
            _shadowSprite = CreateShadowSprite();
        }

        /// <summary>在格子定位点创建棋子视觉（立绘 pivot 底部 + 阴影平贴 + 朝向相机）。</summary>
        public static GameObject CreatePieceView(int pieceId, Side side, Vector2Int cell, Color tint)
        {
            EnsureSprites();
            Vector3 pos = CellToWorld(cell);

            var root = new GameObject($"Piece_{pieceId}");
            root.transform.position = pos;

            // 立绘
            var portrait = new GameObject("Portrait");
            portrait.transform.SetParent(root.transform, false);
            var sr = portrait.AddComponent<SpriteRenderer>();
            sr.sprite = _portraitSprite;
            sr.color = tint;
            sr.sortingOrder = (int)(-cell.y * 100f) + 400;
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
            return Palette[defId % Palette.Length];
        }

        /// <summary>纯色矩形立绘贴图（32×64，比例 1:2——显示 0.5×0.8 格）。</summary>
        static Sprite CreateSolidSprite(int w, int h, Color color)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = color;
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0f), 100f); // pivot 底部
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
