using TheLaw.Core;

namespace TheLaw.UI
{
    /// <summary>
    /// UI 统一碰撞音助手（2026-08-24 音频挂点方案——UI/事件关全部复用 SfxMahjongTile 一套，音量轻）。
    /// 覆盖：按钮/面板开关/确认取消/遗物获得/编辑拖放替换撤销/三选一面板/构筑加减牌/Tooltip。
    /// 纯表现层：只播放音效，不触碰规则/数据/布局。
    /// 防重叠（2026-08-24）：PlaySFX 带 restartIfPlaying 标记——同种碰撞音在播时停旧重播，连点/同帧不叠音。
    /// </summary>
    public static class UiSfx
    {
        /// <summary>播放 UI 碰撞音（默认轻音量；个别场景可传参微调）。</summary>
        public static void Play(float volumeScale = 0.4f)
        {
            AudioManager.Instance.PlaySFX(AudioRefs.SfxMahjongTile, volumeScale, 1f, restartIfPlaying: true); // 防重叠：同种碰撞音在播时停旧重播
        }
    }
}
