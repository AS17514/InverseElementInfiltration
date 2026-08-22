using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 音频 Addressables 管理：
    /// - 扫描 Assets/Audio/BGM|SFX 下所有音频资源，自动注册 addressable（address = BGM/{名} / SFX/{名}——与 AudioRefs 常量一致）
    /// 音频资源放好后跑一次「工具/音频进 Addressables」即可；后续新增音频后重跑一次（增量：已有条目复用/移动）。
    /// </summary>
    public static class AudioTools
    {
        const string AudioDir = "Assets/Audio";
        const string GroupName = "Audio";
        const string BgmPrefix = "Assets/Audio/BGM";

        [MenuItem("工具/音频进 Addressables")]
        public static void RefreshAudioAddressables()
        {
            var settings = EnsureSettings();
            var group = EnsureGroup(settings);

            if (!AssetDatabase.IsValidFolder(AudioDir))
            {
                Debug.LogWarning($"[AudioTools] 目录不存在: {AudioDir}——先创建 Assets/Audio/BGM、Assets/Audio/SFX");
                return;
            }

            int added = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { AudioDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = settings.CreateOrMoveEntry(guid, group, readOnly: false);
                string prefix = path.StartsWith(BgmPrefix) ? "BGM" : "SFX";
                // ⚠️ 2026-08-23 修复：地址 = 分类大写/资源名（需求单/常量口径，如 "SFX/deploy"）——
                // 文件名带 sfx_/bgm_ 前缀（交付命名规范），地址必须去前缀（"sfx_deploy" → "deploy"），否则 LoadAssetAsync("SFX/deploy") 找不到
                string file = Path.GetFileNameWithoutExtension(path); // sfx_deploy / bgm_menu
                string name = file.StartsWith(prefix.ToLowerInvariant() + "_")
                    ? file.Substring(prefix.Length + 1)
                    : file;
                entry.address = $"{prefix}/{name}"; // 地址规则：分类大写/去前缀资源名
                added++;
            }
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AudioTools] 音频注册完成：{added} 个（组 {GroupName}，地址规则 BGM|SFX/文件名——与 AudioRefs 常量一致）");
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