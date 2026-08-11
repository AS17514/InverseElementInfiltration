using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 字体补全：把 Creator 生成的单页资产（~450 字）动态化 + 补齐 GB2312 一级子集（3756 字）。
    /// Dynamic 模式下 TryAddCharacters 自动扩展多页 atlas；未收录字符运行时按需渲染。
    /// </summary>
    public static class FontAssetCompleter
    {
        const string FontAssetPath = "Assets/Fonts/TMP/ALIMAMAFANGYUANTIVF-THIN SDF.asset";
    const string FontPath = "Assets/Fonts/ALIMAMAFANGYUANTIVF-THIN.TTF";
    const string DynamicPath = "Assets/Fonts/TMP/AlibabaFangYuan_Dynamic.asset";

        [MenuItem("Tools/Font/Complete Font Asset (Dynamic+Subset)")]
        public static void Complete()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (asset == null)
            {
                Debug.LogError($"[Font] 未找到资产: {FontAssetPath}");
                return;
            }

            // ① Static → Dynamic + 多页：必须走公开属性 setter（会同步 m_SourceFontFile，直接改字段会导致 LoadFontFace(null) 静默失败）
            asset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            asset.isMultiAtlasTexturesEnabled = true;
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Font] 模式检查：mode={asset.atlasPopulationMode} multi={asset.isMultiAtlasTexturesEnabled} sourceFont={asset.sourceFontFile != null}");

            int before = asset.characterTable.Count;
            // ② 补全子集（多页自动扩展）
            // 先触发 atlasTexture 懒加载——TMP 3.0.7 多页扩页直接读 m_AtlasTexture 缓存字段，不初始化会崩
            var _ = asset.atlasTexture;
            bool ok = asset.TryAddCharacters(BuildSubset());
            int after = asset.characterTable.Count;

            // ③ 新 atlas 页存为 sub-asset（TMP 自己也会 Add；已属于资产的跳过；数组可能有 null 槽位）
            foreach (var atlas in asset.atlasTextures)
            {
                if (atlas == null) continue;
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(atlas)))
                {
                    AssetDatabase.AddObjectToAsset(atlas, asset);
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Font] 完成：{before} → {after} 字符 / {asset.atlasTextures.Length} 页 Atlas / 模式 Dynamic");
        }

        /// <summary>高清晰度重建：72pt + 4096 atlas + padding 12（更清晰——适合大字号 UI）。新建资产不动旧资产（引用不断）。</summary>
        [MenuItem("Tools/Font/Rebuild Font (72pt HighRes)")]
        public static void Rebuild72()
        {
            const string newPath = "Assets/Fonts/TMP/AlibabaFangYuan_SDF_72.asset";
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (font == null)
            {
                Debug.LogError($"[Font] 未找到字体: {FontPath}");
                return;
            }
            var asset = TMP_FontAsset.CreateFontAsset(font, 72, 12, GlyphRenderMode.SDFAA, 4096, 4096, AtlasPopulationMode.Dynamic, true);
            asset.name = "AlibabaFangYuan_SDF_72";
            var _ = asset.atlasTexture; // m_AtlasTexture 懒加载 workaround
            bool ok = asset.TryAddCharacters(BuildSubset());

            AssetDatabase.CreateAsset(asset, newPath);
            AssetDatabase.AddObjectToAsset(asset.material, asset);
            foreach (var atlas in asset.atlasTextures)
            {
                if (atlas != null) AssetDatabase.AddObjectToAsset(atlas, asset);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Font] 72pt 重建完成：{asset.characterTable.Count} 字符 / {asset.atlasTextures.Length} 页 / 体积 {new System.IO.FileInfo(newPath).Length / 1024 / 1024}MB");
        }

        /// <summary>把 TMP Settings 默认字体切到 72pt 高清资产（全局 UI 生效；显式引用 48 资产的 3D 文本不受影响）。</summary>
        [MenuItem("Tools/Font/Set Default Font (72pt HighRes)")]
        public static void SetDefault72()
        {
            const string newPath = "Assets/Fonts/TMP/AlibabaFangYuan_SDF_72.asset";
            var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(newPath);
            if (asset == null)
            {
                Debug.LogError($"[Font] 未找到资产: {newPath}（先跑 Rebuild Font (72pt HighRes)）");
                return;
            }
            var settingsAsset = AssetDatabase.LoadMainAssetAtPath("Assets/TextMesh Pro/Resources/TMP Settings.asset");
            if (settingsAsset == null)
            {
                Debug.LogError("[Font] 未找到 TMP Settings.asset");
                return;
            }
            var so = new SerializedObject(settingsAsset);
            so.FindProperty("m_defaultFontAsset").objectReferenceValue = asset;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(settingsAsset);
            AssetDatabase.SaveAssets();
            Debug.Log("[Font] 默认字体已切到 AlibabaFangYuan_SDF_72");
        }

        /// <summary>重建：字号 48 动态子集（替代 90pt 的 81.5MB 版本，体积 ~12MB）。</summary>
        [MenuItem("Tools/Font/Rebuild Font (48pt Dynamic Subset)")]
        public static void Rebuild48()
        {
            const string newPath = "Assets/Fonts/TMP/AlibabaFangYuan_SDF_48.asset";
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (font == null)
            {
                Debug.LogError($"[Font] 未找到字体: {FontPath}");
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(newPath) != null)
            {
                AssetDatabase.DeleteAsset(newPath);
            }

            var asset = TMP_FontAsset.CreateFontAsset(font, 48, 9, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
            asset.name = "AlibabaFangYuan_SDF_48";
            var _ = asset.atlasTexture; // m_AtlasTexture 懒加载 workaround（TMP 3.0.7 多页 bug）
            bool ok = asset.TryAddCharacters(BuildSubset());

            AssetDatabase.CreateAsset(asset, newPath);
            AssetDatabase.AddObjectToAsset(asset.material, asset);
            foreach (var atlas in asset.atlasTextures)
            {
                if (atlas != null) AssetDatabase.AddObjectToAsset(atlas, asset);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // TMP Settings 默认字体切到新资产，再删旧资产
            var settingsAsset = AssetDatabase.LoadMainAssetAtPath("Assets/TextMesh Pro/Resources/TMP Settings.asset");
            var so = new SerializedObject(settingsAsset);
            so.FindProperty("m_defaultFontAsset").objectReferenceValue = asset;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(settingsAsset);
            AssetDatabase.SaveAssets();

            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath) != null)
            {
                AssetDatabase.DeleteAsset(FontAssetPath);
            }
            AssetDatabase.Refresh();

            Debug.Log($"[Font] 重建完成：{asset.characterTable.Count} 字符 / {asset.atlasTextures.Length} 页 / 体积 {new System.IO.FileInfo(newPath).Length / 1024 / 1024}MB");
        }

        /// <summary>创建动态兜底资产（不预填，运行时按需渲染）+ 挂到 TMP Settings fallback 链。</summary>
        [MenuItem("Tools/Font/Create Dynamic Fallback")]
        public static void CreateDynamicFallback()
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (font == null)
            {
                Debug.LogError($"[Font] 未找到字体: {FontPath}");
                return;
            }

            // 若已存在则删除重建（避免 fileID 冲突）
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(DynamicPath) != null)
            {
                AssetDatabase.DeleteAsset(DynamicPath);
            }

            var asset = TMP_FontAsset.CreateFontAsset(font, 60, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
            asset.name = "AlibabaFangYuan_Dynamic";
            AssetDatabase.CreateAsset(asset, DynamicPath);
            AssetDatabase.AddObjectToAsset(asset.material, asset);
            foreach (var atlas in asset.atlasTextures)
            {
                AssetDatabase.AddObjectToAsset(atlas, asset);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // TMP Settings：默认 = 子集主资产；fallback = 动态资产
            var settingsAsset = AssetDatabase.LoadMainAssetAtPath("Assets/TextMesh Pro/Resources/TMP Settings.asset");
            var so = new SerializedObject(settingsAsset);
            so.FindProperty("m_defaultFontAsset").objectReferenceValue =
                AssetDatabase.LoadMainAssetAtPath(FontAssetPath);
            var fb = so.FindProperty("m_fallbackFontAssets");
            fb.ClearArray();
            fb.InsertArrayElementAtIndex(0);
            fb.GetArrayElementAtIndex(0).objectReferenceValue = asset;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(settingsAsset);
            AssetDatabase.SaveAssets();

            Debug.Log("[Font] 动态兜底资产已创建并挂接 fallback");
        }

        /// <summary>ASCII 可打印 + 中文标点 + GB2312 一级汉字（3755 个）。</summary>
        public static string BuildSubset()
        {
            var sb = new StringBuilder();
            for (int c = 0x20; c <= 0x7E; c++) sb.Append((char)c);
            sb.Append("，。、；：！？…—·《》「」『』“”‘’（）【】％￥");
            var enc = Encoding.GetEncoding("gb2312");
            for (int hi = 0xB0; hi <= 0xD7; hi++)
            {
                int loMax = hi == 0xD7 ? 0xF9 : 0xFE;
                for (int lo = 0xA1; lo <= loMax; lo++)
                {
                    sb.Append(enc.GetString(new byte[] { (byte)hi, (byte)lo }));
                }
            }
            return sb.ToString();
        }
    }
}
