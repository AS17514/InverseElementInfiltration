using System;
using System.Collections.Generic;
using DG.Tweening;
using TheLaw.Data;
using TheLaw.Gameplay;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TheLaw.UI
{
    /// <summary>编辑事件三选一面板：点击候选卡立即确认并进入唯一棋子编辑。</summary>
    public sealed class EditCandidatePanel : PanelBase
    {
        public override string Key => "EditCandidatePanel";
        public event Action<int> OnCandidateConfirmed;

        private GameState _state;
        private readonly List<Transform> _cards = new List<Transform>();
        private readonly List<Vector3> _baseScales = new List<Vector3>();
        private bool _locked;

        private void Awake()
        {
            // 2026-08-26 修复：prefab 内候选卡默认激活（未绑定的白卡）——实例化到绑定之间会露"白模"闪帧；
            // 实例化即隐藏，OnShow 绑定数据后再按候选数量激活。
            ResolveCards();
            for (int i = 0; i < _cards.Count; i++)
            {
                if (_cards[i] != null) _cards[i].gameObject.SetActive(false);
            }
        }

        public void Init(GameState state)
        {
            _state = state;
        }

        protected override void OnShow()
        {
            _locked = false;
            ResolveCards();
            RefreshCards();
        }

        protected override void OnHide()
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                if (_cards[i] == null) continue;
                DOTween.Kill(_cards[i]);
                _cards[i].localScale = i < _baseScales.Count ? _baseScales[i] : Vector3.one;
            }
        }

        private void ResolveCards()
        {
            _cards.Clear();
            _baseScales.Clear();
            var root = FindDeep(transform, "Grp_Candidates");
            if (root == null) return;
            foreach (Transform child in root)
            {
                if (child.name.StartsWith("Piece_Handcard", StringComparison.Ordinal))
                {
                    _cards.Add(child);
                    _baseScales.Add(child.localScale);
                }
            }
        }

        private void RefreshCards()
        {
            var candidates = _state != null && _state.EditCandidates != null
                ? _state.EditCandidates
                : new List<int>();
            for (int i = 0; i < _cards.Count; i++)
            {
                var card = _cards[i];
                if (card == null) continue;
                bool active = i < candidates.Count;
                card.gameObject.SetActive(active);
                if (!active) continue;
                var def = ConfigTable.Find<PieceDef>(candidates[i]);
                if (def == null)
                {
                    card.gameObject.SetActive(false);
                    continue;
                }
                BindCard(card, def);
                var click = card.GetComponent<EditCandidateCardClick>();
                if (click == null) click = card.gameObject.AddComponent<EditCandidateCardClick>();
                click.Bind(this, candidates[i]);
                DOTween.Kill(card);
                card.localScale = _baseScales[i];
            }
        }

        private void BindCard(Transform card, PieceDef def)
        {
            var type = _state != null ? _state.GetEffectiveType(def.Id) : def.pieceType;
            var value = _state != null ? _state.GetEffectiveValue(def.Id) : def.value;
            List<Template> program = null;
            if (_state != null && _state.TryGetCurrentProgram(def.Id, out var edited)) program = edited;
            else if (def.programSet != null && def.programSet.Count > 0) program = def.programSet[0].slots;

            var view = card.GetComponent<HandCardView>();
            if (view == null) view = card.gameObject.AddComponent<HandCardView>();
            view.Bind(PiecePresentationMapper.ToHandCard(def, type, value, program));
        }

        internal void Hover(Transform card, bool enter)
        {
            if (card == null) return;
            int index = _cards.IndexOf(card);
            var baseScale = index >= 0 && index < _baseScales.Count ? _baseScales[index] : Vector3.one;
            DOTween.Kill(card);
            var target = enter ? baseScale * 1.06f : baseScale;
            DOTween.To(() => card.localScale, v => card.localScale = v, target, 0.12f)
                .SetEase(Ease.OutQuad).SetTarget(card);
        }

        internal void ClickCandidate(int defId)
        {
            if (_locked || _state == null) return;
            UiSfx.Play(); // 编辑候选三选一确认碰撞音（2026-08-24 音频挂点方案）
            _locked = true;
            foreach (var card in _cards) if (card != null) card.gameObject.SetActive(false);
            OnCandidateConfirmed?.Invoke(defId);
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

    }

    internal sealed class EditCandidateCardClick : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        private EditCandidatePanel _panel;
        private int _defId;
        public void Bind(EditCandidatePanel panel, int defId) { _panel = panel; _defId = defId; }
        public void OnPointerClick(PointerEventData eventData) { if (eventData.button == PointerEventData.InputButton.Left) _panel?.ClickCandidate(_defId); }
        public void OnPointerEnter(PointerEventData eventData) { _panel?.Hover(transform, true); }
        public void OnPointerExit(PointerEventData eventData) { _panel?.Hover(transform, false); }
        private void OnDestroy() { DOTween.Kill(transform); }
    }
}
