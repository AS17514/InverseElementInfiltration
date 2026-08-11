using System.Collections.Generic;
using TheLaw.Core;
using UnityEngine;

namespace TheLaw.Data
{
    /// <summary>事件定义（标题/描述 + 选项列表）——SO 类单独成文件（Unity 一文件一 SO 类）。</summary>
    public class EventDefinition : GameConfigBase
    {
        public string title;             // UI 标题（JSON title 导入；空 = 资产名兜底）
        [TextArea] public string description; // UI 剧情文案（JSON description 导入；空 = 标题兜底）
        public List<EventOption> options = new List<EventOption>();
    }
}
