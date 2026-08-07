using TheLaw.Core;
using Newtonsoft.Json;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 进度系统：剧情区（storyIndex）——策划案无成就系统（成就区已移除）。
    /// 剧情内容待基础玩法完成后设计（当前骨架）。
    /// 实例由 Bootstrap 创建并显式传递（规则层行为类——避免单例全局耦合）。
    /// </summary>
    public class ProgressSystem : ISnapshot
    {
        private int _storyIndex;

        public string Key => "ProgressSystem";

        public int StoryIndex => _storyIndex;

        /// <summary>推进剧情。</summary>
        public void AdvanceStory()
        {
            _storyIndex++;
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(_storyIndex);
        }

        public void FromJson(string json)
        {
            _storyIndex = JsonConvert.DeserializeObject<int>(json);
        }
    }
}
