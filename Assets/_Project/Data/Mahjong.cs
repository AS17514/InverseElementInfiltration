using System;
using System.Collections.Generic;

namespace TheLaw.Data
{
    /// <summary>
    /// 麻将玩法数据（2026-08-20 实现——源自麻将玩法口述记录）：
    /// 墙体格数据、玩法常量、牌山刻子/顺子判定。
    /// </summary>
    public static class Mahjong
    {
        /// <summary>麻将玩法标识（GameState.ActiveStyles）。</summary>
        public const string StyleId = "mahjong";

        /// <summary>麻将牌总数（一至九各两张 = 18）。</summary>
        public const int TotalTiles = 18;

        /// <summary>一分值一份（一至九各两张）。</summary>
        public static IEnumerable<Card> Tiles()
        {
            for (int i = 1; i <= 9; i++)
            {
                yield return Card.Mahjong(i);
                yield return Card.Mahjong(i);
            }
        }

        /// <summary>
        /// 判定一组 3 个数字是否为 刻子（三个相同）或 顺子（三个连续——**不要求顺序**，645 也算）。
        /// 2026-08-20：判定在移除之前（第 3 个填入时 3 个一起判；组 → 番数 +1 清空牌山；不组 → 移除最早，牌山 ≤2）。
        /// </summary>
        public static bool IsTripletOrSequence(IReadOnlyList<int> three)
        {
            if (three == null || three.Count != 3) return false;
            // 刻子：三个相同
            if (three[0] == three[1] && three[1] == three[2]) return true;
            // 顺子：三个连续（不要求顺序——排序后两两差 1）
            var sorted = new List<int>(three);
            sorted.Sort();
            return sorted[2] - sorted[1] == 1 && sorted[1] - sorted[0] == 1;
        }
    }

    /// <summary>
    /// 通用障碍物格数据（2026-08-20 决策：轻量 + 预留扩展点——墙体现在只有点数，未来障碍物可加字段；
    /// 决策依据：决策记录_牌数据结构与玩法语义_20260820.md——不重构 Obstacles，对象值表 + 统一 IsBlocked 入口）。
    /// </summary>
    [Serializable]
    public class ObstacleData
    {
        public int value; // 麻将墙体：点数（破坏时填牌山）；未来障碍物扩展字段

        public ObstacleData(int value)
        {
            this.value = value;
        }
    }
}
