using System;
using System.Collections.Generic;

namespace TheLaw.Data
{
    // ========== 事件关（事件池加权抽取，事件级无条件、池级有条件）==========
    // 注：EventPool / EventDefinition 为 SO 类，单独成文件（EventPool.cs / EventDefinition.cs）

    /// <summary>事件池条目（池级条件）。</summary>
    [Serializable]
    public class EventPoolEntry
    {
        public string eventId;      // 指向 EventDefinition（资产名）
        public float weight;        // 抽取权重
        public string conditionId;  // 池级条件（可空；空 = 无条件）
    }

    /// <summary>事件选项（availability：UI 灰显 + 规则层二次校验）。</summary>
    [Serializable]
    public class EventOption
    {
        public string optionId;
        public string label;                    // UI 文案
        public bool available = true;           // 选项可用性
        public List<EffectDefinition> effects = new List<EffectDefinition>();
    }

    /// <summary>效果类型（事件效果——经 Resolver 落账，禁止绕过结算器）。</summary>
    public enum EffectType
    {
        AddPiece,        // 增加新棋子（从预定义 Def 池选择——运行时不合成）
        ModifyDurability,// 改承伤（±amount）
        EditProgram,     // 改棋子程序（编辑事件——打开编辑器，UI 交互）
        GrantAbility,    // 给予临时特殊能力
        GrantRelic,      // 获得遗物（relicName 指向 RelicDef 资产名）
        DeckBuild,       // 打开牌组构筑（UI 交互）
    }

    /// <summary>效果定义（targetScope + targetRule？空 = 玩家手动选目标）。</summary>
    [Serializable]
    public class EffectDefinition
    {
        public EffectType effectType;
        public TargetScope targetScope;        // PieceCollection（选棋子）/ Board（选格）
        public TargetRule? targetRule;         // 空 = 玩家手动选
        public int targetDefId;                // AddPiece 用：Def id
        public int amount;                     // 数值参数（承伤/价值等）
        public int abilityId;                  // GrantAbility 用：特殊能力 id
        public string relicName;               // GrantRelic 用：遗物资产名（RelicDef.name）
    }
}
