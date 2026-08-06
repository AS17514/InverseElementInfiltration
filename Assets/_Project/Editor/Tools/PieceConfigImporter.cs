using System.Collections.Generic;
using System.IO;
using TheLaw.Data;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 棋子配置导入器：读取 Assets/Data/Pieces/*.json（配置器导出）→ 生成 PieceDef SO 资产。
    /// 菜单：工具 → 导入棋子配置（JSON）
    /// 资产落位：棋子 → Assets/Settings/Pieces/；特殊能力 → Assets/Settings/Abilities/（按能力指纹去重复用）
    /// 命名：资产名 = assetName（英文）；displayName = pieceName（中文显示）；Id = 稳定哈希（assetName，重复导入不变）
    /// </summary>
    public static class PieceConfigImporter
    {
        private const string PiecesJsonDir = "Assets/Data/Pieces";
        private const string PieceAssetsDir = "Assets/Settings/Pieces";
        private const string AbilityAssetsDir = "Assets/Settings/Abilities";

        [MenuItem("工具/导入棋子配置（JSON）")]
        public static void ImportAll()
        {
            EnsureFolder(PieceAssetsDir);
            EnsureFolder(AbilityAssetsDir);

            ClearAssetsIn(PieceAssetsDir); // 清空旧棋子资产（重建——防残留/改名遗留）

            var jsonFiles = Directory.GetFiles(PiecesJsonDir, "*.json");
            int ok = 0;
            foreach (var file in jsonFiles)
            {
                try
                {
                    if (ImportOne(file))
                    {
                        ok++;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[导入器] 失败：{Path.GetFileName(file)} → {e.Message}");
                }
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[导入器] 完成：{ok}/{jsonFiles.Length} 个棋子导入成功");
        }

        private static bool ImportOne(string jsonPath)
        {
            var dto = JsonConvert.DeserializeObject<PieceJson>(File.ReadAllText(jsonPath));
            if (dto == null || string.IsNullOrEmpty(dto.pieceName))
            {
                Debug.LogError($"[导入器] 文件格式错误：{jsonPath}");
                return false;
            }
            string assetName = string.IsNullOrEmpty(dto.assetName) ? dto.pieceName : dto.assetName; // 无英文名回退中文

            // 特殊能力（按指纹去重：多棋子可引用同一能力资产）
            var abilities = new List<SpecialAbilityDef>();
            if (dto.abilities != null)
            {
                foreach (var a in dto.abilities)
                {
                    var ability = GetOrCreateAbility(a);
                    if (ability != null)
                    {
                        abilities.Add(ability);
                    }
                }
            }

            // 棋子资产（目录已清空——直接创建）
            string assetPath = $"{PieceAssetsDir}/{assetName}.asset";
            var piece = ScriptableObject.CreateInstance<PieceDef>();
            piece.name = assetName;
            piece.displayName = dto.pieceName; // 中文显示名
            piece.pieceType = ParseEnum(dto.pieceType, PieceType.Initial);
            piece.value = dto.value;
            piece.durability = dto.durability;
            piece.footprint = ParseEnum(dto.footprint, Footprint.Size1x1);
            piece.specialAbilities = abilities;

            // 程序（默认模组 = programSet[0]）
            if (dto.modules != null && dto.modules.Count > 0)
            {
                var slots = new List<Template>();
                foreach (var m in dto.modules)
                {
                    var template = ParseModule(m);
                    if (template != null)
                    {
                        slots.Add(template);
                    }
                }
                piece.programSet.Add(new ProgramDef(slots));
            }

            AssetDatabase.CreateAsset(piece, assetPath);
            SetId(piece, StableHash(assetName)); // 稳定 Id（按资产名哈希——重复导入不变）
            Debug.Log($"[导入器] 导入：{dto.pieceName}（{assetName}，模块 {(piece.programSet.Count > 0 ? piece.programSet[0].slots.Count : 0)} 个，能力 {abilities.Count} 个，Id={StableHash(assetName)}）");
            return true;
        }

        // ========== 模块解析 ==========

        private static Template ParseModule(ModuleJson m)
        {
            switch (m.moduleType)
            {
                case "Move":
                    return ParseMove(m);
                case "Melee":
                case "MeleeAOE":
                case "DirectFire":
                    return ParseDirectionalAttack(m);
                case "Arcing":
                case "Spell":
                    return ParsePointAttack(m);
                default:
                    Debug.LogWarning($"[导入器] 未知模块类型：{m.moduleType}");
                    return null;
            }
        }

        private static Template ParseMove(ModuleJson m)
        {
            var template = new MoveTemplate();
            if (m.paths == null)
            {
                return template;
            }
            foreach (var path in m.paths)
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
            return template;
        }

        private static Template ParseDirectionalAttack(ModuleJson m)
        {
            return new AttackTemplate(
                ParseEnum(m.moduleType, AttackMode.Melee),
                ParseDirections(m.directions),
                m.range,
                m.damage,
                m.friendlyFire);
        }

        private static Template ParsePointAttack(ModuleJson m)
        {
            var template = new AttackTemplate
            {
                mode = ParseEnum(m.moduleType, AttackMode.Arcing),
                damage = m.damage,
                friendlyFire = m.friendlyFire,
            };
            if (m.points != null)
            {
                foreach (var p in m.points)
                {
                    template.points.Add(new Vector2Int(p.dx, p.dy));
                }
            }
            return template;
        }

        // ========== 特殊能力 ==========

        private static SpecialAbilityDef GetOrCreateAbility(AbilityJson a)
        {
            string name = AbilityFingerprint(a);
            string path = $"{AbilityAssetsDir}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<SpecialAbilityDef>(path);
            if (existing != null)
            {
                return existing; // 复用（多棋子引用同一能力；Id 已分配）
            }

            var ability = ScriptableObject.CreateInstance<SpecialAbilityDef>();
            ability.name = name;
            ability.type = ParseEnum(a.type, SpecialAbilityType.Trigger);
            if (ability.type == SpecialAbilityType.Trigger)
            {
                ability.triggerPoint = ParseEnum(a.triggerPoint, TriggerPoint.OnKill);
                ability.triggerEffect = ParseEnum(a.effect, TriggerEffect.ExtraAction);
                ability.amount = a.amount;
            }
            else if (ability.type == SpecialAbilityType.Attach)
            {
                ability.attachPoint = ParseEnum(a.attachPoint, AttachPoint.OnAttack);
                ability.attachShape = ParseEnum(a.attachShape, AttackShape.Cross);
                ability.attachDamage = a.attachDamage;
            }
            AssetDatabase.CreateAsset(ability, path);
            SetId(ability, StableHash(name)); // 能力稳定 Id（按指纹名哈希）
            return ability;
        }

        private static string AbilityFingerprint(AbilityJson a)
        {
            // 按能力参数生成去重名（同参数共享同一资产）
            if (a.type == "Attach")
            {
                return $"Ability_Attach_{a.attachPoint ?? "OnAttack"}_{a.attachShape ?? "Cross"}";
            }
            return $"Ability_{a.effect ?? "Effect"}_{a.triggerPoint ?? "Trigger"}_{a.amount}";
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

        /// <summary>清空目录内全部资产（重建用——防残留/改名遗留）。</summary>
        private static void ClearAssetsIn(string dir)
        {
            if (!AssetDatabase.IsValidFolder(dir))
            {
                return;
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Object", new[] { dir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                AssetDatabase.DeleteAsset(path);
            }
        }

        /// <summary>设置 GameConfigBase._id（private 序列化字段——SerializedObject 反射设置）。</summary>
        private static void SetId(ScriptableObject asset, int id)
        {
            var so = new SerializedObject(asset);
            so.FindProperty("_id").intValue = id;
            so.ApplyModifiedProperties();
        }

        /// <summary>稳定哈希（FNV-1a 32 位转正——同一名字永远同一 Id，重复导入不变）。</summary>
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

        private static T ParseEnum<T>(string s, T fallback) where T : struct
        {
            return System.Enum.TryParse<T>(s, out var v) ? v : fallback;
        }

        // ========== DTO（与配置器导出 JSON 对齐，camelCase）==========

        private class PieceJson
        {
            public string pieceName;   // 中文显示名
            public string assetName;   // 英文资产名（可选；缺省回退 pieceName）
            public string pieceType;
            public int value;
            public int durability;
            public string footprint;
            public List<ModuleJson> modules;
            public List<AbilityJson> abilities; // 可选：特殊能力（配置器未导出时手工补充）
        }

        private class ModuleJson
        {
            public string moduleType;
            public List<PathJson> paths;              // Move
            public List<string> directions;           // 方向集攻击
            public int range;
            public int damage;
            public bool friendlyFire;
            public List<PointJson> points;            // 抛射/法术自由点选
        }

        private class PathJson
        {
            public List<SegmentJson> segments;
        }

        private class SegmentJson
        {
            public List<MoveJson> moves;
        }

        private class MoveJson
        {
            public string direction;
            public List<int> steps;
        }

        private class PointJson
        {
            public int dx;
            public int dy;
        }

        private class AbilityJson
        {
            public string type;          // Trigger / Attach
            public string triggerPoint;  // OnKill / OnDamaged / ...
            public string effect;        // ExtraAction / ShieldBlock / ...
            public int amount;
            public string attachPoint;   // OnAttack
            public string attachShape;   // Cross
            public int attachDamage;
        }
    }
}
