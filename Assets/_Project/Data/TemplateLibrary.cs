using System.Collections.Generic;
using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>
    /// 程序块模板库（静态注册表）：按"种类+编号"查询独立模板定义（编辑界面候选池）。
    /// 数据源 = TemplateDef 资产（templates.json → 导入器 → 资产），Bootstrap 启动时注册。
    /// 与棋子内联模块的关系：独立并存（棋子内联 = 默认程序，模板库 = 编辑可选池——语义不同，
    /// 同结构同编号互认：描述表/编号体系统一）。
    /// </summary>
    public static class TemplateLibrary
    {
        private static readonly Dictionary<string, Template> _templates = new Dictionary<string, Template>();

        /// <summary>注册模板（按 templateKey 覆盖；重复注册断言）。</summary>
        public static void Register(TemplateDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.templateKey) || def.template == null)
            {
                return;
            }
            Assert.IsTrue(!_templates.ContainsKey(def.templateKey),
                $"TemplateLibrary: 重复注册模板 {def.templateKey}");
            _templates[def.templateKey] = def.template;
        }

        /// <summary>按种类+编号查询（如 "Move-1"；未找到返回 null——调用方回退棋子自带模板）。</summary>
        public static Template Get(string templateKey)
        {
            return _templates.TryGetValue(templateKey, out var template) ? template : null;
        }

        /// <summary>全部模板（编辑界面候选池遍历用）。</summary>
        public static IEnumerable<Template> All()
        {
            return _templates.Values;
        }

        /// <summary>清空（测试/整局重置用）。</summary>
        public static void Clear()
        {
            _templates.Clear();
        }
    }
}
