using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>
    /// 单场战斗棋子视觉注册表。按 pieceId 管理，禁止表现层依赖全场景名称搜索。
    /// </summary>
    internal sealed class BattleViewRegistry
    {
        private readonly Dictionary<int, GameObject> _views = new Dictionary<int, GameObject>();

        public GameObject Get(int pieceId)
        {
            if (_views.TryGetValue(pieceId, out var view) && view != null) return view;
            _views.Remove(pieceId);
            return null;
        }

        public void Register(int pieceId, GameObject view)
        {
            if (pieceId < 0 || view == null) return;
            _views[pieceId] = view;
        }

        public void Remove(int pieceId)
        {
            _views.Remove(pieceId);
        }

        public void DestroyAll()
        {
            foreach (var view in _views.Values)
            {
                if (view == null) continue;
                DOTween.Kill(view.transform);
                var portrait = view.transform.Find("Portrait")?.GetComponent<SpriteRenderer>();
                if (portrait != null && portrait.material != null) DOTween.Kill(portrait.material);
                Object.Destroy(view);
            }
            _views.Clear();
        }
    }
}
