using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 字体 Addressables 管理：
    /// 把 TMP 字体资产（含子资产材质/图集）注册进 Addressables，保证 prefab 在包内能解析字体引用
    /// （Addressables bundle 内 prefab 引用未入组的资产会悬空 → 玩家端字体/材质丢失）。
    /// </summary>
    public static class FontAddressableTools
    {
        const string GroupName = "Fonts";
        const string FontAssetPath = "Assets/Fonts/TMP/AlibabaFangYuan_SDF_48.asset";

        [MenuItem("工具/字体进 Addressables")]
        public static void RegisterFont()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[FontAddressableTools] Addressables 配置未初始化");
                return;
            }

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

            var guid = AssetDatabase.AssetPathToGUID(FontAssetPath);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogError($"[FontAddressableTools] 未找到字体资产: {FontAssetPath}");
                return;
            }

            var entry = settings.CreateOrMoveEntry(guid, group, false);
            entry.address = "AlibabaFangYuan_SDF_48";
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entry, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[FontAddressableTools] 字体已注册：{FontAssetPath}（组 {GroupName}，address=AlibabaFangYuan_SDF_48）");
        }
    }
}
