using System.Collections.Generic;
using System.IO;
using TheLaw.Core;
using TheLaw.Data;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 关卡/事件/遗物/模板配置导入器：读取 Assets/Data/Configs/*.json + Assets/Data/Templates/*.json → 生成 SO 资产。
    /// 菜单：工具 → 导入关卡配置（JSON）
    /// 资产落位：Assets/Settings/Configs/（能力 → 按指纹去重复用；模板 → TemplateDef）
    /// 导入顺序：遗物/能力 → 事件（池/定义）→ 关卡 → 地图 → 模板库
    /// </summary>
    public static class ConfigImporter
    {
        private const string ConfigsJsonDir = "Assets/Data/Configs";
        private const string ConfigAssetsDir = "Assets/Settings/Configs";
        private const string PieceAssetsDir = "Assets/Settings/Pieces"; // 波次阵容按棋子资产名 → defId 转换
        private const string TemplatesJsonDir = "Assets/Data/Templates";

        [MenuItem("工具/导入关卡配置（JSON）")]
        public static void ImportAll()
        {
            EnsureFolder(ConfigAssetsDir);

            ImportRelics();
            ImportEvents();
            ImportFloor();
            ImportMap();
            ImportTemplates();

            AssetDatabase.SaveAssets();
            Debug.Log("[配置导入器] 完成：遗物/事件/关卡/地图/模板库");
        }

        /// <summary>
        /// 一次性工具：把模板资产（Tpl_*.asset）批量填入 Bootstrap 预制体的 _templateConfigs（免手动拖 20 个）。
        /// 直接修改预制体资产（Prefabs/Bootstrap.prefab）——团队共享生效（场景实例是预制体实例，改资产才落库）。
        /// 不引用 UI 程序集——按组件类型名匹配 Bootstrap；PrefabUtility 修改预制体内容。
        /// ⚠️ 不自动保存场景（防把未保存的误操作一起存进去）——场景实例若需同步请手动处理。
        /// 未来新增模板后重跑一次即可。
        /// </summary>
        [MenuItem("工具/收集模板资产到 Bootstrap")]
        public static void CollectTemplatesToBootstrap()
        {
            var templateAssets = new List<TemplateDef>();
            foreach (var guid in AssetDatabase.FindAssets("t:TemplateDef", new[] { ConfigAssetsDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<TemplateDef>(path);
                if (asset != null)
                {
                    templateAssets.Add(asset);
                }
            }
            Debug.Log($"[配置导入器] 找到模板资产 {templateAssets.Count} 个");
            if (templateAssets.Count == 0)
            {
                Debug.LogWarning("[配置导入器] 未找到模板资产——先运行'导入关卡配置（JSON）'生成 Tpl_*.asset");
                return;
            }

            // 找 Bootstrap 预制体资产（Prefabs/Bootstrap.prefab——按名字搜，不硬编码路径）
            string prefabPath = null;
            foreach (var guid in AssetDatabase.FindAssets("Bootstrap t:Prefab"))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.EndsWith("Bootstrap.prefab"))
                {
                    prefabPath = p;
                    break;
                }
            }
            if (string.IsNullOrEmpty(prefabPath))
            {
                Debug.LogError("[配置导入器] 未找到 Bootstrap.prefab——检查 Prefabs 目录");
                return;
            }
            Debug.Log($"[配置导入器] 找到预制体：{prefabPath}");

            // 修改预制体资产内容（不碰场景实例——实例 override 与资产分离）
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            MonoBehaviour bootstrap = null;
            foreach (var mb in contents.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb.GetType().Name == "Bootstrap")
                {
                    bootstrap = mb;
                    break;
                }
            }
            if (bootstrap == null)
            {
                PrefabUtility.UnloadPrefabContents(contents);
                Debug.LogError("[配置导入器] Bootstrap.prefab 内未找到 Bootstrap 组件");
                return;
            }

            var so = new SerializedObject(bootstrap);
            var prop = so.FindProperty("_templateConfigs");
            if (prop == null)
            {
                PrefabUtility.UnloadPrefabContents(contents);
                Debug.LogError("[配置导入器] Bootstrap 组件上未找到 _templateConfigs 字段——检查脚本是否已编译最新版");
                return;
            }
            prop.ClearArray();
            prop.arraySize = templateAssets.Count;
            for (int i = 0; i < templateAssets.Count; i++)
            {
                prop.GetArrayElementAtIndex(i).objectReferenceValue = templateAssets[i];
            }
            so.ApplyModifiedProperties();
            PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            PrefabUtility.UnloadPrefabContents(contents);
            Debug.Log($"[配置导入器] 已填充 {templateAssets.Count} 个模板资产到 {prefabPath}（预制体已保存——场景实例如需同步请手动重置/重新实例化）");
        }

        // ========== 遗物 ==========

        private static void ImportRelics()
        {
            string path = $"{ConfigsJsonDir}/relics.json";
            if (!File.Exists(path))
            {
                return;
            }
            var dto = JsonConvert.DeserializeObject<RelicsJson>(File.ReadAllText(path));
            if (dto?.relics == null)
            {
                return;
            }
            foreach (var r in dto.relics)
            {
                var abilities = new List<SpecialAbilityDef>();
                if (r.abilities != null)
                {
                    foreach (var a in r.abilities)
                    {
                        var ability = GetOrCreateAbility(a);
                        if (ability != null)
                        {
                            abilities.Add(ability);
                        }
                    }
                }
                string assetPath = $"{ConfigAssetsDir}/Relic_{r.relicName}.asset";
                var relic = LoadOrCreate<RelicDef>(assetPath, $"Relic_{r.relicName}"); // 增量：已存在更新不删建（GUID 不变）
                relic.displayName = r.displayName;
                relic.description = r.description;
                relic.abilities = abilities;
                if (relic.Id == 0)
                {
                    SetId(relic, StableHash(relic.name));
                }
                EditorUtility.SetDirty(relic);
            }
        }

        // ========== 事件 ==========

        private static void ImportEvents()
        {
            string path = $"{ConfigsJsonDir}/events.json";
            if (!File.Exists(path))
            {
                return;
            }
            var dto = JsonConvert.DeserializeObject<EventsJson>(File.ReadAllText(path));
            if (dto == null)
            {
                return;
            }
            // 事件定义（先建——池条目按 name 引用）
            if (dto.events != null)
            {
                foreach (var e in dto.events)
                {
                    string assetPath = $"{ConfigAssetsDir}/Event_{e.eventId}.asset";
                    var def = LoadOrCreate<EventDefinition>(assetPath, $"Event_{e.eventId}"); // 增量：已存在更新不删建
                    def.title = e.title;
                    def.description = e.description;
                    def.deckSizeLimit = e.deckSizeLimit;
                    def.totalValueLimit = e.totalValueLimit;
                    def.allowDuplicate = e.allowDuplicate;            // 构筑新规则开关（2026-08-15：可复数）
                    def.promoteLimitByInitial = e.promoteLimitByInitial; // 构筑新规则开关（2026-08-15：升变≤初始）
                    def.options = new List<EventOption>();
                    foreach (var o in e.options ?? new List<OptionJson>())
                    {
                        var option = new EventOption { optionId = o.optionId, label = o.label, available = true, effects = new List<EffectDefinition>() };
                        foreach (var fx in o.effects ?? new List<EffectJson>())
                        {
                            option.effects.Add(new EffectDefinition
                            {
                                effectType = ParseEnum(fx.effectType, EffectType.AddPiece),
                                targetDefId = fx.targetDefId,
                                amount = fx.amount,
                                abilityId = fx.abilityId,
                                // 遗物引用：JSON 裸名 → 资产名（幂等补 Relic_ 前缀——与池/事件同一契约）
                                relicName = NormalizeAssetName(fx.relicName, "Relic_"),
                            });
                        }
                        def.options.Add(option);
                    }
                    if (def.Id == 0)
                    {
                        SetId(def, StableHash(def.name));
                    }
                    EditorUtility.SetDirty(def);
                }
            }
            // 事件池
            if (dto.pools != null)
            {
                foreach (var p in dto.pools)
                {
                    string assetPath = $"{ConfigAssetsDir}/Pool_{p.poolName}.asset";
                    var pool = LoadOrCreate<EventPool>(assetPath, $"Pool_{p.poolName}"); // 增量：已存在更新不删建
                    pool.entries = new List<EventPoolEntry>();
                    foreach (var entry in p.entries ?? new List<EntryJson>())
                    {
                        pool.entries.Add(new EventPoolEntry { eventId = $"Event_{entry.eventId}", weight = entry.weight });
                    }
                    if (pool.Id == 0)
                    {
                        SetId(pool, StableHash(pool.name));
                    }
                    EditorUtility.SetDirty(pool);
                }
            }
        }

        // ========== 关卡 ==========

        private static void ImportFloor()
        {
            string path = $"{ConfigsJsonDir}/floor1.json";
            if (!File.Exists(path))
            {
                return;
            }
            var dto = JsonConvert.DeserializeObject<FloorJson>(File.ReadAllText(path));
            if (dto == null)
            {
                return;
            }
            string assetPath = $"{ConfigAssetsDir}/Floor_{dto.floorName}.asset";
            var floor = LoadOrCreate<FloorConfig>(assetPath, $"Floor_{dto.floorName}"); // 增量：已存在更新不删建
            floor.victoryRule = ParseEnum(dto.victoryRule, VictoryRule.WipeOut);
            floor.targetScore = dto.targetScore;
            floor.scoreDeductEnabled = dto.scoreDeductEnabled; // 敌方击杀扣分开关（2026-08-20）
            floor.enemyMaxAP = dto.enemyMaxAP;
            floor.eventSequence = dto.eventSequence ?? new List<string>();
            // 事件池引用：JSON 裸名 → 资产名（幂等补 Pool_ 前缀——与池条目 eventId 补 Event_ 前缀同一契约）
            floor.eventPoolIds = new List<string>();
            foreach (var id in dto.eventPoolIds ?? new List<string>())
            {
                floor.eventPoolIds.Add(id.StartsWith("Pool_") ? id : $"Pool_{id}");
            }
            floor.waveDefs = new List<WaveDef>();
            foreach (var w in dto.waves ?? new List<WaveJson>())
            {
                var wave = new WaveDef
                {
                    startTurn = w.startTurn,
                    pieceDefIds = ResolvePieceIds(w.pieceDefIds), // 资产名 → defId（int）
                    isLastWave = w.isLastWave,
                    endCountdown = w.endCountdown,
                    promotions = ParsePromotions(w.promotions), // 波次升变预告（2026-08-12：DTO 补字段——此前只能手动配资产，与"JSON 是权威源"公约冲突）
                    autoPromote = w.autoPromote, // 自动预告模式（2026-08-19）
                    waveScoreTarget = w.waveScoreTarget, // 每波达标线（2026-08-19 计分规则；0=未配置）
                };
                // 随机池（2026-08-19：Initial/Deployable——从该类棋子随机抽 count 个，可重复）
                if (!string.IsNullOrEmpty(w.pool))
                {
                    if (w.pool == "Initial")
                    {
                        wave.randomPool = true;
                        wave.poolType = PieceType.Initial;
                        wave.count = w.count;
                    }
                    else if (w.pool == "Deployable")
                    {
                        wave.randomPool = true;
                        wave.poolType = PieceType.Deployable;
                        wave.count = w.count;
                    }
                    else
                    {
                        Debug.LogWarning($"[配置导入器] 未知随机池类型：{w.pool}（忽略——按固定阵容）");
                    }
                }
                // 固定站位（Unity 坐标 [x=列, y=行]；空=自动找位）
                if (w.positions != null)
                {
                    foreach (var p in w.positions)
                    {
                        wave.positions.Add(new Vector2Int(p.dx, p.dy));
                    }
                }
                floor.waveDefs.Add(wave);
            }
            if (floor.Id == 0)
            {
                SetId(floor, StableHash(floor.name));
            }
            EditorUtility.SetDirty(floor);
        }

        // ========== 地图 ==========

        private static void ImportMap()
        {
            string path = $"{ConfigsJsonDir}/map.json";
            if (!File.Exists(path))
            {
                return;
            }
            var dto = JsonConvert.DeserializeObject<MapJson>(File.ReadAllText(path));
            if (dto == null)
            {
                return;
            }
            string assetPath = $"{ConfigAssetsDir}/Map_{dto.mapName}.asset";
            var map = LoadOrCreate<MapConfig>(assetPath, $"Map_{dto.mapName}"); // 增量：已存在更新不删建
            map.floors = new List<FloorConfig>();
            foreach (var floorName in dto.floors ?? new List<string>())
            {
                var floor = AssetDatabase.LoadAssetAtPath<FloorConfig>($"{ConfigAssetsDir}/Floor_{floorName}.asset");
                if (floor != null)
                {
                    map.floors.Add(floor);
                }
            }
            if (map.Id == 0)
            {
                SetId(map, StableHash(map.name));
            }
            EditorUtility.SetDirty(map);
        }

        // ========== 模板库（独立程序块定义——编辑界面候选池）==========

        private static void ImportTemplates()
        {
            string path = $"{TemplatesJsonDir}/templates.json";
            if (!File.Exists(path))
            {
                return;
            }
            var dto = JsonConvert.DeserializeObject<TemplatesJson>(File.ReadAllText(path));
            if (dto?.templates == null)
            {
                return;
            }
            int ok = 0;
            foreach (var t in dto.templates)
            {
                // ⚠️ 2026-08-13：key 统一"种类前缀+编号"（与描述表/编号体系同构）——原用具体类型前缀（"Melee-1"）
                // 与描述表 "Attack-1" 不一致（攻击类统一 Attack 前缀、移动类 Move 前缀）
                string key = t.type == "Move" ? $"Move-{t.id}" : t.type == "Effect" ? $"Effect-{t.id}" : $"Attack-{t.id}";
                string assetPath = $"{ConfigAssetsDir}/Tpl_{t.type}_{t.id}.asset";
                var def = LoadOrCreate<TemplateDef>(assetPath, $"Tpl_{t.type}_{t.id}"); // 增量：已存在更新不删建
                def.templateKey = key;
                def.template = ParseTemplate(t);
                if (def.Id == 0)
                {
                    SetId(def, StableHash(def.name));
                }
                EditorUtility.SetDirty(def);
                ok++;
            }
            Debug.Log($"[配置导入器] 模板库：{ok} 条（{TemplatesJsonDir}/templates.json）");
        }

        private static Template ParseTemplate(TemplateJson t)
        {
            switch (t.type)
            {
                case "Move":
                    return ParseMoveTemplate(t);
                case "Melee":
                case "MeleeAOE":
                case "DirectFire":
                    // 方案 B（2026-08-16）：方向→射程集合（每方向独立射程）——非空优先
                    if (t.rangeSteps != null && t.rangeSteps.Count > 0)
                    {
                        var atkSteps = new AttackTemplate
                        {
                            mode = ParseEnum(t.type, AttackMode.Melee),
                            damage = t.damage,
                            friendlyFire = t.friendlyFire,
                            id = t.id,
                        };
                        foreach (var rs in t.rangeSteps)
                        {
                            atkSteps.rangeSteps.Add(new AttackRangeStep
                            {
                                direction = ParseDirection(rs.direction),
                                ranges = rs.ranges != null ? new List<int>(rs.ranges) : new List<int>(),
                            });
                        }
                        return atkSteps;
                    }
                    return new AttackTemplate(
                        ParseEnum(t.type, AttackMode.Melee),
                        ParseDirections(t.directions),
                        t.range,
                        t.damage,
                        t.friendlyFire)
                    { id = t.id };
                case "Arcing":
                case "Spell":
                    var atk = new AttackTemplate
                    {
                        mode = ParseEnum(t.type, AttackMode.Arcing),
                        damage = t.damage,
                        friendlyFire = t.friendlyFire,
                        id = t.id,
                    };
                    if (t.points != null)
                    {
                        foreach (var p in t.points)
                        {
                            atk.points.Add(new Vector2Int(p.dx, p.dy));
                        }
                    }
                    return atk;
                case "Effect":
                    // 效果槽（2026-08-19 数据层）：引用特殊能力资产（abilityKey = 资产名——运行时 ConfigTable 查）
                    return new EffectTemplate { id = t.id, abilityKey = t.ability };
                default:
                    Debug.LogWarning($"[配置导入器] 未知模板类型：{t.type}");
                    return null;
            }
        }

        private static Template ParseMoveTemplate(TemplateJson t)
        {
            var template = new MoveTemplate { id = t.id };
            if (t.paths == null)
            {
                return template;
            }
            foreach (var path in t.paths)
            {
                var movePath = new MovePath();
                if (path.segments != null)
                {
                    foreach (var seg in path.segments)
                    {
                        var segment = new MoveSegment();
                        if (seg.moves != null)
                        {
                            foreach (var mv in seg.moves)
                            {
                                var step = new MoveStep { direction = ParseDirection(mv.direction) };
                                if (mv.steps != null)
                                {
                                    step.steps = new List<int>(mv.steps);
                                }
                                segment.moves.Add(step);
                            }
                        }
                        movePath.segments.Add(segment);
                    }
                }
                template.paths.Add(movePath);
            }
            // 跳跃落点（2026-08-16：配置器导出的 Move 模块 jumpOffsets）
            if (t.jumpOffsets != null)
            {
                foreach (var p in t.jumpOffsets)
                {
                    template.jumpOffsets.Add(new Vector2Int(p.dx, p.dy));
                }
            }
            return template;
        }

        // ========== 特殊能力（支持 Passive/Trigger/Attach）==========

        private static SpecialAbilityDef GetOrCreateAbility(AbilityJson a)
        {
            string name = AbilityFingerprint(a);
            string path = $"{ConfigAssetsDir}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<SpecialAbilityDef>(path);
            if (existing != null)
            {
                var so = new SerializedObject(existing);
                if (so.FindProperty("_id").intValue == 0)
                {
                    so.Dispose();
                    SetId(existing, StableHash(name));
                }
                else
                {
                    so.Dispose();
                }
                return existing;
            }

            var ability = ScriptableObject.CreateInstance<SpecialAbilityDef>();
            ability.name = name;
            ability.type = ParseEnum(a.type, SpecialAbilityType.Passive);
            switch (ability.type)
            {
                case SpecialAbilityType.Passive:
                    ability.passiveTarget = ParseEnum(a.passiveTarget, PassiveTarget.AttackRange);
                    ability.passiveValue = a.passiveValue;
                    ability.applyBeforeResolve = a.applyBeforeResolve;
                    break;
                case SpecialAbilityType.Trigger:
                    ability.triggerPoint = ParseEnum(a.triggerPoint, TriggerPoint.OnKill);
                    ability.triggerEffect = ParseEnum(a.effect, TriggerEffect.ExtraAction);
                    ability.amount = a.amount;
                    break;
                case SpecialAbilityType.Attach:
                    ability.attachPoint = ParseEnum(a.attachPoint, AttachPoint.OnAttack);
                    ability.attachShape = ParseEnum(a.attachShape, AttackShape.Cross);
                    ability.attachDamage = a.attachDamage;
                    break;
            }
            AssetDatabase.CreateAsset(ability, path);
            SetId(ability, StableHash(name));
            return ability;
        }

        private static string AbilityFingerprint(AbilityJson a)
        {
            switch (a.type)
            {
                case "Passive":
                    return $"Ability_Passive_{a.passiveTarget}_{a.passiveValue}";
                case "Attach":
                    return $"Ability_Attach_{a.attachPoint ?? "OnAttack"}_{a.attachShape ?? "Cross"}";
                default:
                    return $"Ability_{a.effect ?? "Effect"}_{a.triggerPoint ?? "Trigger"}_{a.amount}";
            }
        }

        // ========== 工具 ==========

        private static void EnsureFolder(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                string parent = Path.GetDirectoryName(path).Replace('\\', '/');
                string folder = Path.GetFileName(path);
                AssetDatabase.CreateFolder(parent, folder);
            }
        }

        /// <summary>
        /// 加载资产；不存在则新建（增量模式——资产已存在更新不删建，GUID 不变，Bootstrap 引用不断）。
        /// 旧"删旧建新"模式每次导入都会让场景 Bootstrap 拖拽引用全部断掉，已废弃。
        /// </summary>
        private static T LoadOrCreate<T>(string assetPath, string name) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<T>();
                asset.name = name;
                AssetDatabase.CreateAsset(asset, assetPath);
            }
            return asset;
        }

        /// <summary>
        /// 幂等补资产名前缀（JSON 引用字段写裸名，导入器补前缀生成资产名——统一契约）：
        /// 裸名 "relic_move" → "Relic_relic_move"；已带前缀 "Relic_relic_move" → 原样（防重复导入前缀叠加）。
        /// </summary>
        private static string NormalizeAssetName(string name, string prefix)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }
            return name.StartsWith(prefix) ? name : $"{prefix}{name}";
        }

        private static void SetId(ScriptableObject asset, int id)
        {
            var so = new SerializedObject(asset);
            so.FindProperty("_id").intValue = id;
            so.ApplyModifiedProperties();
        }

        private static Direction ParseDirection(string s)
        {
            return System.Enum.TryParse<Direction>(s, out var d) ? d : Direction.Up;
        }

        private static Direction ParseDirections(List<string> dirs)
        {
            Direction result = Direction.None;
            if (dirs != null)
            {
                foreach (var s in dirs)
                {
                    result |= ParseDirection(s);
                }
            }
            return result == Direction.None ? Direction.Up : result;
        }

        private static int StableHash(string s)
        {
            uint hash = 2166136261;
            foreach (var c in s)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)(hash & 0x7FFFFFFF);
        }

        /// <summary>波次阵容：棋子资产名 → defId（加载 PieceDef 资产取 Id）。</summary>
        private static List<int> ResolvePieceIds(List<string> assetNames)
        {
            var ids = new List<int>();
            foreach (var name in assetNames ?? new List<string>())
            {
                ids.Add(ResolvePieceId(name));
            }
            return ids;
        }

        /// <summary>单个棋子资产名 → defId（找不到返回 0 并警告——升变预告等单值引用用）。</summary>
        private static int ResolvePieceId(string assetName)
        {
            var def = AssetDatabase.LoadAssetAtPath<PieceDef>($"{PieceAssetsDir}/{assetName}.asset");
            if (def != null)
            {
                return def.Id;
            }
            Debug.LogWarning($"[配置导入器] 找不到棋子资产 {assetName}（Assets/Settings/Pieces/）——该项跳过");
            return 0;
        }

        /// <summary>波次升变预告（JSON WavePromotionJson → WaveDef.promotions；toDefId 资产名 → defId）。</summary>
        private static List<WavePromotion> ParsePromotions(List<WavePromotionJson> promotions)
        {
            var list = new List<WavePromotion>();
            foreach (var p in promotions ?? new List<WavePromotionJson>())
            {
                list.Add(new WavePromotion { pieceIndexInWave = p.pieceIndexInWave, toDefId = ResolvePieceId(p.toDefId) });
            }
            return list;
        }

        private static T ParseEnum<T>(string s, T fallback) where T : struct
        {
            return System.Enum.TryParse<T>(s, out var v) ? v : fallback;
        }

        // ========== DTO ==========

        private class TemplatesJson { public List<TemplateJson> templates; }
        private class TemplateJson
        {
            public string type;                 // Move/Melee/MeleeAOE/DirectFire/Arcing/Spell/Effect
            public int id;                      // 种类内编号（与棋子内联模块/描述表 key 同构）
            public List<TplPathJson> paths;     // Move
            public List<PointJson> jumpOffsets; // Move 跳跃落点（2026-08-16）
            public List<AttackRangeStepJson> rangeSteps; // 攻击：方向→射程集合（方案 B，2026-08-16）
            public List<string> directions;     // 方向集攻击
            public int range;
            public int damage;
            public bool friendlyFire;
            public List<PointJson> points;      // 抛射/法术自由点选
            public string ability;              // Effect：特殊能力资产名（abilityKey）
        }
        private class AttackRangeStepJson { public string direction; public List<int> ranges; }
        private class TplPathJson { public List<TplSegmentJson> segments; }
        private class TplSegmentJson { public List<TplMoveJson> moves; }
        private class TplMoveJson { public string direction; public List<int> steps; }
        private class PointJson { public int dx; public int dy; }

        private class MapJson { public string mapName; public string displayName; public string description; public List<string> floors; }
        private class FloorJson { public string floorName; public string displayName; public string description; public string victoryRule; public int targetScore; public bool scoreDeductEnabled; public int enemyMaxAP; public List<string> eventSequence; public List<string> eventPoolIds; public List<WaveJson> waves; }
        private class WaveJson { public int startTurn; public List<string> pieceDefIds; public string pool; public int count; public List<PointJson> positions; public bool isLastWave; public int endCountdown; public List<WavePromotionJson> promotions; public bool autoPromote; public int waveScoreTarget; }
        private class WavePromotionJson { public int pieceIndexInWave; public string toDefId; }
        private class RelicsJson { public List<RelicJson> relics; }
        private class RelicJson { public string relicName; public string displayName; public string description; public List<AbilityJson> abilities; }
        private class EventsJson { public List<PoolJson> pools; public List<EventJson> events; }
        private class PoolJson { public string poolName; public List<EntryJson> entries; }
        private class EntryJson { public string eventId; public float weight; }
        private class EventJson { public string eventId; public string title; public string description; public int deckSizeLimit; public int totalValueLimit; public bool allowDuplicate; public bool promoteLimitByInitial; public List<OptionJson> options; }
        private class OptionJson { public string optionId; public string label; public List<EffectJson> effects; }
        private class EffectJson { public string effectType; public int targetDefId; public int amount; public int abilityId; public string relicName; }
        private class AbilityJson
        {
            public string type; public string passiveTarget; public int passiveValue; public bool applyBeforeResolve = true;
            public string triggerPoint; public string effect; public int amount;
            public string attachPoint; public string attachShape; public int attachDamage;
        }
    }
}
