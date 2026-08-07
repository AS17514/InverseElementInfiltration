using System.Collections.Generic;
using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>
    /// 爬塔地图配置（SO 资产）：单线节点序列生成——每层 = 运营事件区（固定顺序）+ 战斗关（层末）。
    /// 每层节点序列由 FloorConfig 的事件池 + 战斗节点推导（Map 职责：单线节点序列生成）。
    /// </summary>
    public class MapConfig : GameConfigBase
    {
        /// <summary>各层事件节点配置（顺序固定：改变规则[2/3/4关]→能力→棋子编辑→牌组构筑）。</summary>
        public List<FloorConfig> floors = new List<FloorConfig>();
    }
}
