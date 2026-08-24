using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 事件 CG Addressables 管理（2026-08-25 前端，事件面板 CG 切换）：
    /// - 扫描 Assets/Art/Event 下全部 PNG，统一 Sprite 导入并注册 addressable（address = 文件名，如 event_ability）
    /// 新 CG 放好后跑一次「工具/事件 CG 进 Addressables」即可；增量幂等（已有条目复用/移动）。
    /// </summary>
    public static class EventArtTools
    {
        const string EventArtDir = "Assets/Art/Event";
        const string GroupName = "Art";

        [MenuItem("工具/事件 CG 进 Addressables")]
        public static void RefreshEventArtAddressables()
        {
            var settings = EnsureSettings();
            var group = EnsureGroup(settings);

            if (!AssetDatabase.IsValidFolder(EventArtDir))
            {
                Debug.LogWarning($"[EventArtTools] 目录不存在: {EventArtDir}");
                return;
            }

            int added = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { EventArtDir }))
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
                entry.address = Path.GetFileNameWithoutExtension(path); // 地址 = 文件名（event_ability / event_edit / event_mode ...）
                added++;
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[EventArtTools] 事件 CG 注册完成：{added} 个（组 {GroupName}，地址 = 文件名）");
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
