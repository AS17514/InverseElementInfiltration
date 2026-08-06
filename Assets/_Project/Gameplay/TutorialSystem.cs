using System.Collections.Generic;
using TheLaw.Core;
using Newtonsoft.Json;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 教程系统：主动触发（BattleFlow/TowerFlow 调用 TryShow），已触发记录入快照。
    /// 教程内容/剧情待基础玩法完成后设计（当前骨架）。
    /// 实例由 Bootstrap 创建并显式传递（规则层行为类——避免单例全局耦合）。
    /// </summary>
    public class TutorialSystem : ISnapshot
    {
        private readonly HashSet<string> _triggered = new HashSet<string>();

        public string Key => "TutorialSystem";

        /// <summary>尝试触发教程（未触发过返回 true 并标记；已触发返回 false）。</summary>
        public bool TryShow(string tutorialId)
        {
            if (_triggered.Contains(tutorialId))
            {
                return false;
            }
            _triggered.Add(tutorialId);
            // TODO: 发教程展示事件（UI 层监听）
            return true;
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
