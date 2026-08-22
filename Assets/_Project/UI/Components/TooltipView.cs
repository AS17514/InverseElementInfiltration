using TMPro;
using UnityEngine;

namespace TheLaw.UI
{
    /// <summary>TipPanel 的纯展示绑定。</summary>
    public sealed class TooltipView : MonoBehaviour
    {
        private TMP_Text _descriptionText;

        private void Awake()
        {
            CacheNodes();
        }

        public void Bind(TooltipViewData data)
        {
            CacheNodes();
            if (_descriptionText != null) _descriptionText.text = data?.Text ?? string.Empty;
        }

        private void CacheNodes()
        {
            if (_descriptionText == null)
            {
                foreach (var transform in GetComponentsInChildren<Transform>(true))
                {
                    if (transform.name != "Txt_Desc") continue;
                    _descriptionText = transform.GetComponent<TMP_Text>();
                    break;
                }
            }
        }
    }
}
