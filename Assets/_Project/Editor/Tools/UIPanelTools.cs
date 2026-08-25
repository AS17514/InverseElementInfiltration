using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 面板 Prefab Addressables 管理：
    /// - 确保 Addressables 配置初始化（settings + UIPanels 组）
    /// - 扫描 Assets/_Project/UI/Prefabs/ 下所有 prefab，自动注册 addressable（address = 文件名）
    /// 拼完新面板跑一次 Tools/UI/Refresh Panel Addressables 即可。
    /// </summary>
    public static class UIPanelTools
    {
        const string PrefabDir = "Assets/_Project/UI/Prefabs";
        const string GroupName = "UIPanels";

        [MenuItem("Tools/UI/Refresh Panel Addressables")]
        public static void RefreshPanelAddressables()
        {
            var settings = EnsureSettings();
            var group = EnsureGroup(settings);

            if (!AssetDatabase.IsValidFolder(PrefabDir))
            {
                Debug.LogWarning($"[UIPanelTools] 目录不存在: {PrefabDir}，先创建它");
                return;
            }

            int added = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = settings.CreateOrMoveEntry(guid, group, readOnly: false);
                entry.address = Path.GetFileNameWithoutExtension(path); // address = 类名
                added++;
            }
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[UIPanelTools] 注册完成：{added} 个面板 prefab（组 {GroupName}）");
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
                group = settings.CreateGroup(GroupName, false, false, true, null,
                    new System.Type[] { typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema) });
            }
            else
            {
                if (!group.HasSchema<BundledAssetGroupSchema>()) group.AddSchema<BundledAssetGroupSchema>();
                if (!group.HasSchema<ContentUpdateGroupSchema>()) group.AddSchema<ContentUpdateGroupSchema>();
            }
            return group;        }
    }
}
