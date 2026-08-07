using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 爬塔推进器：只做推进行为（塔状态在 GameState，不实现 ISnapshot）。
    /// 每层 = 运营事件区（固定顺序：改变规则[2/3/4关]→能力→棋子编辑→牌组构筑）+ 战斗关（层末）。
    /// </summary>
    public class TowerFlow
    {
        private readonly GameState _state;
        private readonly EventNodeSystem _eventNodeSystem;
        private readonly BattleFlow _battleFlow;
        private readonly MapConfig _map;

        /// <summary>每层事件节点数（第 1 关无"改变规则"事件 → 3 个；其余 4 个）。</summary>
        private int EventCountForFloor(int floorIndex) => floorIndex == 0 ? 3 : 4;

        public TowerFlow(GameState state, EventNodeSystem eventNodeSystem, BattleFlow battleFlow, MapConfig map)
        {
            _state = state;
            _eventNodeSystem = eventNodeSystem;
            _battleFlow = battleFlow;
            _map = map;
        }

        /// <summary>进入某层：生成单线节点序列（事件区 → 战斗关），从第一个节点开始。</summary>
        public void EnterFloor(int floorIndex)
        {
            _state.CurrentFloor = floorIndex;
            _state.CurrentNodeIndex = 0;
            _state.NodeStates.Clear();
            int eventCount = EventCountForFloor(floorIndex);
            for (int i = 0; i < eventCount; i++)
            {
                _state.NodeStates.Add(NodeState.Available);
            }
            _state.NodeStates.Add(NodeState.Available); // 战斗关（层末）
            AdvanceNode();
        }

        /// <summary>推进到当前节点（事件 → 打开事件关；战斗 → 开战）。</summary>
        public void AdvanceNode()
        {
            if (_state.CurrentNodeIndex >= _state.NodeStates.Count)
            {
                // 本层结束 → 下一层（第 4 关后 → 通关）
                if (_state.CurrentFloor + 1 < _map.floors.Count)
                {
                    EnterFloor(_state.CurrentFloor + 1);
                }
                else
                {
                    OnRunEnded(true);
                }
                return;
            }
            _state.NodeStates[_state.CurrentNodeIndex] = NodeState.Completed;
            int index = _state.CurrentNodeIndex;
            _state.CurrentNodeIndex++;

            bool isBattle = index == _state.NodeStates.Count - 1; // 战斗关在层末
            if (isBattle)
            {
                var floor = _map.floors[_state.CurrentFloor];
                var aiParams = GetDefaultAIParams();
                _battleFlow.StartBattle(floor, aiParams);
            }
            else
            {
                var pools = GetFloorEventPools();
                _eventNodeSystem.OpenEvent($"event-{_state.CurrentFloor}-{index}", pools);
            }
        }

        /// <summary>战斗结束回调（胜利 → 推进；失败 → 整局结束）。</summary>
        public void OnBattleEnded(Side winner)
        {
            if (winner == Side.Player)
            {
                AdvanceNode();
            }
            else
            {
                OnRunEnded(false);
            }
        }

        /// <summary>本层事件池（从 FloorConfig.eventPoolIds 查）。</summary>
        private List<EventPool> GetFloorEventPools()
        {
            var result = new List<EventPool>();
            var floor = _map.floors[_state.CurrentFloor];
            foreach (var poolId in floor.eventPoolIds)
            {
                var pool = ConfigTable.FindByName<EventPool>(poolId);
                if (pool != null)
                {
                    result.Add(pool);
                }
            }
            return result;
        }

        private AIParams GetDefaultAIParams()
        {
            foreach (var ai in ConfigTable.All<AIParams>())
            {
                return ai;
            }
            return new AIParams(); // 无配置时默认值兜底
        }

        private void OnRunEnded(bool victory)
        {
            EventCenter.Instance.EventTrigger(GameEvent.RunEnded, victory);
            // Bootstrap.OnRunEnded 监听：SaveAll → ResetForNewRun → 回主菜单（装配职责在入口）
        }
    }
}
