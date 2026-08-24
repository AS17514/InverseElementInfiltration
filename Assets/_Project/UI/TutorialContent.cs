using System.Collections.Generic;

namespace TheLaw.UI
{
    /// <summary>教程步骤：一条角色台词 + 可选高亮 + 布局预设。</summary>
    public class TutorialStep
    {
        public string id;              // 步骤 id（诊断）
        public string speaker;         // 说话人（"Xeon"/"3号"）
        public string portraitKey;     // 立绘 key（差分）：xeon_normal / xeon_surprised / xeon_smirk / no3
        public string text;            // 台词（\\n 换行）
        public List<string> highlightTargets; // 高亮目标（UI 节点路径 / 场景对象名；"@xxx"=教程管理器动态解析；空=不高亮）
        public float highlightPadding = 26f; // 框与目标间距（像素）
        public string layout = "bottomCenter"; // 布局预设：bottomLeft/bottomCenter/bottomRight/topLeft/topCenter/topRight/leftMid/rightMid
        public string waitEvent;       // 等待该事件（GameEvent 名）后才展示本步；空=直接展示
    }

    /// <summary>教程序列：一组步骤 + 去重 id。</summary>
    public class TutorialSequence
    {
        public string id;              // 序列 id（onceKey 去重）
        public List<TutorialStep> steps = new List<TutorialStep>();
    }

