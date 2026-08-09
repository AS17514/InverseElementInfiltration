using System.Collections.Generic;
using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>事件池（一等对象，可跨层复用）——SO 类单独成文件（Unity 一文件一 SO 类）。</summary>
    public class EventPool : GameConfigBase
    {
        public List<EventPoolEntry> entries = new List<EventPoolEntry>();
    }
}
