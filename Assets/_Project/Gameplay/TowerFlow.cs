using System;
using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;
using UnityEngine;

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
        private readonly Func<BattleFlow> _battleFlowFactory; // 战斗级"进入创建"（2026-08-13：每场战斗创建新 BattleFlow）
        private BattleFlow _battleFlow;                       // 当前战斗实例（可空——非战斗中为 null）
        private readonly MapConfig _map;

        public TowerFlow(GameState state, EventNodeSystem eventNodeSystem, Func<BattleFlow> battleFlowFactory, MapConfig map)
        {
            _state = state;
            _eventNodeSystem = eventNodeSystem;
            _battleFlowFactory = battleFlowFactory;
            _map = map;
            // 事件完成推进（与 PlacementFinished 同模式：UI 报告完成，规则层决定推进）
            EventCenter.Instance.AddEventListener(GameEvent.EventCompleted, OnEventCompleted);
        }

        /// <summary>
        /// 销毁钩子（2026-08-13 整局级"进入创建、离开销毁"）：注销构造注册的监听 + 销毁当前战斗。
        /// ⚠️ 必须对称于构造注册——漏注销 = 旧实例幽灵回调（EventCompleted 跨局双推进）。
        /// </summary>
        public void Dispose()
        {
            DisposeCurrentBattle();
            EventCenter.Instance.RemoveEventListener(GameEvent.EventCompleted, OnEventCompleted);
        }

        /// <summary>当前战斗实例（Bootstrap 创建战斗控制器时取——非战斗中为 null）。</summary>
        public BattleFlow CurrentBattleFlow => _battleFlow;

        /// <summary>战斗中续玩/重开当前关战斗（2026-08-24 临时方案：Continue 战斗档 → 已加载 SL 槽 → 直接开战；
        /// 不经 AdvanceNode 推进——战斗从开头重打[SL 重打语义]，构筑/遗物/塔进度保留）。</summary>
        public void StartBattleAtCurrentFloor()
        {
            if (!TryGetFloor(_state.CurrentFloor, out var floor)) return; // 存档越界防御：非法 CurrentFloor 不崩（LogError + 安全退出）
            _state.CurrentFloorConfig = floor; // 2026-08-24 续玩：SL 槽不含 CurrentFloorConfig（不入档——EnterFloor 路径重设）——补设
            var aiParams = GetDefaultAIParams();
            _battleFlow = _battleFlowFactory();
            _battleFlow.StartBattle(floor, aiParams);
        }

        /// <summary>
        /// 销毁当前战斗实例（"离开销毁"——注销监听防幽灵回调；胜利/失败/退出/新游戏路径均调用）。
        /// ⚠️ 2026-08-13 战斗级改造：BattleFlow 每场创建、战斗结束销毁——瞬态字段随实例消失（不再依赖手清清单）。
        /// </summary>
        public void DisposeCurrentBattle()
        {
            _battleFlow?.Dispose();
            _battleFlow = null;
        }

        /// <summary>
        /// 事件关完成（UI 发 EventCompleted，携带当前事件 id）→ 推进下一个节点（下一事件或战斗）。
        /// ⚠️ 2026-08-12 防重：信号必须对应当前事件（data 为当前事件 id）——重复/过期信号（id 不匹配）被拒绝，防 UI 连发跳节点。
        /// </summary>
        private void OnEventCompleted(object data)
        {
            if (!(data is string eventId) || eventId != _state.CurrentEventId)
            {
                return; // 信号不匹配当前事件（旧/重复信号）——拒绝
            }
            AdvanceNode();
        }

        /// <summary>
        /// 进入某层：生成单线节点序列——事件节点 = FloorConfig.eventSequence（类型固定顺序）
        /// + 战斗关（层末）。事件节点各自对应 eventPoolIds[i] 的池（类型内变体随机）。
        /// </summary>
        public void EnterFloor(int floorIndex)
        {
            if (!TryGetFloor(floorIndex, out var floor)) return; // 越界防御：非法层索引不崩（LogError + 安全退出）
            _state.CurrentFloor = floorIndex;
            _state.CurrentFloorConfig = floor; // 2026-08-20：当前关卡配置引用（Resolver 读 scoreDeductEnabled 等）
            _state.CurrentNodeIndex = 0;
            _state.NodeStates.Clear();
            _state.ConsumedModules.Clear(); // 2026-08-23 消耗制：进层复原（候选池=模板库−本层占用增量；跨层上层用过的模块可再抽）
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
                // 战斗级"进入创建"（2026-08-13）：每场战斗创建新 BattleFlow——瞬态字段随实例归零
                _battleFlow = _battleFlowFactory();
                _battleFlow.StartBattle(floor, aiParams);
            }
            else
            {
                var floor = _map.floors[_state.CurrentFloor];
                var pool = GetEventPool(floor, index); // 本节点类型对应的事件池（类型内变体随机）
                _eventNodeSystem.OpenEvent($"event-{_state.CurrentFloor}-{index}", pool);
            }
        }

        /// <summary>
        /// 战斗结束回调（2026-08-27 时序修正：胜利不再立即推进——先销毁当前战斗实例，
        /// 推进等结算面板确认后由 Bootstrap 经 loading 过渡调用 AdvanceNode）。
        /// 时序安全：EndBattle 先发 StateChanged（结算面板快照）后走到本方法——快照在前、销毁在后。
        /// 失败仍立即触发 RunEnded（Bootstrap 侧挂起收尾，确认前保持战斗场景）。
        /// </summary>
        public void OnBattleEnded(Side winner)
        {
            DisposeCurrentBattle(); // 离开销毁（注销监听防幽灵回调；战斗视觉仍留在场景，结算面板覆盖其上）
            if (winner == Side.Enemy)
            {
                OnRunEnded(false);
            }
            // 胜利：不在此推进——由 Bootstrap 在 BattleResultPanel.OnConfirmed 后调用 AdvanceNode（含 loading 过渡）
        }

        /// <summary>楼层索引越界防御（存档越界 Continue / 非法 EnterFloor）——越界 LogError 并返回 false，调用方安全退出。</summary>
        private bool TryGetFloor(int floorIndex, out FloorConfig floor)
        {
            if (floorIndex < 0 || floorIndex >= _map.floors.Count)
            {
                Debug.LogError($"[TowerFlow] 楼层索引越界：{floorIndex}（共 {_map.floors.Count} 层）——阻止进入");
                floor = null;
                return false;
            }
            floor = _map.floors[floorIndex];
            return true;
        }

        /// <summary>节点类型对应的事件池（FloorConfig.eventPoolIds[eventIndex]——与 eventSequence 顺序对应）。</summary>
        private EventPool GetEventPool(FloorConfig floor, int eventIndex)
        {
            if (eventIndex >= 0 && eventIndex < floor.eventPoolIds.Count)
            {
                var pool = ConfigTable.FindByName<EventPool>(floor.eventPoolIds[eventIndex]);
                if (pool != null) return pool;
                Debug.LogError($"[TowerFlow] 事件池缺失：{floor.eventPoolIds[eventIndex]}（eventIndex={eventIndex}）——回退上一可用池");
            }
            else
            {
                Debug.LogError($"[TowerFlow] 事件池索引越界：{eventIndex}（共 {floor.eventPoolIds.Count}）——回退上一可用池");
            }
            // 越界/缺失兜底：复用上一可用池（不返回 null → OpenEvent 不抛断言卡节点）
            for (int i = eventIndex - 1; i >= 0; i--)
            {
                if (i < floor.eventPoolIds.Count)
                {
                    var fallback = ConfigTable.FindByName<EventPool>(floor.eventPoolIds[i]);
                    if (fallback != null) return fallback;
                }
            }
            return null; // 无上一可用池——交由 OpenEvent 空池安全路径兜底
        }

        private AIParams GetDefaultAIParams()
        {
            foreach (var ai in ConfigTable.All<AIParams>())
            {
                return ai;
            }
            return ScriptableObject.CreateInstance<AIParams>(); // SO 不能用 new——CreateInstance 兜底
        }

        private void OnRunEnded(bool victory)
        {
            EventCenter.Instance.EventTrigger(GameEvent.RunEnded, victory);
            // Bootstrap.OnRunEnded 监听：SaveAll → ResetForNewRun → 回主菜单（装配职责在入口）
        }
    }
}
