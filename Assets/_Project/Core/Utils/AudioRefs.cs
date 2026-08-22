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
        // ===== BGM（2026-08-23：BGM 资源未交付——暂挂起；仅保留已启用的两首）=====
        public const string BgmMenu = "BGM/menu";   // 主菜单
        public const string BgmBattle = "BGM/battle"; // 战斗

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
        public const string SfxDeathUi = "SFX/death_ui"; // 2026-08-23 由音频交付（原名"sfx_death ui"含空格，已改名合规）；**用途待音频同学确认**（未列入需求单，先建常量备用）

        // ===== 遗留兼容（2026-08-23 扩展前旧常量）=====
        public const string SfxAttack = "SFX/attack";   // ⚠️ 旧统一攻击音——资源未交付；前端接入 5 种分发后不再使用（保留防编译破坏）
        public const string SfxUiClick = "SFX/ui_click"; // ⚠️ 旧 UI 占位——无独立资源；UI 音统一用 SfxMahjongTile
    }
}