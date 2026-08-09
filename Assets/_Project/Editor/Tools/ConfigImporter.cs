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
    /// 关卡/事件/遗物配置导入器：读取 Assets/Data/Configs/*.json → 生成 SO 资产。
    /// 菜单：工具 → 导入关卡配置（JSON）
    /// 资产落位：Assets/Settings/Configs/（能力 → 按指纹去重复用）
    /// 导入顺序：遗物/能力 → 事件（池/定义）→ 关卡 → 地图（引用前面资产）
    /// </summary>
    public static class ConfigImporter
    {
        private const string ConfigsJsonDir = "Assets/Data/Configs";
        private const string ConfigAssetsDir = "Assets/Settings/Configs";

        [MenuItem("工具/导入关卡配置（JSON）")]
        public static void ImportAll()
        {
            EnsureFolder(ConfigAssetsDir);

            ImportRelics();
            ImportEvents();
            ImportFloor();
            ImportMap();

            AssetDatabase.SaveAssets();
            Debug.Log("[配置导入器] 完成：遗物/事件/关卡/地图");
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
                DeleteIfExists(assetPath);
                var relic = ScriptableObject.CreateInstance<RelicDef>();
                relic.name = $"Relic_{r.relicName}";
                relic.displayName = r.displayName;
                relic.description = r.description;
                relic.abilities = abilities;
                AssetDatabase.CreateAsset(relic, assetPath);
                SetId(relic, StableHash(relic.name));
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
                    DeleteIfExists(assetPath);
                    var def = ScriptableObject.CreateInstance<EventDefinition>();
                    def.name = $"Event_{e.eventId}";
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
                                relicName = fx.relicName,
                            });
                        }
                        def.options.Add(option);
                    }
                    AssetDatabase.CreateAsset(def, assetPath);
                    SetId(def, StableHash(def.name));
                }
            }
            // 事件池
            if (dto.pools != null)
            {
                foreach (var p in dto.pools)
                {
                    string assetPath = $"{ConfigAssetsDir}/Pool_{p.poolName}.asset";
                    DeleteIfExists(assetPath);
                    var pool = ScriptableObject.CreateInstance<EventPool>();
                    pool.name = $"Pool_{p.poolName}";
                    pool.entries = new List<EventPoolEntry>();
                    foreach (var entry in p.entries ?? new List<EntryJson>())
                    {
                        pool.entries.Add(new EventPoolEntry { eventId = $"Event_{entry.eventId}", weight = entry.weight });
                    }
                    AssetDatabase.CreateAsset(pool, assetPath);
                    SetId(pool, StableHash(pool.name));
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
            DeleteIfExists(assetPath);
            var floor = ScriptableObject.CreateInstance<FloorConfig>();
            floor.name = $"Floor_{dto.floorName}";
            floor.victoryRule = ParseEnum(dto.victoryRule, VictoryRule.WipeOut);
            floor.targetScore = dto.targetScore;
            floor.enemyMaxAP = dto.enemyMaxAP;
            floor.eventSequence = dto.eventSequence ?? new List<string>();
            floor.eventPoolIds = dto.eventPoolIds ?? new List<string>();
            floor.waveDefs = new List<WaveDef>();
            foreach (var w in dto.waves ?? new List<WaveJson>())
            {
                floor.waveDefs.Add(new WaveDef
                {
                    startTurn = w.startTurn,
                    pieceDefIds = w.pieceDefIds ?? new List<int>(),
                    isLastWave = w.isLastWave,
                    endCountdown = w.endCountdown,
                });
            }
            AssetDatabase.CreateAsset(floor, assetPath);
            SetId(floor, StableHash(floor.name));
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
            DeleteIfExists(assetPath);
            var map = ScriptableObject.CreateInstance<MapConfig>();
            map.name = $"Map_{dto.mapName}";
            map.floors = new List<FloorConfig>();
            foreach (var floorName in dto.floors ?? new List<string>())
            {
                var floor = AssetDatabase.LoadAssetAtPath<FloorConfig>($"{ConfigAssetsDir}/Floor_{floorName}.asset");
                if (floor != null)
                {
                    map.floors.Add(floor);
                }
            }
            AssetDatabase.CreateAsset(map, assetPath);
            SetId(map, StableHash(map.name));
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

        private static void DeleteIfExists(string assetPath)
        {
            var existing = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (existing != null)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        private static void SetId(ScriptableObject asset, int id)
        {
            var so = new SerializedObject(asset);
            so.FindProperty("_id").intValue = id;
            so.ApplyModifiedProperties();
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

        private static T ParseEnum<T>(string s, T fallback) where T : struct
        {
            return System.Enum.TryParse<T>(s, out var v) ? v : fallback;
        }

        // ========== DTO ==========

        private class MapJson { public string mapName; public string displayName; public string description; public List<string> floors; }
        private class FloorJson { public string floorName; public string displayName; public string description; public string victoryRule; public int targetScore; public int enemyMaxAP; public List<string> eventSequence; public List<string> eventPoolIds; public List<WaveJson> waves; }
        private class WaveJson { public int startTurn; public List<int> pieceDefIds; public bool isLastWave; public int endCountdown; }
        private class RelicsJson { public List<RelicJson> relics; }
        private class RelicJson { public string relicName; public string displayName; public string description; public List<AbilityJson> abilities; }
        private class EventsJson { public List<PoolJson> pools; public List<EventJson> events; }
        private class PoolJson { public string poolName; public List<EntryJson> entries; }
        private class EntryJson { public string eventId; public float weight; }
        private class EventJson { public string eventId; public string title; public string description; public List<OptionJson> options; }
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
