using System;
using System.Collections.Generic;
using DG.Tweening;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

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
            PieceType type = _state != null ? _state.GetEffectiveType(def.Id) : def.pieceType;
            int value = _state != null ? _state.GetEffectiveValue(def.Id) : def.value;
            var bg = card.GetComponent<Image>();
            if (bg != null) bg.color = CardTypeColors.For(type);

            // Piece_Handcard uses Img_Info* names. Old Img_Piece* lookup left defaults visible.
            var name = FindText(card, "Txt_InfoName");
            if (name != null) name.text = VerticalName(def.displayName);
            // 价值节点已有唯一 TMP 子对象；类型和占格的表现尚未定稿，暂不处理。
            var valueText = FindText(card, "Img_InfoValue");
            if (valueText != null) valueText.text = value.ToString();
            var portrait = FindDeep(card, "Img_InfoPortrait")?.GetComponent<Image>();
            if (portrait != null) portrait.color = CardTypeColors.For(type);

            List<Template> slots = null;
            if (_state != null && _state.TryGetCurrentProgram(def.Id, out var edited)) slots = edited;
            else if (def.programSet != null && def.programSet.Count > 0) slots = def.programSet[0].slots;
            var programRoot = FindDeep(card, "Grp_InfoProgram");
            var descRoot = FindDeep(card, "Grp_ProgramDesc");
            for (int i = 0; i < 4; i++)
            {
                bool show = slots != null && i < slots.Count && slots[i] != null;
                var slotImage = FindDeep(programRoot, $"Img_InfoProgram{i + 1}") ?? FindDeep(card, $"Img_InfoProgram{i + 1}");
                if (slotImage != null)
                {
                    slotImage.gameObject.SetActive(show);
                    var txt = slotImage.GetComponentInChildren<TMP_Text>(true);
                    if (txt != null) txt.text = show ? SlotTypeChar(slots[i]) : "";
                }
                var desc = FindDeep(descRoot, $"Txt_InfoProgram{i + 1}Desc") ?? FindDeep(card, $"Txt_InfoProgram{i + 1}Desc");
                if (desc != null)
                {
                    desc.gameObject.SetActive(show);
                    var txt = desc.GetComponent<TMP_Text>();
                    if (txt != null) txt.text = show ? SlotDescription(slots[i]) : "";
                }
            }
        }

        private static string SlotDescription(Template slot)
        {
            if (slot == null) return "";
            var fromTable = SlotDescTable.Get(slot);
            if (!string.IsNullOrEmpty(fromTable)) return fromTable;
            switch (SlotTypeChar(slot))
            {
                case "移": return "移：移动";
                case "攻": return "攻：攻击";
                case "效": return "效：被动效果";
                default: return "跳：跳过";
            }
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
            _locked = true;
            foreach (var card in _cards) if (card != null) card.gameObject.SetActive(false);
            OnCandidateConfirmed?.Invoke(defId);
        }

        private static TMP_Text FindText(Transform root, string node)
        {
            return FindDeep(root, node)?.GetComponentInChildren<TMP_Text>(true);
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

        private static string VerticalName(string name) => string.Join("\n", name.ToCharArray());

        private static string SlotTypeChar(Template t)
        {
            switch (t)
            {
                case MoveTemplate: return "移";
                case AttackTemplate: return "攻";
                case EffectTemplate: return "效";
                case SkipTemplate: return "跳";
                default: return "";
            }
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
