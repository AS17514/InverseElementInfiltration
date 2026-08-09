using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>
    /// 程序块模板定义（SO 资产，独立模板库条目）——编辑界面的候选池。
    /// 包装 Template（普通 [Serializable] 类，不继承 ScriptableObject——需要 SO 包装才能资产化/可视化）。
    /// templateKey = "种类+编号"（如 "Move-1" / "Attack-11"）——与描述表 key、棋子内联模块编号同构。
    /// </summary>
    public class TemplateDef : GameConfigBase
    {
        public string templateKey;   // 种类+编号（"Move-1"）——TemplateLibrary 按此查询
        public Template template;    // 实际模板结构（MoveTemplate/AttackTemplate 等）
    }
}
