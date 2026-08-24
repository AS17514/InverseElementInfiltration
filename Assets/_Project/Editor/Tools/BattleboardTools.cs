using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 战斗棋盘美术 Addressables 管理：
    /// - 扫描 Assets/Art/Battleboard 下全部 PNG，统一设为 Sprite 导入并注册 addressable（address = 文件名，如 battle_wave_big）
    /// 美术放好后跑一次「工具/战斗美术进 Addressables」即可；后续新增后重跑（增量：已有条目复用/移动）。
    /// </summary>
    public static class BattleboardTools
    {
        const string BattleboardDir = "Assets/Art/Battleboard";
        const string GroupName = "Battleboard";

        [MenuItem("工具/战斗美术进 Addressables")]
        public static void RefreshBattleboardAddressables()
        {
            var settings = EnsureSettings();
            var group = EnsureGroup(settings);

            if (!AssetDatabase.IsValidFolder(BattleboardDir))
            {
                Debug.LogWarning($"[BattleboardTools] 目录不存在: {BattleboardDir}");
                return;
            }

            int added = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { BattleboardDir }))
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
                entry.address = Path.GetFileNameWithoutExtension(path); // 地址 = 文件名（battle_progress / battle_wave_big ...）
                added++;
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[BattleboardTools] 战斗美术注册完成：{added} 个（组 {GroupName}，地址 = 文件名）");
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
