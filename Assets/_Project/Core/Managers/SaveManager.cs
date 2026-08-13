using System.Collections.Generic;
using System.IO;
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

        private string SavePath => Path.Combine(Application.persistentDataPath, "save.json");

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
            var bundle = new Dictionary<string, string>();
            foreach (var pair in _snapshots)
            {
                bundle[pair.Key] = pair.Value.ToJson();
            }
            string tmpPath = SavePath + ".tmp";
            File.WriteAllText(tmpPath, JsonConvert.SerializeObject(bundle));
            if (File.Exists(SavePath))
            {
                File.Delete(SavePath);
            }
            File.Move(tmpPath, SavePath);
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
    }
}
