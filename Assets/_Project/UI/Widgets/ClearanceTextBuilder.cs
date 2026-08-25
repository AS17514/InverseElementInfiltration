using System.Collections.Generic;
using TheLaw.Data;
using TheLaw.Gameplay;

namespace TheLaw.UI
{
    /// <summary>
    /// 通关条件文案构建（2026-08-26 策划定稿版）：
    /// 数值全部来自 FloorConfig（count/groups/poolType/spawnShield/waveScoreTarget/targetScore/endCountdown）——配置驱动，免双份维护；
    /// 规则句/升变句为文案模板；配置缺失降级提示。
    /// 结构：Title = 关名 · 通关条件；Body 多行（胜利规则 / 升变规则 / 波次明细 / 得分目标）。
    /// </summary>
    public static class ClearanceTextBuilder
    {
        public static ClearanceViewData Build(GameState state)
        {
            if (state == null) return new ClearanceViewData("通关条件", "（状态未就绪）");
            var cfg = state.CurrentFloorConfig;
            string title = $"{Bootstrap.FloorDisplayName(state.CurrentFloor)} · 通关条件";
            var lines = new List<string>();
            if (cfg == null)
            {
                lines.Add("（本关配置缺失）");
                return new ClearanceViewData(title, string.Join("\n", lines));
            }

            lines.Add(VictoryLine(cfg));
            lines.Add("敌方升变：升变前一回合会有红色描边闪烁效果");

            if (state.CurrentFloor == 0)
            {
                // 白模：无波次明细（开局初始敌方 + 波次增援），补限定回合说明
                lines.Add("敌方按波次增援");
                if (cfg.waveDefs != null && cfg.waveDefs.Count > 0)
                {
                    var last = cfg.waveDefs[cfg.waveDefs.Count - 1];
                    if (last.endCountdown > 0) lines.Add($"最后一波出现后 {last.endCountdown} 回合内未全灭 → 判负");
                }
            }
            else
            {
                lines.AddRange(WaveLines(cfg));
                lines.AddRange(ScoreLines(cfg));
            }
            return new ClearanceViewData(title, string.Join("\n", lines));
        }

        static string VictoryLine(FloorConfig cfg)
        {
            switch (cfg.victoryRule)
            {
                case VictoryRule.ScoreTarget:
                    return cfg.targetScore > 0
                        ? $"胜利：限定回合内击败敌方所有棋子，或总得分达到 {cfg.targetScore}"
                        : "胜利：限定回合内击败敌方所有棋子";
                case VictoryRule.PerWaveScore:
                    return cfg.targetScore > 0
                        ? $"胜利：每个波次得分达标，或总得分达到 {cfg.targetScore}"
                        : "胜利：每个波次得分达标";
                case VictoryRule.Both:
                    return $"胜利：每个波次得分达标，且总得分达到 {cfg.targetScore}";
                default:
                    return "胜利：在限定回合内，击败敌方所有棋子";
            }
        }

        static IEnumerable<string> WaveLines(FloorConfig cfg)
        {
            if (cfg.waveDefs == null || cfg.waveDefs.Count == 0)
            {
                yield return "敌方波次：待配置";
                yield break;
            }
            yield return $"敌方波次（共 {cfg.waveDefs.Count} 波）：";
            for (int i = 0; i < cfg.waveDefs.Count; i++)
            {
                yield return $"第 {i + 1} 波：{DescribeWave(cfg.waveDefs[i])}";
            }
        }

        static string DescribeWave(WaveDef w)
        {
            var parts = new List<string>();
            if (w.groups != null && w.groups.Count > 0)
            {
                foreach (var g in w.groups)
                {
                    parts.Add($"{AreaName(g.deployArea)}随机 {g.count} 个{TypeName(g.poolType)}棋子");
                }
            }
            else
            {
                parts.Add($"部署区随机 {w.count} 个空格，生成 {w.count} 个{TypeName(w.poolType)}棋子");
            }
            string s = string.Join(" + ", parts);
            if (w.spawnShield > 0) s += $"（额外获得护盾 {w.spawnShield}）";
            return s;
        }

        static string AreaName(DeployArea area) => area == DeployArea.Midfield ? "非双方部署区" : "部署区";
        static string TypeName(PieceType t) => t == PieceType.Initial ? "初始" : "部署";

        static IEnumerable<string> ScoreLines(FloorConfig cfg)
        {
            if (cfg.waveDefs != null && cfg.waveDefs.Count > 0)
            {
                var parts = new List<string>();
                bool anyWaveTarget = false;
                for (int i = 0; i < cfg.waveDefs.Count; i++)
                {
                    if (cfg.waveDefs[i].waveScoreTarget > 0)
                    {
                        anyWaveTarget = true;
                        parts.Add($"第 {i + 1} 波 {cfg.waveDefs[i].waveScoreTarget}");
                    }
                }
                if (anyWaveTarget)
                {
                    yield return $"得分目标：{string.Join(" ｜ ", parts)}，总分 {cfg.targetScore}";
                    yield break;
                }
            }
            if (cfg.targetScore > 0) yield return $"得分目标：总分 {cfg.targetScore}";
        }
    }
}
