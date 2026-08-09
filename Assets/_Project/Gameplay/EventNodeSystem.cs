using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 事件关：事件池加权抽取 → 选项 → 效果（经 Resolver 落账——禁止绕过结算器）。
    /// 事件级无条件、池级有条件；抽取过滤已抽事件，候选空 → 必出兜底。
    /// </summary>
    public class EventNodeSystem
    {
        private readonly GameState _state;
        private readonly Resolver _resolver;

        public EventNodeSystem(GameState state, Resolver resolver)
        {
            _state = state;
            _resolver = resolver;
        }

        /// <summary>
        /// 打开事件节点：从【本节点类型对应的事件池】随机抽 1 个事件（类型内变体随机——顺序固定由 TowerFlow 节点序列保证）。
        /// </summary>
        public void OpenEvent(string nodeId, EventPool pool)
        {
            var candidates = new List<EventPoolEntry>();
            if (pool != null)
            {
                foreach (var entry in pool.entries)
                {
                    if (!_state.DrawnEventIds.Contains(entry.eventId))
                    {
                        candidates.Add(entry);
                    }
                }
            }
            // 候选空 → 必出兜底（该池全部条目；池空则断言——配置缺失当场暴露）
            if (candidates.Count == 0)
            {
                if (pool == null || pool.entries.Count == 0)
                {
                    Core.Assert.Fail($"OpenEvent: 事件池为空或缺失（node={nodeId}）——检查 FloorConfig.eventPoolIds");
                    return;
                }
                candidates = new List<EventPoolEntry>(pool.entries);
            }
            var picked = RandomManager.Instance.NextWeighted(candidates, e => e.weight);
            _state.CurrentEventId = picked.eventId;
            _state.DrawnEventIds.Add(picked.eventId);
            // 通知 UI：事件关打开（携带当前事件 id——UI 据此打开事件界面）
            EventCenter.Instance.EventTrigger(GameEvent.EventOpened, _state.CurrentEventId);
        }

        /// <summary>选择选项（availability 校验 + 防重入——执行效果）。</summary>
        public void OnOptionSelected(string eventId, int optionIndex)
        {
            var ev = FindEvent(eventId);
            if (ev == null || optionIndex < 0 || optionIndex >= ev.options.Count)
            {
                return;
            }
            var option = ev.options[optionIndex];
            if (!option.available)
            {
                return; // 二次校验（UI 已灰显）
            }
            ExecuteEffects(option.effects);
        }

        /// <summary>目标选择（targetRule 空时走这步——玩家手动选目标棋子）。</summary>
        public void OnTargetSelected(int pieceId)
        {
            // 目标选择结果由效果执行流程消费（当前效果作用目标由调用方传入——骨架）
        }

        /// <summary>执行效果（全部经 Resolver 落账）。</summary>
        public void ExecuteEffects(List<EffectDefinition> effects)
        {
            foreach (var effect in effects)
            {
                switch (effect.effectType)
                {
                    case EffectType.AddPiece:
                        _resolver.AddToHand(effect.targetDefId); // 增加新棋子（预定义池选择——运行时不合成）
                        break;
                    case EffectType.ModifyDurability:
                        _resolver.ModifyTargetDurability(effect.targetDefId, effect.amount); // 目标棋子（简化：按 defId 首棋子）
                        break;
                    case EffectType.EditProgram:
                        // 棋子编辑事件：打开编辑器（UI 交互——由 UI 层触发 EditorSession；此处占位）
                        EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "edit");
                        break;
                    case EffectType.GrantAbility:
                        _resolver.GrantTempAbility(effect.targetDefId, effect.abilityId); // 给予临时特殊能力
                        break;
                    case EffectType.GrantRelic:
                        _resolver.AddRelic(effect.relicName); // 获得遗物（整局持续）
                        break;
                    case EffectType.DeckBuild:
                        // 牌组构筑：打开构筑界面（UI 交互——由 UI 层触发；此处占位）
                        EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "deck");
                        break;
                }
            }
        }

        private EventDefinition FindEvent(string eventId)
        {
            foreach (var pool in ConfigTable.All<EventPool>())
            {
                foreach (var entry in pool.entries)
                {
                    if (entry.eventId == eventId)
                    {
                        // eventId 按资产名匹配（EventDefinition.name）
                        return ConfigTable.FindByName<EventDefinition>(eventId);
                    }
                }
            }
            return null;
        }
    }
}
