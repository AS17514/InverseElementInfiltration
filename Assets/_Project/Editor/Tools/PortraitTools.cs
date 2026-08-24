using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 立绘 Addressables 管理（2026-08-24 前端，主角团素材整理）：
    /// - 扫描 Assets/Art/Characters/Protagonist 下全部立绘（测试员/棋手3号/骇客Xeon），注册进 Art 组
    /// - 地址 = 文件名（去扩展名）——与 StoryPanel 差分加载约定一致（"Xeon_常态" 等）
    /// 新立绘放好后跑一次「工具/立绘进 Addressables」；增量幂等（已有条目复用/移动）。
    /// </summary>
    public static class PortraitTools
    {
        const string ProtagonistDir = "Assets/Art/Characters/Protagonist";
        const string GroupName = "Art";

        [MenuItem("工具/立绘进 Addressables")]
        public static void RefreshPortraitAddressables()
        {
            var settings = EnsureSettings();
            var group = EnsureGroup(settings);

            if (!AssetDatabase.IsValidFolder(ProtagonistDir))
            {
                Debug.LogWarning($"[PortraitTools] 目录不存在: {ProtagonistDir}——先放入主角团立绘");
                return;
            }

            int added = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Sprite", new[] { ProtagonistDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = settings.CreateOrMoveEntry(guid, group, readOnly: false);
                entry.address = Path.GetFileNameWithoutExtension(path); // 地址 = 文件名（Tester_Default / Xeon_常态 / ChessPlayer3_01_Default ...）
                added++;
            }
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PortraitTools] 主角团立绘注册完成：{added} 个（组 {GroupName}，地址 = 文件名）");
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
