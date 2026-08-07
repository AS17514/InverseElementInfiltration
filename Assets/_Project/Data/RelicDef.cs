using System.Collections.Generic;
using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>
    /// 遗物定义（SO 资产，局内获得，整局持续、可叠加）。
    /// 遗物 = 特殊能力底层工具的消费者（引用 SpecialAbilityDef 实现效果，不单独搞机制）。
    /// </summary>
    public class RelicDef : GameConfigBase
    {
        public string displayName;
        public string description;
        public List<SpecialAbilityDef> abilities = new List<SpecialAbilityDef>();
    }
}
