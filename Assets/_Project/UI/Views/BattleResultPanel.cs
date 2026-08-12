using System.Collections.Generic;
using DG.Tweening;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TheLaw.UI
{
    /// <summary>
    /// 结算面板（overlay 模态——后端收尾链的 UI 层）：
    /// 监听 StateChanged(GameOver + Side winner) → 【立即快照】胜负/得分/波次（1 帧后 Reset 清空 GameState，确认时再读会丢数据）
    /// → PushOverlay 覆盖显示（不隐藏下层——收尾在面板下层完成）→ 按任意键（键盘/鼠标）→ PopOverlay 恢复下层。
    /// 交互：确认只关面板，不触发任何后端逻辑（推进决策权在规则层）。
    /// 显示：Txt_BattleResult（测试通过/失败 + 颜色 00FF2A/E10000）、Txt_Stats（右对齐多行）、Txt_Tip（“按任意键继续”闪动）。
    /// </summary>
    public class BattleResultPanel : PanelBase
    {
        public override string Key => "BattleResult";

        private GameState _state;
        private UIManager _uiManager; // 2026-08-12 架构重构：PushOverlay/PopOverlay

        // ====== 节点引用 ======
        private TMP_Text _resultText;  // Txt_BattleResult（胜负大字）
        private TMP_Text _statsText;   // Txt_Stats（统计多行，右对齐）
        private TMP_Text _tipText;     // Txt_Tip（"按任意键继续"——闪动）

        // ====== 快照（收到信号立即读——防 Reset 清空）======
        private bool _hasResult;
        private bool _victory;
        private int _playerScore;
        private int _enemyScore;
        private List<int> _waveScores = new List<int>();

        private Tween _tipTween; // 提示闪动

        /// <summary>玩家确认结算（PopOverlay 后触发）——Bootstrap 订阅：失败/通关时执行挂起的收尾（保持战斗场景直到确认）。</summary>
        public event System.Action OnConfirmed;

        public void Init(GameState state, UIManager uiManager)
        {
            _state = state;
            _uiManager = uiManager;
        }

        private void Awake()
        {
            // 节点在 Img_Bg/Img_BgBorder/ 下（二级）——用递归查找（Find 只找直接子级会 null）
            _resultText = FindDeep(transform, "Txt_BattleResult")?.GetComponent<TMP_Text>();
            _statsText = FindDeep(transform, "Txt_Stats")?.GetComponent<TMP_Text>();
            _tipText = FindDeep(transform, "Txt_Tip")?.GetComponent<TMP_Text>();
            EventCenter.Instance.AddEventListener(GameEvent.StateChanged, OnStateChanged);
        }

        /// <summary>递归按名查找（容错 prefab 层级嵌套）。</summary>
        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

        void OnDestroy()
        {
            EventCenter.Instance.RemoveEventListener(GameEvent.StateChanged, OnStateChanged);
            if (_tipTween != null) _tipTween.Kill();
        }

        protected override void OnShow() { } // 兼容 UIManager.ShowPanel 路径（当前直接 SetActive 显示）

        protected override void OnHide()
        {
            if (_tipTween != null)
            {
                _tipTween.Kill();
                _tipTween = null;
            }
        }

        /// <summary>统计文本（右对齐——数值在前标签在后）：我方/敌方得分 + 波次明细一行（12 波次1 | 12 波次2 | 13 波次3）。</summary>
        string BuildStats()
        {
            var lines = new List<string>();
            lines.Add($"{_playerScore} 我方得分");
            lines.Add($"{_enemyScore} 敌方得分");
            // 波次明细：横排一行 | 分隔（信息全且不占行数）
            if (_waveScores.Count > 0)
            {
                var waveParts = new List<string>();
                for (int i = 0; i < _waveScores.Count; i++)
                {
                    waveParts.Add($"{_waveScores[i]} 波次{i + 1}");
                }
                lines.Add(string.Join(" | ", waveParts));
            }
            return string.Join("\n", lines);
        }

        /// <summary>战斗结算信号（EndBattle 落账完成时同步发）：立即快照（防 1 帧后 Reset 清空）+ PushOverlay 显示。</summary>
        void OnStateChanged(object data)
        {
            if (_state == null || _state.Phase != BattlePhase.GameOver) return;
            if (!(data is Side winner)) return;
            if (_hasResult) return; // 防重（规则层 GameOver 幂等已兜底——防御性）
            // ⚠️ 快照时机：收到信号立即读（BackToMainMenu 延后 1 帧 Reset——确认时再读会丢数据）
            _victory = winner == Side.Player;
            _playerScore = _state.PlayerScore;
            _enemyScore = _state.EnemyScore;
            _waveScores = new List<int>(_state.WaveScores);
            _hasResult = true;
            FillContent();
            _uiManager?.PushOverlay(Key); // 覆盖显示（不隐藏下层——收尾/下层界面在面板之下）
        }

        /// <summary>填充内容（PushOverlay 前调用——Show 由 UIManager 负责）。</summary>
        void FillContent()
        {
            if (_resultText != null)
            {
                _resultText.text = _victory ? "测试通过" : "失败";
                // 胜利 00FF2A / 失败 E10000
                _resultText.color = _victory ? new Color(0f, 1f, 0.165f, 1f) : new Color(0.882f, 0f, 0f, 1f);
            }
            if (_statsText != null) _statsText.text = BuildStats();
            // Txt_Tip 闪动（alpha 循环）
            if (_tipText != null)
            {
                if (_tipTween != null) _tipTween.Kill();
                _tipText.alpha = 1f;
                _tipTween = DOTween.To(() => _tipText.alpha, a => _tipText.alpha = a, 0.25f, 0.6f)
                    .SetLoops(-1, LoopType.Yoyo)
                    .SetEase(Ease.InOutSine);
            }
        }

        void Update()
        {
            // 按任意键：键盘任意键 + 鼠标点击 → PopOverlay（确认只关面板——不触发后端逻辑；恢复下层）
            if (!_hasResult || !gameObject.activeSelf) return;
            // Input System 原生检测（项目 activeInputHandler=2——旧 UnityEngine.Input 可能不可用）
            bool keyDown = false;
            bool mouseDown = false;
            if (UnityEngine.InputSystem.Keyboard.current != null)
            {
                keyDown = Keyboard.current.anyKey.wasPressedThisFrame;
            }
            if (UnityEngine.InputSystem.Mouse.current != null)
            {
                mouseDown = Mouse.current.leftButton.wasPressedThisFrame;
            }
            if (keyDown || mouseDown)
            {
                _hasResult = false; // 先置 false 防同帧重复 Pop
                _uiManager?.PopOverlay(); // 恢复下层（胜利=下一节点 / 失败=战斗场景——收尾在确认后才执行）
                OnConfirmed?.Invoke(); // 通知 Bootstrap：失败/通关时执行挂起的收尾（确认前保持战斗场景）
            }
        }
    }
}
