using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace TheLaw.UI
{
    /// <summary>
    /// 图标资源库（2026-08-26 前端，程序块/围棋/麻将图标接入）：
    /// Addressables 预载 + 只读查询（与 PieceViewFactory 立绘同模式——启动同步 WaitForCompletion，小图可忽略阻塞）。
    /// 地址 = 资产文件名（IconArtTools 注册：move_step / attack_melee / GoPiece / mahjong_1 ...）。
    /// </summary>
    public static class IconLibrary
    {
        static readonly string[] PreloadKeys =
        {
            // 程序块·移动（4 族）
            "move_step", "move_area", "move_jump", "move_combo",
            // 程序块·攻击（5 方式）
            "attack_melee", "attack_melee_aoe", "attack_direct", "attack_arcing", "attack_spell",
            // 围棋立绘（代码内建 def——供 PieceViewFactory 查询）
            "GoPiece",
            // 麻将牌 1-9
            "mahjong_1", "mahjong_2", "mahjong_3", "mahjong_4", "mahjong_5",
            "mahjong_6", "mahjong_7", "mahjong_8", "mahjong_9",
        };

        static readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        static bool _preloading;

        public static void PreloadIcons()
        {
            if (_preloading) return;
            _preloading = true;
            int loaded = 0;
            foreach (var key in PreloadKeys)
            {
                if (_sprites.ContainsKey(key)) continue;
                var handle = Addressables.LoadAssetAsync<Sprite>(key);
                var sp = handle.WaitForCompletion();
                if (sp != null && sp.name == key)
                {
                    _sprites[key] = sp;
                    loaded++;
                }
                else
                {
                    Debug.LogWarning($"[IconLibrary] 图标加载失败：{key}");
                }
            }
            Debug.Log($"[IconLibrary] 图标预载完成：{loaded}/{PreloadKeys.Length}");
            _preloading = false;
        }

        public static bool TryGet(string key, out Sprite sprite)
        {
            sprite = null;
            return !string.IsNullOrEmpty(key) && _sprites.TryGetValue(key, out sprite) && sprite != null;
        }
    }
}
