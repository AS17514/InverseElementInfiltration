using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;

namespace TheLaw.UI
{
    /// <summary>
    /// 行动经济 buff 前端同步桥（2026-08-24）：
    /// BuffDisplay 是只读查询器；已行动集（GameState.ActionEconomyActed）的两处变化——
    /// 玩家回合开始重置（BattleFlow.StartPlayerTurn）/ 棋子执行后被标记（ExecuteRequest 处理）——
    /// 后端均不发 BuffsChanged。本桥在 UI 层订阅现有事件并补发 BuffsChanged，
    /// 驱动 BattleController 刷新选中棋子的 buff 区（Txt_Other）。
    /// 只订阅既有事件，不新增后端接口；不改 BattleController。
    /// </summary>
    public static class ActionEconomyBuffSync
    {
        private static GameState _state;
        private static bool _subscribed;

        /// <summary>接线（Bootstrap.Awake 调用一次；进程内常驻）。</summary>
        public static void EnsureSubscribed(GameState state)
        {
            _state = state;
            if (_subscribed) return;
            _subscribed = true;
            EventCenter.Instance.AddEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
            EventCenter.Instance.AddEventListener(GameEvent.PresentationFinished, OnPresentationFinished);
            EventCenter.Instance.AddEventListener(GameEvent.RelicObtained, OnRelicObtained);
        }

        /// <summary>退订（Bootstrap.OnDestroy 防御性调用，与订阅对称）。</summary>
        public static void Shutdown()
        {
            if (!_subscribed) return;
            _subscribed = false;
            _state = null;
            EventCenter.Instance.RemoveEventListener(GameEvent.PhaseChanged, OnPhaseChanged);
            EventCenter.Instance.RemoveEventListener(GameEvent.PresentationFinished, OnPresentationFinished);
            EventCenter.Instance.RemoveEventListener(GameEvent.RelicObtained, OnRelicObtained);
        }

        // 玩家回合开始：已行动集已清空（StartPlayerTurn 先于 ChangePhase）→ 全体 buff 回态 A
        static void OnPhaseChanged(object data)
        {
            if (data is BattlePhase phase && phase == BattlePhase.PlayerTurn)
            {
                RefreshAllPlayerBuffs();
            }
        }

        // 行动表现收尾：执行请求处理时已把该棋子加入已行动集（状态先于表现）→ 补发刷新（态 A → 态 B）
        static void OnPresentationFinished(object data)
        {
            RefreshAllPlayerBuffs();
        }

        // 行动经济能力获得（RelicObtained 时 effects 已落账）→ 激活即刷新（无需等下一次表现/回合）
        static void OnRelicObtained(object data)
        {
            RefreshAllPlayerBuffs();
        }

        static void RefreshAllPlayerBuffs()
        {
            if (_state == null || !_state.ActionEconomyActive) return; // 未激活不空刷
            foreach (var piece in _state.Pieces.Values)
            {
                if (piece != null && piece.side == Side.Player)
                {
                    EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, piece.Id);
                }
            }
        }
    }
}
