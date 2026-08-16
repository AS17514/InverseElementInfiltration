using System.Collections.Generic;
using System.IO;
using TheLaw.Data;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 棋子配置导入器：读取 Assets/Data/Pieces/*.json（配置器导出）→ 生成/更新 PieceDef SO 资产。
    /// 菜单：工具 → 导入棋子配置（JSON）
    /// 资产落位：棋子 → Assets/Settings/Pieces/；特殊能力 → Assets/Settings/Abilities/（按能力指纹去重复用）
    /// 命名：资产名 = assetName（英文）；displayName = pieceName（中文显示）；Id = 稳定哈希（assetName，重复导入不变）
    /// ⚠️ 增量模式（2026-08-09）：资产已存在 → 更新字段不删建（GUID 不变，场景引用不断）；
    ///    不存在 → 新建。旧"清空重建"模式已废弃（删旧建新会让场景 Bootstrap 引用全部断掉）。
    /// 程序块编号：JSON modules 的 id 字段（种类内编号，同结构可复用同 id——描述表按"种类+编号"查描述）。
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

            var jsonFiles = Directory.GetFiles(PiecesJsonDir, "*.json");
            int ok = 0;
            foreach (var file in jsonFiles)
            {
                // slot-descriptions.json 是描述表（文案数据），不是棋子配置——跳过（防误解析报格式错误）
                if (Path.GetFileName(file) == "slot-descriptions.json")
                {
                    continue;
                }
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
            Debug.Log($"[导入器] 完成：{ok}/{jsonFiles.Length} 个棋子导入成功（增量模式——GUID 不变）");
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

            // 棋子资产（增量模式：已存在 → 更新字段不删建——GUID 不变，场景引用不断；不存在 → 新建）
            string assetPath = $"{PieceAssetsDir}/{assetName}.asset";
            var piece = AssetDatabase.LoadAssetAtPath<PieceDef>(assetPath);
            bool created = piece == null;
            if (created)
            {
                piece = ScriptableObject.CreateInstance<PieceDef>();
                AssetDatabase.CreateAsset(piece, assetPath);
            }
            piece.name = assetName;
            piece.displayName = dto.pieceName; // 中文显示名
            piece.pieceType = ParseEnum(dto.pieceType, PieceType.Initial);
            piece.value = dto.value;
            piece.durability = dto.durability;
            piece.footprint = ParseEnum(dto.footprint, Footprint.Size1x1);
            piece.specialAbilities = abilities;

            // 程序（默认模组 = programSet[0]；增量更新先清空防叠加）
            piece.programSet.Clear();
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

            if (piece.Id == 0)
            {
                SetId(piece, StableHash(assetName)); // 稳定 Id（按资产名哈希——幂等：已有 Id 不重设）
            }
            EditorUtility.SetDirty(piece); // 增量更新必须标脏（新建资产 CreateAsset 已标）
            Debug.Log($"[导入器] {(created ? "新建" : "更新")}：{dto.pieceName}（{assetName}，模块 {(piece.programSet.Count > 0 ? piece.programSet[0].slots.Count : 0)} 个，能力 {abilities.Count} 个，Id={piece.Id}）");
            return true;
        }

        // ========== 模块解析 ==========

        private static Template ParseModule(ModuleJson m)
        {
            Template template;
            switch (m.moduleType)
            {
                case "Move":
                    template = ParseMove(m);
                    break;
                case "Melee":
                case "MeleeAOE":
                case "DirectFire":
                    template = ParseDirectionalAttack(m);
                    break;
                case "Arcing":
                case "Spell":
                    template = ParsePointAttack(m);
                    break;
                default:
                    Debug.LogWarning($"[导入器] 未知模块类型：{m.moduleType}");
                    return null;
            }
            if (template != null)
            {
                template.id = m.id; // 程序块编号（种类内编号，同结构可复用——描述表按此查）
            }
            return template;
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
            // 跳跃落点（2026-08-16：棋子 JSON 移动模块 jumpOffsets——与模板库同构）
            if (m.jumpOffsets != null)
            {
                foreach (var p in m.jumpOffsets)
                {
                    template.jumpOffsets.Add(new Vector2Int(p.dx, p.dy));
                }
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
                // 复用（多棋子引用同一能力）——早期导入产物可能没有 Id → 补设
                var so = new SerializedObject(existing);
                if (so.FindProperty("_id").intValue == 0)
                {
                    so.Dispose();
                    SetId(existing, StableHash(name));
                    Debug.Log($"[导入器] 能力补设 Id：{name} → {StableHash(name)}");
                }
                else
                {
                    so.Dispose();
                }
                return existing;
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
            else if (ability.type == SpecialAbilityType.Passive)
            {
                // ⚠️ 2026-08-13：补 Passive 分支（原只有 Trigger/Attach——Passive JSON 会被误解析成 Trigger；
                // 解析逻辑与关卡导入器 ConfigImporter 的 Passive 实现一致）
                ability.passiveTarget = ParseEnum(a.passiveTarget, PassiveTarget.AttackRange);
                ability.passiveValue = a.passiveValue;
                ability.applyBeforeResolve = a.applyBeforeResolve;
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
                // ⚠️ 2026-08-13：补 attachDamage 区分——原指纹不含伤害，不同 attachDamage 的附着能力错误共享资产
                return $"Ability_Attach_{a.attachPoint ?? "OnAttack"}_{a.attachShape ?? "Cross"}_{a.attachDamage}";
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
            public int id;                            // 程序块编号（种类内编号，同结构可复用；0=未编号回退代码生成）
            public List<PathJson> paths;              // Move
            public List<PointJson> jumpOffsets;       // Move 跳跃落点（2026-08-16）
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
            public string type;          // Trigger / Attach / Passive
            public string triggerPoint;  // OnKill / OnDamaged / ...
            public string effect;        // ExtraAction / ShieldBlock / ...
            public int amount;
            public string attachPoint;   // OnAttack
            public string attachShape;   // Cross
            public int attachDamage;
            public string passiveTarget; // Passive（2026-08-13 补：MoveStep/AttackDamage/AttackRange/Durability）
            public int passiveValue;
            public bool applyBeforeResolve = true;
        }
    }
}
