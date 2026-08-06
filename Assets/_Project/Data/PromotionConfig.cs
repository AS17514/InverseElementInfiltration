using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>
    /// 升变映射（SO 资产）：fromDefId → toDefId 一对一定义。
    /// 升变零门槛：有映射 + 手牌有升变牌 + 1 AP 即可（无位置要求）。
    /// </summary>
    public class PromotionConfig : GameConfigBase
    {
        public int fromDefId; // 原棋子 Def id
        public int toDefId;   // 升变版 Def id
    }
}
