using System.Collections.Generic;
using DG.Tweening;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 结算面板（overlay 模态——后端收尾链的 UI 层）：
    /// 监听 StateChanged(GameOver + Side winner) → 【立即快照】胜负/得分/波次（1 帧后 Reset 清空 GameState，确认时再读会丢数据）
    /// → 自身显隐显示（遮罩覆盖下层——不走 UIManager 栈：BattlePanel/EventPanel 均直接 Show 不在栈，PushPanel/PopPanel 会错误露出 MainMenu）
    /// → 按任意键（键盘/鼠标）→ 关闭露出下层（胜利=战斗/事件界面保持 active / 失败=MainMenu 已在其下显示）。
    /// 交互：确认只关面板，不触发任何后端逻辑（推进决策权在规则层）。
    /// 显示：Txt_BattleResult（测试通过/失败 + 颜色 00FF2A/E10000）、Txt_Stats（右对齐多行）、Txt_Tip（"按任意键继续"闪动）。
    /// </summary>
    public class BattleResultPanel : PanelBase
    {
        public override string Key => "BattleResult";

        private GameState _state;

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

        /// <summary>结算面板是否正在显示（Bootstrap 收尾判断用——失败时 BackToMainMenu 不覆盖结算面板）。</summary>
        public bool IsShowing => _hasResult && gameObject.activeSelf;

        private Tween _tipTween; // 提示闪动

        public void Init(GameState state)
        {
            _state = state;
        }

        private void Awake()
        {
            _resultText = transform.Find("Txt_BattleResult")?.GetComponent<TMP_Text>();
            _statsText = transform.Find("Txt_Stats")?.GetComponent<TMP_Text>();
            _tipText = transform.Find("Txt_Tip")?.GetComponent<TMP_Text>();
            EventCenter.Instance.AddEventListener(GameEvent.StateChanged, OnStateChanged);
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

        /// <summary>战斗结算信号（EndBattle 落账完成时同步发）：立即快照（防 1 帧后 Reset 清空）+ 显示。</summary>
        void OnStateChanged(object data)
        {
            if (_state == null || _state.Phase != BattlePhase.GameOver) return;
            if (!(data is Side winner)) return;
            // ⚠️ 快照时机：收到信号立即读（BackToMainMenu 延后 1 帧 Reset——确认时再读会丢数据）
            _victory = winner == Side.Player;
            _playerScore = _state.PlayerScore;
            _enemyScore = _state.EnemyScore;
            _waveScores = new List<int>(_state.WaveScores);
            _hasResult = true;
            FillAndShow();
        }

        /// <summary>填充内容并显示（遮罩盖下层——关闭时露出）。</summary>
        void FillAndShow()
        {
            if (!_hasResult) return;
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
            gameObject.SetActive(true);
        }

        void Update()
        {
            // 按任意键：键盘任意键 + 鼠标点击 → 关闭（确认只关面板——不触发后端逻辑；下层界面本来就 active）
            if (!_hasResult || !gameObject.activeSelf) return;
            if (Input.anyKeyDown || Input.GetMouseButtonDown(0))
            {
                gameObject.SetActive(false); // 关闭露出下层（胜利=战斗/事件界面 / 失败=MainMenu）
                _hasResult = false; // 防重复触发（同帧多次输入）
                OnHide();
            }
        }
    }
}
