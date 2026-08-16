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

        // ====== 牌组构筑限制（DeckBuild 事件用；0 = 无限制）======
        public int deckSizeLimit;        // 牌组牌数上限（0 = 不限制）
        public int totalValueLimit;      // 牌组总价值上限（0 = 不限制）

        // ====== 构筑规则开关（2026-08-15 策划新案——事件级配置；默认 false = 旧行为兼容）======
        public bool allowDuplicate;          // 允许同种棋子复数编入（false = 去重——旧行为）
        public bool promoteLimitByInitial;   // 升变棋子数量 ≤ 初始棋子数量（按当前价值档位计；false = 不限制——旧行为）
    }
}
