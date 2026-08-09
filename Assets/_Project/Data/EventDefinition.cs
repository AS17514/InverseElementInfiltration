using System.Collections.Generic;
using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>事件定义（选项列表）——SO 类单独成文件（Unity 一文件一 SO 类）。</summary>
    public class EventDefinition : GameConfigBase
    {
        public List<EventOption> options = new List<EventOption>();
    }
}
