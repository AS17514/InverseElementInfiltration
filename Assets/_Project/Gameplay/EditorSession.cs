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

        /// <summary>实时修改（经 Resolver 落账；旧程序入撤销栈）。</summary>
        public void EditProgram(int defId, List<Template> program)
        {
            if (!_undoStacks.TryGetValue(defId, out var stack))
            {
                stack = new Stack<EditOp>();
                _undoStacks[defId] = stack;
            }
            stack.Push(new EditOp { DefId = defId, Before = GetCurrentProgram(defId) });
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

        /// <summary>还原单棋子（用初始快照，经 Resolver）。</summary>
        public void RestoreOriginal(int defId)
        {
            if (_initialSnapshots.TryGetValue(defId, out var original))
            {
                _resolver.ApplyProgramEdit(defId, original);
            }
        }

        /// <summary>全部还原（遍历初始快照，经 Resolver）。</summary>
        public void RestoreAll()
        {
            foreach (var pair in _initialSnapshots)
            {
                _resolver.ApplyProgramEdit(pair.Key, pair.Value);
            }
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
        /// 编辑候选池（2026-08-19 双池模型——前端切换棋子时查询并刷新 UI）：
        /// ① 外部共享池 = 模板库（纯外部编号模块——templates.json 已不含内置编号，外部价）
        /// ② 本棋子"被覆盖的内置槽" = 默认程序槽中内置编号不在当前程序的槽（差集推导，零存储——
        ///    符合原则 4：可推导不入快照；仅本棋子可见——其他棋子候选不含它，天然满足"隐藏格"语义）
        /// 覆盖后玩家可把内置槽单独放回本棋子；还原（RestoreAll）清编辑差异后差集自然为空。
        /// </summary>
        public List<Template> GetEditCandidates(int defId)
        {
            var result = new List<Template>();
            // ① 外部共享池（模板库——外部独立模块，所有棋子可见）
            foreach (var template in TemplateLibrary.All())
            {
                result.Add(template);
            }
            // ② 本棋子被覆盖的内置槽（默认程序 − 当前程序）
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