    /// <summary>
    /// 教程内容（文案源：Assets/test/docs/新手引导顺序.docx，策划未定稿——占位符 ??? 处待策划补全）。
    /// 暂存代码；文案定稿后可迁 JSON。仅 UI 层数据，不触后端配置。
    /// </summary>
    public static class TutorialContent
    {
        public static readonly List<TutorialSequence> All = new List<TutorialSequence>
        {
            new TutorialSequence
            {
                id = "event_intro",
                steps = new List<TutorialStep>
                {
                    new TutorialStep
                    {
                        id = "event_intro_1",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "「伊甸」现在正在开发一款战棋的游戏，测试员你需要做的就是用你的想法，来侵蚀这个无聊的东西。看到了吗，我前面的就是刚刚破解出来的漏洞。",
                        highlightTargets = new List<string> { "EventPanel" },
                    },
                    new TutorialStep
                    {
                        id = "event_intro_2",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "这就是「伊甸」进行编辑的界面了，测试员，试着选择一个你想要的效果吧，这些效果会持续到整个游戏结束，要谨慎一点哦。",
                        highlightTargets = new List<string> { "EventPanel" },
                    },
                    new TutorialStep
                    {
                        id = "event_intro_3",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "这就是遗物，遗物是……（文案待策划补全）",
                        waitEvent = "RelicObtained",
                        highlightTargets = new List<string> { "RelicObtained" }, // TODO: 高亮遗物栏节点名待李毕确认
                    },
                },
            },

            new TutorialSequence
            {
                id = "edit_intro",
                steps = new List<TutorialStep>
                {
                    new TutorialStep
                    {
                        id = "edit_intro_1",
                        speaker = "3号",
                        portraitKey = "no3",
                        layout = "bottomCenter",
                        text = "哦，你来了啊，测试员。我是3号，棋子编辑区的机器人，废话就免了，这下面就是棋子的编辑现场。",
                    },
                    new TutorialStep
                    {
                        id = "edit_intro_2",
                        speaker = "3号",
                        portraitKey = "no3",
                        layout = "bottomCenter",
                        text = "好了，接下来的场景你会见到许多次，先来看看我面前的屏幕吧，我们的敌人有手段，你也同样拥有。",
                    },
                    new TutorialStep
                    {
                        id = "edit_intro_3",
                        speaker = "3号",
                        portraitKey = "no3",
                        layout = "bottomCenter",
                        text = "在这个界面上有一些模块，你可以把这些模块拖动到棋子的空缺处。但要记得，棋子的模块一定要符合棋子本身的能力，用常识来想，你也不可能让一个拿小刀的士兵去开炮吧。",
                        highlightTargets = new List<string> { "PieceEditPanel" },
                    },
                    new TutorialStep
                    {
                        id = "edit_intro_4",
                        speaker = "3号",
                        portraitKey = "no3",
                        layout = "bottomCenter",
                        text = "你所做出的改动会跟随往后的所有过程，个人建议你的改动还是统一一点。与之相对的，你拥有的改变敌人也会拥有，所以尽量往你擅长的方面去改动吧。",
                    },
                },
            },

            new TutorialSequence
            {
                id = "deck_intro",
                steps = new List<TutorialStep>
                {
                    new TutorialStep
                    {
                        id = "deck_intro_1",
                        speaker = "Xeon",
                        portraitKey = "xeon_surprised",
                        layout = "bottomCenter",
                        text = "测试员，你出来了啊，接下来就是要去面对「伊甸」在「牧场」开发的第一步——「白模」了。",
                    },
                    new TutorialStep
                    {
                        id = "deck_intro_2",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "进去之前，你要编辑一下你自己的牌组。测试员你的牌组最多才能有12张，「伊甸」毕竟还是一个巨型企业，不会让你带这么多棋子进去的。",
                    },
                    new TutorialStep
                    {
                        id = "deck_intro_3",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "在牌组构筑界面，所有的棋子都被印到一张卡牌上，点击卡牌后你可以看到这个棋子的具体技能和数值。点击卡牌后就可以选择上去，再次点击会重复添加，在已选择界面点击则会回退你的选择。",
                        highlightTargets = new List<string> { "DeckBuildPanel" },
                    },
                    new TutorialStep
                    {
                        id = "deck_intro_4",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "你会发现下方有一些红色和蓝色的卡牌选择不了，这是因为他们需要合成。你需要先选择特定的绿色棋子作为基础，才可以选择这些棋子；在局内你可以把这些后选的棋子覆盖到他们适合的攻击方式的棋子上，这样叫做「升变」。",
                        highlightTargets = new List<string> { "DeckBuildPanel" },
                    },
                    new TutorialStep
                    {
                        id = "deck_intro_5",
                        speaker = "Xeon",
                        portraitKey = "xeon_smirk",
                        layout = "bottomCenter",
                        text = "虽然这些升变棋子很强大，但是相对的你的行动数就会受到限制，请认真做出抉择吧。",
                    },
                },
            },

            new TutorialSequence
            {
                id = "battle_intro",
                steps = new List<TutorialStep>
                {
                    new TutorialStep
                    {
                        id = "battle_intro_1",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "终于到棋盘了，现在还只是白模阶段，测试员不用太担心，我们的对手还不太强。",
                    },
                    new TutorialStep
                    {
                        id = "battle_intro_2",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "好了测试员，你的面前应该已经出现了一个棋盘，在你的视野下方有一排卡片，这是你的手牌，同时也是你的棋子。你可以把棋子拖向棋盘靠近你的基础为两排的任意位置，这样就可以部署棋子啦。",
                        highlightTargets = new List<string> { "@handArea", "@frontRows" },
                    },
                    new TutorialStep
                    {
                        id = "battle_intro_3",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "接着测试员你可以试试看点击你已经部署的棋子，在你的右侧会显示这颗棋子的具体信息。看到另一边闪着红光的棋子了吗，你也可以点击他们查看敌方的棋子信息。",
                        highlightTargets = new List<string> { "@firstPlayerPiece" },
                    },
                    new TutorialStep
                    {
                        id = "battle_intro_4",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "棋盘左侧是这次棋局的规则和状态，测试员可以随时查看在这个棋盘上的基础规则、双方得分以及测试员已经获得的能力。",
                        highlightTargets = new List<string> { "@leftInfo" },
                    },
                    new TutorialStep
                    {
                        id = "battle_intro_5",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "左下角和右下角测试员可以查看本回合还剩下多少行动点、牌组和重新抽牌。",
                        highlightTargets = new List<string> { "@bottomLeft", "@bottomRight" },
                    },
                    new TutorialStep
                    {
                        id = "battle_intro_6",
                        speaker = "Xeon",
                        portraitKey = "xeon_normal",
                        layout = "bottomCenter",
                        text = "对了对了，还有最重要的一点：双方的棋子攻击都会不分敌我，测试员在进行群攻或是直射时要记得注意当前的局势哦。",
                    },
                },
            },
        };

        public static TutorialSequence Find(string id)
        {
            foreach (var s in All)
            {
                if (s.id == id) return s;
            }
            return null;
        }
    }
}
