using System;

namespace TheLaw.Data
{
    /// <summary>
    /// buff 显示信息（展示查询器产出——UI 消费）。
    /// 只含机器数据（key + 剩余量）——名称/描述/图标由 UI 按 key 查配置表
    /// （Assets/Data/Pieces/buffs-descriptions.json），未命中回退 key。
    /// </summary>
    [Serializable]
    public class BuffInfo
    {
        public string key;      // 唯一标识（"shield" / "free_execute" / "ability_{Id}"）——UI 查配置表显示
        public int remaining;   // 剩余量（护盾=剩余盾数；免费行动=1；-1=无剩余概念）
    }
}
