using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TheLaw.Core;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// 开场剧情导入工具（前端 UI 层）：读取 Assets/Data/Configs/story_opening.json 并校验/透传。
    /// 菜单：工具 → 导入开场剧情
    /// 数据源：story_opening.json 由 Assets/test/parse_opening_story.py 从 Assets/test/docs/开场剧情.docx 解析生成。
    /// 现状（2026-08-24）：开场剧情尚无运行时加载器与 SO 类型（剧情面板挂点待前端）；
    /// 按《配置资产协作公约》Assets/Settings 下 .asset 由后端单一生成——本工具不生成资产，
    /// 只做读取/校验/汇总（"直接透传"）。前端剧情面板就绪后可直接读本 JSON，或由后端按同一 JSON 生成 SO。
    /// </summary>
    public static class StoryOpeningImporter
    {
        private const string JsonPath = "Assets/Data/Configs/story_opening.json";
        private const int ExpectedBeats = 19; // 人工核对口径：剧情节拍数（仅提示，不强制断言）

        private static readonly HashSet<string> Speakers = new HashSet<string> { "？？？", "Xeon", "测试员" };
        private static readonly HashSet<string> XeonDiffs = new HashSet<string> { "凝重", "坏笑", "常态", "惊讶", "恼火", "404" };
        private static readonly HashSet<string> StorySfx = new HashSet<string>
        {
            AudioRefs.SfxStoryWallBreak,
            AudioRefs.SfxStoryScrape,
            AudioRefs.SfxStoryStatic,
        };

        [MenuItem("工具/导入开场剧情")]
        public static void Import()
        {
            if (!File.Exists(JsonPath))
            {
                Debug.LogError($"[开场剧情导入] 未找到 {JsonPath}——请先运行 python Assets/test/parse_opening_story.py");
                return;
            }

            var dto = JsonConvert.DeserializeObject<StoryOpeningJson>(File.ReadAllText(JsonPath));
            if (dto == null || dto.entries == null || dto.entries.Count == 0)
            {
                Debug.LogError($"[开场剧情导入] {JsonPath} 内容为空或 JSON 结构非法（预期 {{ entries: [...] }}）");
                return;
            }

            int errors = Validate(dto.entries);
            Report(dto.entries);

            if (dto.entries.Count != ExpectedBeats)
            {
                Debug.LogWarning($"[开场剧情导入] 条目 {dto.entries.Count} 条 ≠ 人工核对口径 {ExpectedBeats} 拍：" +
                    "本 JSON 按“每句一条”输出（部分节拍含 2 条旁白段），19 拍映射见转换脚本运行输出/交接汇报——请人工核对。");
            }

            if (errors == 0)
            {
                Debug.Log($"[开场剧情导入] 校验通过（{dto.entries.Count} 条）——JSON 已就绪，可直接透传给前端剧情面板");
            }
            else
            {
                Debug.LogError($"[开场剧情导入] 校验失败：{errors} 处错误，见上方日志");
            }
        }

        private static int Validate(List<StoryLine> entries)
        {
            int errors = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                StoryLine e = entries[i];
                int no = i + 1;

                if (e.type != "dialogue" && e.type != "narration")
                {
                    LogErr(ref errors, no, $"type 非法：{e.type ?? "<null>"}");
                    continue;
                }
                if (e.type == "dialogue")
                {
                    if (string.IsNullOrEmpty(e.speaker) || !Speakers.Contains(e.speaker))
                        LogErr(ref errors, no, $"对话 speaker 非法：{e.speaker ?? "<null>"}");
                }
                else if (!string.IsNullOrEmpty(e.speaker))
                {
                    LogErr(ref errors, no, $"旁白不应带 speaker：{e.speaker}");
                }
                if (string.IsNullOrEmpty(e.text))
                {
                    LogErr(ref errors, no, "text 为空");
                }
                else
                {
                    if (e.text.Contains("这里加入") || e.text.Contains("切换至下一句时停止"))
                        LogErr(ref errors, no, $"text 未剥离加粗提示：{e.text}");
                }

                if (e.cue != null)
                {
                    if (!string.IsNullOrEmpty(e.cue.xeonDiff) && !XeonDiffs.Contains(e.cue.xeonDiff))
                        LogErr(ref errors, no, $"xeonDiff 非法：{e.cue.xeonDiff}");
                    if (!string.IsNullOrEmpty(e.cue.sfx) && !StorySfx.Contains(e.cue.sfx))
                        LogErr(ref errors, no, $"sfx 非法：{e.cue.sfx}（预期 AudioRefs.SfxStory* 常量）");
                }
            }
            return errors;
        }

        private static void LogErr(ref int errors, int no, string msg)
        {
            errors++;
            Debug.LogError($"[开场剧情导入] 第 {no} 条：{msg}");
        }

        private static void Report(List<StoryLine> entries)
        {
            int dia = 0, nar = 0, showXeon = 0, showTester = 0, shake = 0, bgOn = 0, bgOff = 0;
            var diffs = new Dictionary<string, int>();
            var sfx = new Dictionary<string, int>();
            var sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                StoryLine e = entries[i];
                if (e.type == "dialogue") dia++; else nar++;
                if (e.cue != null)
                {
                    if (e.cue.bg == true) bgOn++;
                    if (e.cue.bg == false) bgOff++;
                    if (e.cue.showXeon == true) showXeon++;
                    if (e.cue.showTester == true) showTester++;
                    if (e.cue.shake == true) shake++;
                    if (!string.IsNullOrEmpty(e.cue.xeonDiff))
                        diffs[e.cue.xeonDiff] = diffs.TryGetValue(e.cue.xeonDiff, out var v) ? v + 1 : 1;
                    if (!string.IsNullOrEmpty(e.cue.sfx))
                        sfx[e.cue.sfx] = sfx.TryGetValue(e.cue.sfx, out var s) ? s + 1 : 1;
                }
                string head = e.text != null && e.text.Length > 16 ? e.text.Substring(0, 16) + "…" : e.text ?? "";
                sb.AppendLine($"  {i + 1,2}. [{e.type}] {(e.speaker ?? "").PadRight(3)} {head}  cue={JsonConvert.SerializeObject(e.cue ?? new StoryCue())}");
            }

            Debug.Log($"[开场剧情导入] 汇总：{entries.Count} 条（对话 {dia} / 旁白 {nar}）" +
                $" | cue: bgOn {bgOn} / bgOff {bgOff} / showXeon {showXeon} / showTester {showTester} / shake {shake}" +
                $" | 差分 {string.Join("、", diffs.Select(kv => $"{kv.Key}x{kv.Value}"))}" +
                $" | 音效 {string.Join("、", sfx.Select(kv => $"{kv.Key}x{kv.Value}"))}");
            Debug.Log($"[开场剧情导入] 明细：\n{sb}");
        }

        // ===== JSON DTO（与 story_opening.json 一一对应；Newtonsoft 大小写不敏感）=====
        private class StoryOpeningJson { public List<StoryLine> entries; }
        private class StoryLine
        {
            public string type;
            public string speaker; // 仅 dialogue
            public string text;
            public StoryCue cue;
        }
        private class StoryCue
        {
            public bool? bg;
            public bool? showXeon;
            public bool? showTester;
            public string xeonDiff;
            public bool? shake;
            public string sfx;
        }
    }
}
