using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 编辑会话（实时修改方案 B）：编辑直接写 GameState 当前程序表（经 Resolver），
    /// 无弹窗；配套：编辑态标记（防半截程序进战斗）+ 撤销机制（初始快照 + 撤销栈，会话级不入存档）。
    /// </summary>
    public class EditorSession
    {
        private readonly GameState _state;
        private readonly Resolver _resolver;
        private readonly Dictionary<int, List<Template>> _initialSnapshots = new Dictionary<int, List<Template>>(); // 进入编辑时的原样
        private readonly Dictionary<int, Stack<EditOp>> _undoStacks = new Dictionary<int, Stack<EditOp>>();

        public EditorSession(GameState state, Resolver resolver)
        {
            _state = state;
            _resolver = resolver;
        }

        /// <summary>重置会话（新局必清——快照/撤销栈为会话级不入存档，跨局残留会让"恢复原样"恢复到上一局，后端待办 #7）。</summary>
        public void ResetSession()
        {
            _initialSnapshots.Clear();
            _undoStacks.Clear();
        }

        /// <summary>进入编辑（记录初始快照 + 编辑态标记）。</summary>
        public void BeginEdit(int defId)
        {
            if (!_initialSnapshots.ContainsKey(defId))
            {
                _initialSnapshots[defId] = GetCurrentProgram(defId);
                _state.EditingDefs.Add(defId);
            }
        }

        /// <summary>
        /// 退出编辑（校验程序非空——至少 1 槽；移除编辑态标记）。
        /// ⚠️ 2026-08-12：原断言 `!= null` 恒真（GetCurrentProgram 永不返回 null，空程序也通过）——
        /// 空程序进战斗 = 棋子永不行动（缺陷）。改为 Count &gt; 0 校验；失败返回 false（UI 提示，不移除标记）。
        /// 注："必须 4 槽"是玩法规则（待策划确认）——此处只做"非空"防御。
        /// </summary>
        public bool EndEdit(int defId)
        {
            if (GetCurrentProgram(defId).Count == 0)
            {
                return false; // 空程序：拒绝结束编辑（防"废棋子"进战斗）
            }
            _state.EditingDefs.Remove(defId);
            return true;
        }

        /// <summary>实时修改（经 Resolver 落账；旧程序入撤销栈）。
        /// ⚠️ 2026-08-19 hide 模式（EditConfig——策划"直接隐藏"）：离开程序的**外部模块**记入本棋子隐藏集合
        /// （候选区不可见——不可再选；回退靠撤销/还原——快照恢复程序后模块回程序，过滤自然不命中；还原清空集合）。
        /// show 模式（默认）：不做记录（外部模块本就在候选池；内置模块差集推导进隐藏格）。</summary>
        public void EditProgram(int defId, List<Template> program)
        {
            var before = GetCurrentProgram(defId);
            if (EditConfig.IsHideMode)
            {
                foreach (var m in before)
                {
                    if (!ContainsSlot(program, m) && !IsBuiltinSlot(m))
                    {
                        AddHidden(defId, m);
                    }
                }
            }
            if (!_undoStacks.TryGetValue(defId, out var stack))
            {
                stack = new Stack<EditOp>();
                _undoStacks[defId] = stack;
            }
            stack.Push(new EditOp { DefId = defId, Before = before });
            _resolver.ApplyProgramEdit(defId, program);
        }

        /// <summary>是否有可撤销历史（UI 空栈置灰用——只读查询，不改状态）。</summary>
        public bool CanUndo(int defId)
        {
            return _undoStacks.TryGetValue(defId, out var stack) && stack.Count > 0;
        }

        /// <summary>清空指定棋子的撤销历史（"全部撤回"后历史无意义——UI 调用）。</summary>
        public void ClearHistory(int defId)
        {
            if (_undoStacks.TryGetValue(defId, out var stack)) stack.Clear();
        }

        /// <summary>撤销上一步（弹栈恢复 before，经 Resolver）。</summary>
        public void Undo(int defId)
        {
            if (_undoStacks.TryGetValue(defId, out var stack) && stack.Count > 0)
            {
                var op = stack.Pop();
                _resolver.ApplyProgramEdit(defId, op.Before);
            }
        }

        /// <summary>还原单棋子（用初始快照，经 Resolver；2026-08-19：还原 = 恢复初始——清 hide 隐藏标记恢复展示）。</summary>
        public void RestoreOriginal(int defId)
        {
            if (_initialSnapshots.TryGetValue(defId, out var original))
            {
                _resolver.ApplyProgramEdit(defId, original);
                _state.HiddenModules.Remove(defId);
            }
        }

        /// <summary>全部还原（遍历初始快照，经 Resolver；hide 隐藏标记一并清空）。</summary>
        public void RestoreAll()
        {
            foreach (var pair in _initialSnapshots)
            {
                _resolver.ApplyProgramEdit(pair.Key, pair.Value);
            }
            _state.HiddenModules.Clear();
        }

        /// <summary>
        /// 可用模板（编辑面板供选择）= 棋子自带程序集 ∪ 独立模板库（按 id 去重——同编号=同结构）。
        /// 模板库为编辑候选池：玩家可编排超出自带范围的模板（如事件文案承诺的"攻击[后·左·右选一格]"）。
        /// </summary>
        public List<Template> GetAvailableTemplates(PieceDef def)
        {
            var result = new List<Template>();
            foreach (var program in def.programSet)
            {
                foreach (var slot in program.slots)
                {
                    if (!result.Contains(slot))
                    {
                        result.Add(slot);
                    }
                }
            }
            foreach (var template in TemplateLibrary.All())
            {
                // 按 id 去重（id=0 未编号直接加入——不参与去重）
                bool duplicated = false;
                foreach (var existing in result)
                {
                    if (template.id != 0 && existing.id == template.id)
                    {
                        duplicated = true;
                        break;
                    }
                }
                if (!duplicated)
                {
                    result.Add(template);
                }
            }
            return result;
        }

        /// <summary>
        /// 编辑候选池（2026-08-19 双池模型 + 两方案切换——前端切换棋子时查询并刷新 UI）：
        /// ① 外部候选 = 编辑事件抽取的 6 模块（移动/攻击/效果各 2——EditModuleCandidates，事件级；无事件/过渡 = 模板库全部）
        ///    ——hide 模式（EditConfig）：再过滤本棋子隐藏集合（被替换/移除的外部模块——候选区不可见）
        /// ② 本棋子"被替换的内置模块" = 默认程序槽中内置编号不在当前程序的槽（差集推导，零存储——
        ///    符合原则 4：可推导不入快照；仅本棋子可见）——**show 模式展示；hide 模式不展示（策划直接隐藏）**
        /// 还原（RestoreAll）清编辑差异后差集自然为空；hide 模式还原同时清隐藏集合（恢复展示）。
        /// </summary>
        public List<Template> GetEditCandidates(int defId)
        {
            var result = new List<Template>();
            // ① 外部候选（编辑事件 6 候选优先；无事件/过渡 = 模板库全部）
            if (_state.EditModuleCandidates != null && _state.EditModuleCandidates.Count > 0)
            {
                foreach (var template in _state.EditModuleCandidates)
                {
                    result.Add(template);
                }
            }
            else
            {
                foreach (var template in TemplateLibrary.All())
                {
                    result.Add(template);
                }
            }
            // hide 模式：过滤本棋子已隐藏模块（被替换/移除的外部模块——候选区不可见）
            if (EditConfig.IsHideMode && _state.HiddenModules.TryGetValue(defId, out var hidden))
            {
                result.RemoveAll(m => ContainsSlot(hidden, m));
            }
            // ② 本棋子被替换的内置模块（差集——show 模式展示；hide 模式不展示）
            if (!EditConfig.IsHideMode)
            {
                var def = ConfigTable.Get<PieceDef>(defId);
                if (def != null && def.programSet != null && def.programSet.Count > 0)
                {
                    var current = GetCurrentProgram(defId); // 编辑差异优先；空则=默认（未编辑——内置槽仍在棋子上，无需放回）
                    foreach (var slot in def.programSet[0].slots)
                    {
                        if (IsBuiltinSlot(slot) && !ContainsSlot(current, slot))
                        {
                            result.Add(slot);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>内置编号判定（2026-08-19 双池模型：棋子内置槽用 Move≤9 / Attack≤11；外部模块编号 ≥10/≥12）。</summary>
        private static bool IsBuiltinSlot(Template slot)
        {
            switch (slot)
            {
                case MoveTemplate m: return m.id > 0 && m.id <= 9;
                case AttackTemplate a: return a.id > 0 && a.id <= 11;
                default: return false;
            }
        }

        /// <summary>程序是否含同类型同编号槽（id 相同 = 同结构）。</summary>
        private static bool ContainsSlot(List<Template> program, Template slot)
        {
            foreach (var s in program)
            {
                if (s != null && slot != null && s.GetType() == slot.GetType() && s.id > 0 && s.id == slot.id)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 放回被替换的内置模块（2026-08-19 更名 TryPlaceBuiltinSlotBack → TryRestoreBuiltinModule——操作对象是**模块**不是槽；
        /// 前端先校验后执行：返回 false 则不改状态、前端不得执行任何 UI 更改）：
        /// ① 该模块必须是被替换的内置模块（内置编号且不在当前程序——候选池②）
        /// ② 目标位置必须 = 该模块在默认程序中的**原位置**（放回只能回原位——不给玩家重排灵活性）
        /// 通过：落账——目标位置若被其他模块占据（外部模块）→ **直接替换**（占据者无损失：外部回池/内置进隐藏格——
        /// 2026-08-19 修正：按槽位身份替换 current[originalIndex]，不产生"删错模块"问题）；
        /// ⚠️ 越界防御（2026-08-19 修复）：程序被删得过短（originalIndex &gt; Count）→ 返回 false（不放错位置、防 List.Insert 崩溃）。
        /// ⚠️ hide 模式（EditConfig）：被替换模块不展示、**不可放回**（回退靠撤销/还原）——直接返回 false。
        /// </summary>
        public bool TryRestoreBuiltinModule(int defId, Template slot, int targetIndex)
        {
            if (EditConfig.IsHideMode)
            {
                return false; // hide 模式：无放回（模块隐藏，回退靠撤销/还原）
            }
            var def = ConfigTable.Get<PieceDef>(defId);
            if (def == null || def.programSet == null || def.programSet.Count == 0)
            {
                return false;
            }
            // ① 被替换的内置模块（内置编号 + 不在当前程序 = 在候选池②）
            if (!IsBuiltinSlot(slot))
            {
                return false;
            }
            var current = GetCurrentProgram(defId);
            if (ContainsSlot(current, slot))
            {
                return false; // 仍在程序中——不是被替换模块
            }
            // ② 原位置校验（默认程序索引）
            int originalIndex = -1;
            var defaultSlots = def.programSet[0].slots;
            for (int i = 0; i < defaultSlots.Count; i++)
            {
                if (defaultSlots[i] != null && defaultSlots[i].GetType() == slot.GetType() && defaultSlots[i].id > 0 && defaultSlots[i].id == slot.id)
                {
                    originalIndex = i;
                    break;
                }
            }
            if (originalIndex < 0 || targetIndex != originalIndex)
            {
                return false; // 非该棋子的内置模块 / 目标位置 ≠ 原位置
            }
            // ⚠️ 越界防御（2026-08-19）：程序被删得比原位置短（originalIndex > Count）→ 不放错位置（List.Insert 会抛异常）
            if (originalIndex > current.Count)
            {
                return false;
            }
            // 替换：目标位置若被其他模块占据（外部模块）→ 直接替换（占据者无损失：外部模块本就在候选池/内置模块进隐藏格②）
            if (originalIndex < current.Count)
            {
                current.RemoveAt(originalIndex);
            }
            current.Insert(originalIndex, slot);
            _resolver.ApplyProgramEdit(defId, current); // 经 Resolver 落账（进撤销栈）
            return true;
        }

        /// <summary>
        /// 编辑事件三选一确认（2026-08-19）：校验所选棋子 ∈ 编辑事件抽取的候选（GameState.EditCandidates）——
        /// 通过返回 true，前端随后走现有流程（BeginEdit + 打开编辑面板，候选 = 6 模块）；未抽取/不在候选 → false。
        /// </summary>
        public bool ConfirmEditPiece(int defId)
        {
            return _state.EditCandidates != null && _state.EditCandidates.Contains(defId);
        }

        /// <summary>hide 模式：本棋子隐藏集合添加（按类型+id 去重——模板实例可能不同引用）。</summary>
        private void AddHidden(int defId, Template module)
        {
            if (module == null)
            {
                return;
            }
            if (!_state.HiddenModules.TryGetValue(defId, out var list))
            {
                list = new List<Template>();
                _state.HiddenModules[defId] = list;
            }
            if (!ContainsSlot(list, module))
            {
                list.Add(module);
            }
        }

        /// <summary>预览（棋子副本模拟，不改状态——骨架：用副本实例跑 BoardRules）。</summary>
        public void PreviewProgram(PieceDef def, List<Template> program)
        {
            // TODO: 用临时副本实例 + BoardRules 计算移动/攻击范围展示
        }

        private List<Template> GetCurrentProgram(int defId)
        {
            if (_state.TryGetCurrentProgram(defId, out var program))
            {
                return new List<Template>(program);
            }
            var def = ConfigTable.Get<PieceDef>(defId);
            return def.programSet.Count > 0 ? new List<Template>(def.programSet[0].slots) : new List<Template>();
        }
    }

    /// <summary>编辑操作记录（撤销栈元素）。</summary>
    public class EditOp
    {
        public int DefId;
        public List<Template> Before;
    }
}
