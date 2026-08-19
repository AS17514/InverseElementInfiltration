namespace TheLaw.Core
{
    /// <summary>
    /// 音频资源地址常量（Addressables 地址）——统一入口，调用方用常量避免魔法字符串。
    /// 资源放 Assets/Audio/BGM|SFX 并进 Addressables 后即可发声；缺失只 LogWarning。
    /// </summary>
    public static class AudioRefs
    {
        // ===== BGM =====
        public const string BgmMenu = "BGM/menu";   // 主菜单
        public const string BgmBattle = "BGM/battle"; // 战斗

        // ===== SFX：战斗 =====
        public const string SfxMove = "SFX/move";
        public const string SfxAttack = "SFX/attack";
        public const string SfxHit = "SFX/hit";
        public const string SfxDeath = "SFX/death";
        public const string SfxDeploy = "SFX/deploy";

        // ===== SFX：UI（备用，权重低）=====
        public const string SfxUiClick = "SFX/ui_click";
    }
}
