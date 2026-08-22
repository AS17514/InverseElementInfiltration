using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>通用 UI 组件统一创建入口。调用方负责 Addressables 模板生命周期。</summary>
    public static class UIComponentFactory
    {
        public static HandCardView CreateHandCard(GameObject template, Transform parent, HandCardViewData data)
        {
            var go = Object.Instantiate(template, parent);
            var view = go.GetComponent<HandCardView>();
            if (view == null) view = go.AddComponent<HandCardView>();
            view.Bind(data);
            return view;
        }

        public static PieceCardView CreatePieceCard(GameObject template, Transform parent, PieceCardViewData data,
            GameObject programIconTemplate)
        {
            var go = Object.Instantiate(template, parent);
            var view = go.GetComponent<PieceCardView>();
            if (view == null) view = go.AddComponent<PieceCardView>();
            view.Bind(data, programIconTemplate);
            return view;
        }

        public static ProgramIconView CreateProgramIcon(GameObject template, Transform parent, ProgramIconViewData data)
        {
            var go = Object.Instantiate(template, parent);
            var view = go.GetComponent<ProgramIconView>();
            if (view == null) view = go.AddComponent<ProgramIconView>();
            if (data != null) view.Bind(data);
            return view;
        }

        public static ProgramCardView CreateProgramCard(GameObject template, Transform parent, ProgramCardViewData data)
        {
            var go = Object.Instantiate(template, parent);
            var view = go.GetComponent<ProgramCardView>();
            if (view == null) view = go.AddComponent<ProgramCardView>();
            view.Bind(data);
            return view;
        }

        public static EventOptionView CreateEventOption(GameObject template, Transform parent, EventOptionViewData data,
            System.Action onClick)
        {
            var go = Object.Instantiate(template, parent);
            var view = go.GetComponent<EventOptionView>();
            if (view == null) view = go.AddComponent<EventOptionView>();
            view.Bind(data, onClick);
            return view;
        }
    }
}
