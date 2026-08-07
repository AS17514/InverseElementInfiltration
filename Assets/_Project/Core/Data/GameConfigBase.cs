using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 配置资产基类（ScriptableObject）：数据层全部配置资产（PieceDef/FloorConfig/RelicDef...）继承。
    /// 数值配置载体 = SO（类型安全 + 可视化编辑），Addressables 加载。
    /// </summary>
    public abstract class GameConfigBase : ScriptableObject
    {
        [SerializeField]
        private int _id;

        /// <summary>配置唯一 id（ConfigTable 按此查询，fail-fast）。</summary>
        public int Id => _id;
    }
}
