using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 图标资产 Addressables 管理（2026-08-26 前端，程序块/围棋/麻将图标接入）：
    /// - 扫描指定目录 PNG，统一 Sprite 导入并注册 addressable（address = 文件名，如 move_step / GoPiece / mahjong_1）
    /// 新图标放好后跑一次「工具/图标资产进 Addressables」即可；增量幂等（已有条目复用/移动）。
    /// </summary>
    public static class IconArtTools
    {
        static readonly string[] IconDirs =
        {
            "Assets/Art/ProgramBlock",
            "Assets/Art/Characters/Special",
            "Assets/Art/BaseInfoBlock",
        };
        const string GroupName = "Art";

        [MenuItem("工具/图标资产进 Addressables")]
        public static void RefreshIconAddressables()
        {
            var settings = EnsureSettings();
            var group = EnsureGroup(settings);

            int added = 0;
            foreach (var dir in IconDirs)
            {
                if (!AssetDatabase.IsValidFolder(dir)) continue;

                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { dir }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path == null || !path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)) continue;

                    // 统一 Sprite 导入（地址按 Sprite 加载——LoadAssetAsync<Sprite> 需要 Sprite 类型）
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer != null && importer.textureType != TextureImporterType.Sprite)
                    {
                        importer.textureType = TextureImporterType.Sprite;
                        importer.SaveAndReimport();
                    }

                    var entry = settings.CreateOrMoveEntry(guid, group, readOnly: false);
                    entry.address = Path.GetFileNameWithoutExtension(path); // 地址 = 文件名（move_step / GoPiece / mahjong_1 ...）
                    added++;
                }
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[IconArtTools] 图标注册完成：{added} 个（组 {GroupName}，地址 = 文件名）");
        }

        static AddressableAssetSettings EnsureSettings()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                settings = AddressableAssetSettings.Create("Assets/AddressableAssetsData", "AddressableAssetSettings", true, true);
                AddressableAssetSettingsDefaultObject.Settings = settings;
            }
            return settings;
        }

        static AddressableAssetGroup EnsureGroup(AddressableAssetSettings settings)
        {
            var group = settings.FindGroup(GroupName);
            if (group == null)
            {
                group = settings.CreateGroup(GroupName, false, false, true, null);
            }
            return group;
        }
    }
}
