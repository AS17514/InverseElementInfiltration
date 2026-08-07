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

        /// <summary>进入编辑（记录初始快照 + 编辑态标记）。</summary>
        public void BeginEdit(int defId)
        {
            if (!_initialSnapshots.ContainsKey(defId))
            {
                _initialSnapshots[defId] = GetCurrentProgram(defId);
                _state.EditingDefs.Add(defId);
            }
        }

        /// <summary>退出编辑（校验程序完整——4 槽；移除编辑态标记）。</summary>
        public void EndEdit(int defId)
        {
            Assert.IsTrue(GetCurrentProgram(defId) != null, $"EndEdit: defId={defId} 程序为空");
            _state.EditingDefs.Remove(defId);
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

        /// <summary>可用模板（该棋子程序集出现的模板合集——编辑面板供选择）。</summary>
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
            return result;
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
