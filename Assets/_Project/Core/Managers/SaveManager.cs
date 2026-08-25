using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 存档管理器：收集 ISnapshot → 打包 JSON → 写文件；读 → 分发。
    /// 两阶段注册：构造期注册（Bootstrap），存档时收集。不做版本号校验。
    /// </summary>
    public class SaveManager : BaseManager<SaveManager>
    {
        private readonly Dictionary<string, ISnapshot> _snapshots = new Dictionary<string, ISnapshot>();

        private const int MaxHistoryCount = 5; // 历史存档保留上限（2026-08-13：整局结束归档保留 N 份，超出删除最旧——排查可回溯）
        private const string LegacySettingsKey = "SettingsSystem"; // 旧存档里的设置字段，加载时剥离
        private const string BattleStartFileName = "save_battle.json"; // 战斗开始快照（SL 槽——2026-08-24 临时方案：回战斗开始重开用）

        private string SavePath => Path.Combine(Application.persistentDataPath, "save.json");
        private string BattleStartPath => Path.Combine(Application.persistentDataPath, BattleStartFileName);

        /// <summary>注册快照（构造期由 Bootstrap 调用；重复注册覆盖）。</summary>
        public void RegisterSnapshot(ISnapshot snapshot)
        {
            _snapshots[snapshot.Key] = snapshot;
        }

        /// <summary>
        /// 保存全部快照（key → json 打包为一个文件）。
        /// ⚠️ 2026-08-13 原子写：先写临时文件再替换——防"写到一半进程被杀"留下半个 JSON 损坏存档。
        /// </summary>
        public void SaveAll()
        {
            WriteBundle(CollectBundle());
        }

        /// <summary>原子写主存档（先写临时文件再替换——防"写到一半进程被杀"留下半个 JSON 损坏存档）。</summary>
        private void WriteBundle(Dictionary<string, string> bundle)
        {
            WriteBundleTo(SavePath, bundle);
        }

        /// <summary>原子写指定路径（主档/战斗开始快照共用——先写临时文件再替换）。</summary>
        private void WriteBundleTo(string path, Dictionary<string, string> bundle)
        {
            string tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, JsonConvert.SerializeObject(bundle));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tmpPath, path);
        }

        /// <summary>
        /// 保存"战斗开始快照"（SL 槽——2026-08-24 临时方案）：GameState + RandomManager 两快照独立槽位。
        /// ⚠️ 必须在**波次随机之前**保存（BattleFlow.StartBattle 内 SetupDrawPile 后、HandleWaveAndPromotions 前）——
        /// 这样 Continue 战斗档加载后 StartBattle 重开，波次随机与首次完全一致（种子系统：RNG 回开战前）。
        /// </summary>
        public void SaveBattleStart()
        {
            var bundle = new Dictionary<string, string>();
            if (_snapshots.TryGetValue("GameState", out var gs)) bundle[gs.Key] = gs.ToJson();
            if (_snapshots.TryGetValue("RandomManager", out var rm)) bundle[rm.Key] = rm.ToJson();
            WriteBundleTo(BattleStartPath, bundle);
        }

        /// <summary>加载战斗开始快照（状态 + RNG 回开战前；无 SL 槽返回 false——调用方回退主档）。</summary>
        public bool LoadBattleStart()
        {
            if (!File.Exists(BattleStartPath))
            {
                return false;
            }
            try
            {
                var bundle = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(BattleStartPath));
                if (bundle == null)
                {
                    return false;
                }
                foreach (var pair in bundle)
                {
                    if (_snapshots.TryGetValue(pair.Key, out var snapshot))
                    {
                        snapshot.FromJson(pair.Value);
                    }
                }
                return true;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[SaveManager] 战斗开始快照读取失败：{e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 整局结束归档（2026-08-13）：当前局完整状态（含回放）存为历史存档——排查可回溯。
        /// ⚠️ 主档随后由收尾链清空（ResetForNewRun + SaveAll）——历史档保留最近 N 份，超出删除最旧。
        /// 触发点：Bootstrap.FinalizeRun（ResetForNewRun 之前调用——必须存局终完整状态）。
        /// </summary>
        public void ArchiveHistory()
        {
            try
            {
                string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = Path.Combine(Application.persistentDataPath, $"save_history_{stamp}.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(CollectBundle()));
                CleanupHistory();
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[SaveManager] 历史归档失败：{e.Message}");
            }
        }

        /// <summary>收集全部快照（key → json——SaveAll/ArchiveHistory 共用）。</summary>
        private Dictionary<string, string> CollectBundle()
        {
            var bundle = new Dictionary<string, string>();
            foreach (var pair in _snapshots)
            {
                bundle[pair.Key] = pair.Value.ToJson();
            }
            return bundle;
        }

        /// <summary>清理历史存档：数量超过上限 → 删除最旧（文件名 yyyyMMdd_HHmmss 字典序 = 时间序）。</summary>
        private void CleanupHistory()
        {
            var files = System.IO.Directory.GetFiles(Application.persistentDataPath, "save_history_*.json")
                .OrderBy(f => f).ToList();
            while (files.Count > MaxHistoryCount)
            {
                File.Delete(files[0]);
                files.RemoveAt(0);
            }
        }

        /// <summary>
        /// 读取全部快照并分发（文件不存在则跳过）。
        /// ⚠️ 2026-08-13 健壮性：损坏存档（半个 JSON/解析异常）→ 跳过不崩（LogError），后续快照不恢复——
        /// 宁可丢存档也不崩游戏（原实现异常上抛中断 LoadAll）。
        /// </summary>
        public void LoadAll()
        {
            if (!File.Exists(SavePath))
            {
                return;
            }
            try
            {
                var bundle = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(SavePath));
                if (bundle == null)
                {
                    UnityEngine.Debug.LogError("[SaveManager] 存档为空或格式错误——跳过读档");
                    return;
                }
                if (bundle.ContainsKey(LegacySettingsKey))
                {
                    bundle.Remove(LegacySettingsKey);
                    WriteBundle(bundle); // 设置已独立到 settings.json，旧存档不再保留该字段
                    UnityEngine.Debug.Log("[SaveManager] 已从旧存档剥离 SettingsSystem 设置字段");
                }
                foreach (var pair in bundle)
                {
                    if (_snapshots.TryGetValue(pair.Key, out var snapshot))
                    {
                        snapshot.FromJson(pair.Value);
                    }
                }
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[SaveManager] 读档失败（存档损坏？）：{e.Message}");
            }
        }

        /// <summary>是否存在存档（主菜单"继续"按钮用）。</summary>
        public bool HasSave => File.Exists(SavePath);

        // ========== 独立文本文件槽位（2026-08-25：教程记录 tutorial.json——仿 settings.json 先例：设备级数据不随主档生命周期）==========

        private const string TutorialFileName = "tutorial.json"; // 教程记录独立文件（不在主档——清档/失败清档不影响"是否看过教程"）

        /// <summary>教程记录独立槽位原子写（2026-08-25：仿 settings.json——立即保存；随 TutorialSystem 变更触发）。</summary>
        public void SaveTutorialRecords(string json)
        {
            WriteBundleTo(Path.Combine(Application.persistentDataPath, TutorialFileName),
                new Dictionary<string, string> { [TutorialKey] = json });
        }

        /// <summary>教程记录独立槽位读取（无文件返回 null——调用方按空处理：从未记录、教程可全部重播）。</summary>
        public string LoadTutorialRecords()
        {
            string path = Path.Combine(Application.persistentDataPath, TutorialFileName);
            if (!File.Exists(path)) return null;
            try
            {
                var bundle = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                if (bundle != null && bundle.TryGetValue(TutorialKey, out var json)) return json;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[SaveManager] 教程记录读取失败：{e.Message}");
            }
            return null;
        }

        private const string TutorialKey = "TutorialSystem"; // tutorial.json 内的键（序列化格式与旧主档段兼容——可无缝迁移）
    }
}
