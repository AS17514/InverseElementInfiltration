using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data; // GameEvent——2026-08-25 缺 using 编译错误（CS0103·自查清单前科重犯，教训：新代码头部 using 必查）
using Newtonsoft.Json;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 教程系统：主动触发（BattleFlow/TowerFlow 调用 TryShow），已触发记录**独立持久化**。
    /// ⚠️ 2026-08-25 持久化迁移：原随主档（save.json 的 ISnapshot 段）→ **独立 tutorial.json**
    /// （仿 settings.json 先例——"是否看过教程"是设备级状态，清档/失败清档不应让它回到未看状态）：
    /// 变更立即保存（TryShow/ClearAll 触发 SaveTutorialRecords）、启动时 LoadTutorials。
    /// 序列化格式与旧主档段兼容（同 ToJson/FromJson——迁移无感）。
    /// 实例由 Bootstrap 创建并显式传递（规则层行为类——避免单例全局耦合）。
    /// </summary>
    public class TutorialSystem
    {
        private readonly HashSet<string> _triggered = new HashSet<string>();

        /// <summary>
        /// 清空全部教程记录（2026-08-25 接口开放——重看教程/测试用：清空后所有教程可重新触发）。
        /// 持久化：清空后**立即保存**到独立 tutorial.json（仿设置改动即存）。
        /// </summary>
        public void ClearAll()
        {
            _triggered.Clear();
            SaveTutorialRecords();
        }

        /// <summary>尝试触发教程（未触发过返回 true 并标记；已触发返回 false——跨局持久去重，独立 tutorial.json）。
        /// ⚠️ 2026-08-25 契约落地：审核通过 → 发 TutorialRequested 事件（前端 TutorialManager 监听播放）；
        /// **即刻标记并保存**——跳过/播放中断也计"展示过"（防每局重复骚扰）。</summary>
        public bool TryShow(string tutorialId)
        {
            if (!Tutorials.Enabled) return false; // 2026-08-25 总开关：关闭 → 不触发（不标记/不保存/不发事件——记录零污染）
            if (string.IsNullOrEmpty(tutorialId))
            {
                return false;
            }
            if (_triggered.Contains(tutorialId))
            {
                return false;
            }
            _triggered.Add(tutorialId);
            SaveTutorialRecords(); // 立即持久化（设备级——不随主档 30s 定时）
            EventCenter.Instance.EventTrigger(GameEvent.TutorialRequested, tutorialId); // 2026-08-25：前端监听播放（原 TODO 落地）
            return true;
        }

        // ========== 独立持久化（2026-08-25：tutorial.json——文件 IO 归 Core SaveManager，本类只定内容）==========

        /// <summary>启动加载（Bootstrap Awake 调用——仿 SettingsSystem.LoadSettings；无文件 = 从未记录，教程可全播）。</summary>
        public void LoadTutorials()
        {
            if (!Tutorials.Enabled) return; // 2026-08-25 总开关：关闭 → 不读盘（记录保持现状）
            string json = SaveManager.Instance.LoadTutorialRecords();
            if (json != null)
            {
                FromJson(json);
            }
        }

        private void SaveTutorialRecords()
        {
            SaveManager.Instance.SaveTutorialRecords(ToJson());
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(new List<string>(_triggered));
        }

        public void FromJson(string json)
        {
            _triggered.Clear();
            var list = JsonConvert.DeserializeObject<List<string>>(json);
            if (list != null)
            {
                foreach (var id in list)
                {
                    _triggered.Add(id);
                }
            }
        }
    }
}