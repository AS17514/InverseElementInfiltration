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

        public TowerFlow(GameState state, EventNodeSystem eventNodeSystem, BattleFlow battleFlow, MapConfig map)
        {
            _state = state;
            _eventNodeSystem = eventNodeSystem;
            _battleFlow = battleFlow;
            _map = map;
        }

        /// <summary>
        /// 进入某层：生成单线节点序列——事件节点 = FloorConfig.eventSequence（类型固定顺序）
        /// + 战斗关（层末）。事件节点各自对应 eventPoolIds[i] 的池（类型内变体随机）。
        /// </summary>
        public void EnterFloor(int floorIndex)
        {
            _state.CurrentFloor = floorIndex;
            _state.CurrentNodeIndex = 0;
            _state.NodeStates.Clear();
            var floor = _map.floors[floorIndex];
            foreach (var _ in floor.eventSequence)
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
                var floor = _map.floors[_state.CurrentFloor];
                var pool = GetEventPool(floor, index); // 本节点类型对应的事件池（类型内变体随机）
                _eventNodeSystem.OpenEvent($"event-{_state.CurrentFloor}-{index}", pool);
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

        /// <summary>节点类型对应的事件池（FloorConfig.eventPoolIds[eventIndex]——与 eventSequence 顺序对应）。</summary>
        private EventPool GetEventPool(FloorConfig floor, int eventIndex)
        {
            if (eventIndex >= 0 && eventIndex < floor.eventPoolIds.Count)
            {
                return ConfigTable.FindByName<EventPool>(floor.eventPoolIds[eventIndex]);
            }
            return null;
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
