namespace TheLaw.Core
{
    /// <summary>
    /// 音频资源地址常量（Addressables 地址）——统一入口，调用方用常量避免魔法字符串。
    /// 资源放 Assets/Audio/BGM|SFX 并进 Addressables（工具「工具/音频进 Addressables」）后即可发声；缺失只 LogWarning。
    /// 地址规则 = 分类大写/资源名（BGM/menu、SFX/move）——与「音频命名规范-给音频.md」一致。
    /// 播放 API：AudioManager.PlaySFX(地址, volumeScale=1, pitch=1)——pitch 可用于麻将"音高随点数"（如 0.8~1.6）。
    /// </summary>
    public static class AudioRefs
    {
        // ===== BGM（2026-08-23：4 首已交付——bgm_menu/battle/event/result；地址 = BGM/文件名（工具「工具/音频进 Addressables」注册））=====
        public const string BgmMenu = "BGM/menu";   // 主菜单（挂点已有：Bootstrap 进主菜单）
        public const string BgmBattle = "BGM/battle"; // 战斗（挂点已有：Bootstrap 进战斗）
        public const string BgmEvent = "BGM/event";  // 事件关（能力/编辑/构筑——挂点待前端：事件关打开时切换，战斗开始回切 battle）
        public const string BgmResult = "BGM/result"; // 战斗结算（P3——挂点待前端：结算面板展示时播放；失败路径停战斗曲即可）

        // ===== SFX：操作反馈（P0）=====
        public const string SfxMove = "SFX/move";       // 棋子移动/落地（A1）
        public const string SfxDeploy = "SFX/deploy";   // 部署落子（A2）
        public const string SfxPromote = "SFX/promote"; // 升变替换/进化（A3——同时覆盖敌方升变闪光 C4）
        public const string SfxDraw = "SFX/draw";       // 抽牌（A4——首回合抽4/1AP抽1）＋复用：属性复制牌获得

        // ===== SFX：核心反馈（P0——攻击按方式分发 5 种，前端 BattleController 按 AttackMode 选地址）=====
        public const string SfxAttackMelee = "SFX/attack_melee";       // 近战（B1-melee）
        public const string SfxAttackMeleeAoe = "SFX/attack_melee_aoe"; // 近战群攻（B1-aoe）
        public const string SfxAttackDirect = "SFX/attack_direct";     // 直射（B1-direct）
        public const string SfxAttackArcing = "SFX/attack_arcing";     // 抛射（B1-arcing）
        public const string SfxAttackSpell = "SFX/attack_spell";       // 法术（B1-spell）
        public const string SfxHit = "SFX/hit";       // 命中/伤害（B2——含友伤）
        public const string SfxDeath = "SFX/death";   // 击杀/承伤归零（B3）

        // ===== SFX：重点提示 =====
        public const string SfxShield = "SFX/shield";         // 护盾抵挡"铛"（C1，P1——与掉血区分）
        public const string SfxFreeAction = "SFX/free_action"; // 免费行动获得/发动"叮"（C7，P3）
        public const string SfxScore = "SFX/score";           // 回合末得分结算入账（C10，P3——回合末一次性，非逐击杀；第 1 关无结算）

        // ===== SFX：麻将 + UI 统一（P1——一套复用全部）=====
        public const string SfxMahjongTile = "SFX/mahjong_tile"; // 麻将 M1-M8 全部（打出/破坏/牌山[音高随点数]/刻顺/番数/摸切/雀头）＋ UI/事件 U1-U9（按钮/面板/确认取消/遗物获得/编辑拖放替换撤销/三选一/构筑/Tooltip）统一复用

        // ===== SFX：其他 =====
        public const string SfxDeathUi = "SFX/death_ui"; // 2026-08-23 由音频交付（原名"sfx_death ui"含空格，已改名合规）；用途已确认（2026-08-23 设计）：**玩家死亡提示音**（我方判负/玩家死亡时播放——挂点待前端）

        // ===== SFX：开场剧情（2026-08-23 新增交付——中文描述名已按规范改名；挂点待前端剧情面板/开场演出）=====
        public const string SfxStoryWallBreak = "SFX/story_wall_break"; // 开场剧情·墙壁碎裂声（原"墙壁碎裂声.mp3"）
        public const string SfxStoryScrape = "SFX/story_scrape";       // 开场剧情·摩擦的沙沙声（原"摩擦的沙沙声.mp3"）
        public const string SfxStoryStatic = "SFX/story_static";       // 开场剧情·雪花屏声（原"雪花屏声.mp3"）

        // ===== 遗留兼容（2026-08-23 扩展前旧常量）=====
        public const string SfxAttack = "SFX/attack";   // ⚠️ 旧统一攻击音——资源未交付；前端接入 5 种分发后不再使用（保留防编译破坏）
        public const string SfxUiClick = "SFX/ui_click"; // ⚠️ 旧 UI 占位——无独立资源；UI 音统一用 SfxMahjongTile
    }
}