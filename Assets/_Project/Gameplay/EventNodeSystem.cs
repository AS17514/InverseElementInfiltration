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
        private readonly TutorialSystem _tutorialSystem; // 2026-08-25 教程契约：事件打开触发点（TryShow 跨局去重 → TutorialRequested）
        private string _consumedEventId; // 已消费选项的事件 id（每个 OpenEvent 只允许一次选项消费——2026-08-12 防重复点选双落账）

        public EventNodeSystem(GameState state, Resolver resolver, TutorialSystem tutorialSystem)
        {
            _state = state;
            _resolver = resolver;
            _tutorialSystem = tutorialSystem;
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
            _consumedEventId = null; // 新事件 = 新消费机会（2026-08-12：事件级只允许一次选项消费）
            // 能力事件（2026-08-22）：打开即按玩法词条抽取 3 候选（三选一 + 每项刷新）——不显示固定选项
            var ev = ConfigTable.FindByName<EventDefinition>(picked.eventId);
            if (ev != null && ev.isAbilityPick)
            {
                _resolver.DrawAbilityCandidates();
                if (_tutorialSystem != null) _tutorialSystem.TryShow("event_intro"); // 2026-08-25 教程契约：能力事件 → 教程序列（跨局去重由 TryShow 内部判；直接传 id 不经映射）
                return; // 能力事件不走普通 EventOpened/选项流（前端按 AbilityCandidatesDrawn 显示三选一）
            }
            // 玩法事件（2026-08-24 玩法选择机制）：打开即从未激活玩法抽 2 候选（二选一，不可刷新）——不显示固定选项
            if (ev != null && ev.isRulePick)
            {
                _resolver.DrawRuleCandidates();
                return; // 玩法事件不走普通 EventOpened/选项流（前端按 RuleCandidatesDrawn 显示二选一）
            }
            // 通知 UI：事件关打开（携带当前事件 id——UI 据此打开事件界面）
            TryShowTutorial(ev != null ? ev.name : null); // 2026-08-25 教程契约：按事件 id 映射教程序列（edit_standard→edit_intro / deck_standard→deck_intro；其余无）
            EventCenter.Instance.EventTrigger(GameEvent.EventOpened, _state.CurrentEventId);
        }

        /// <summary>教程触发点（2026-08-25 契约）：普通事件流打开（EventOpened 前）→ 按事件 id 映射教程序列 → TryShow（跨局持久去重 → TutorialRequested 事件→前端播放）。</summary>
        private void TryShowTutorial(string eventId)
        {
            if (_tutorialSystem == null || string.IsNullOrEmpty(eventId)) return;
            switch (eventId)
            {
                case "edit_standard": _tutorialSystem.TryShow("edit_intro"); break;  // 编辑事件
                case "deck_standard": _tutorialSystem.TryShow("deck_intro"); break;  // 构筑事件
                // 能力事件（ability_pick）走 isAbilityPick 分支独立触发（上方）——此处不重复
            }
        }

        /// <summary>
        /// 选择选项（availability 校验 + 事件级消费守卫——每个事件只允许一次选项消费）。
        /// ⚠️ 2026-08-12 防重：UI 延迟完成窗口内重复点选 → 效果二次落账（双遗物/双卡）+ 迟到推进跳节点——
        /// 守卫拒绝非当前事件/已消费事件的选项。
        /// </summary>
        public void OnOptionSelected(string eventId, int optionIndex)
        {
            if (eventId != _state.CurrentEventId || eventId == _consumedEventId)
            {
                return; // 非当前事件 / 已消费过——拒绝（防重复点选）
            }
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
            _consumedEventId = eventId; // 消费标记——本事件后续选项调用一律拒绝
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
                        // 棋子编辑事件（2026-08-19 流程落地）：抽三选一候选（未修改基础棋子，三类型各 1）
                        // + 抽 4 候选模块（移动/攻击各 2——RandomManager 种子相关；效果不参与——2026-08-24）→ 发事件（UI 显示三选一面板）
                        DrawEditCandidates();
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

        /// <summary>
        /// 编辑事件候选抽取（2026-08-19 流程落地）：
        /// ① 三选一：未修改基础棋子（CurrentPrograms 无该棋子 = 无编辑差异）按类型（初始/部署/升变）各随机 1（RandomManager——可复现）；
        /// ② 4 候选模块：模板库按类型分组（移动/攻击）各随机抽 2（无放回；效果不参与——2026-08-24 取消效果编辑）。
        /// 结果写 GameState（存档字段）→ 发 EditCandidatesDrawn（UI 三选一面板；选 1 后调 EditorSession.ConfirmEditPiece + BeginEdit）。
        /// </summary>
        private void DrawEditCandidates()
        {
            // ① 三选一（三类型各抽 1；该组无未修改棋子 → 跳过）
            var pieces = new List<int>();
            foreach (var type in new[] { PieceType.Initial, PieceType.Deployable, PieceType.Promoted })
            {
                var pool = new List<int>();
                foreach (var def in ConfigTable.All<PieceDef>())
                {
                    if (def.pieceType == type && !_state.CurrentPrograms.ContainsKey(def.Id))
                    {
                        pool.Add(def.Id);
                    }
                }
                if (pool.Count > 0)
                {
                    pieces.Add(pool[RandomManager.Instance.Range(0, pool.Count)]);
                }
            }
            // ② 4 候选模块（移动/攻击各抽 2——2026-08-24 策划定案：取消效果编辑，效果不进候选池；某池为空 → 实际数量可能 < 4——防御）
            var modules = new List<Template>();
            var movePool = new List<Template>();
            var attackPool = new List<Template>();
            foreach (var t in TemplateLibrary.All())
            {
                if (t is MoveTemplate) movePool.Add(t);
                else if (t is AttackTemplate) attackPool.Add(t);
                // EffectTemplate 不参与编辑候选（效果 = 默认程序保留的被动，不可编辑——2026-08-24 策划定案）
            }
            modules.AddRange(DrawTwo(movePool));
            modules.AddRange(DrawTwo(attackPool));

            _state.EditCandidates = pieces;
            _state.EditModuleCandidates = modules;
            EventCenter.Instance.EventTrigger(GameEvent.EditCandidatesDrawn, null);
        }

        /// <summary>从池随机抽 2（无放回——RandomManager.Range 种子相关可复现）；池空/不足 → 实际数量。</summary>
        private static List<Template> DrawTwo(List<Template> pool)
        {
            var result = new List<Template>();
            var copy = new List<Template>(pool);
            while (result.Count < 2 && copy.Count > 0)
            {
                int idx = RandomManager.Instance.Range(0, copy.Count);
                result.Add(copy[idx]);
                copy.RemoveAt(idx);
            }
            return result;
        }
    }
}
