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

        /// <summary>保存全部快照（key → json 打包为一个文件）。</summary>
        public void SaveAll()
        {
            var bundle = new Dictionary<string, string>();
            foreach (var pair in _snapshots)
            {
                bundle[pair.Key] = pair.Value.ToJson();
            }
            File.WriteAllText(SavePath, JsonConvert.SerializeObject(bundle));
        }

        /// <summary>读取全部快照并分发（文件不存在则跳过）。</summary>
        public void LoadAll()
        {
            if (!File.Exists(SavePath))
            {
                return;
            }
            var bundle = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(SavePath));
            foreach (var pair in bundle)
            {
                if (_snapshots.TryGetValue(pair.Key, out var snapshot))
                {
                    snapshot.FromJson(pair.Value);
                }
            }
        }

        /// <summary>是否存在存档（主菜单"继续"按钮用）。</summary>
        public bool HasSave => File.Exists(SavePath);
    }
}
