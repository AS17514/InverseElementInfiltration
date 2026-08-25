using System;
using System.Collections;
using TheLaw.Core;
using TheLaw.Data;
using TheLaw.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace TheLaw.UI
{
    /// <summary>
    /// 数字选择面板（2026-08-27 能力交互：宝牌 1-9 / 出千 1-6）。
    /// 节点契约：Txt_Info（说明）/ Grp_Numbers（按钮容器）；按钮 = Btn_Number 预制体（子节点 "Text (TMP)" 写数字）。
    /// 用法（先 PushOverlay 激活面板再调）：ShowBaopaiPick → Resolver.SetBaopaiNumber(n)；
    /// ShowDiceRigPick → BattleFlow.OnDiceNumberSelected(n)。点背景 = 关闭（未选作废——不跨回合）。
    /// </summary>
    public class SelectNumberPanel : PanelBase
    {
        public override string Key => "SelectNumber";

        private UIManager _uiManager;
        private Resolver _resolver;
        private Func<BattleFlow> _flowProvider;
        private TMP_Text _infoText;     // Txt_Info
        private Transform _numbersRoot; // Grp_Numbers
        private GameObject _numberTemplate; // Btn_Number（Addressables 缓存）

        public void Init(UIManager uiManager, Resolver resolver, Func<BattleFlow> flowProvider)
        {
            _uiManager = uiManager;
            _resolver = resolver;
            _flowProvider = flowProvider;
        }

        private void Awake()
        {
            _infoText = FindDeep(transform, "Txt_Info")?.GetComponent<TMP_Text>();
            _numbersRoot = FindDeep(transform, "Grp_Numbers");
        }

        protected override bool CloseOnBgClick => true;

        protected override void OnBgClicked()
        {
            Close(); // 点背景 = 关闭（未选作废）
        }

        private void Close()
        {
            if (_uiManager != null) _uiManager.PopOverlay(Key);
            else gameObject.SetActive(false);
        }

        /// <summary>宝牌选数（1-9）：获得「宝牌」能力后调用 → 选择落账 SetBaopaiNumber（后端校验持有+1-9）。</summary>
        public void ShowBaopaiPick()
        {
            StartCoroutine(BuildAndShow("选择宝牌数字（1-9）——该数字对应价值的牌视为「宝牌」", 9, n =>
            {
                if (_resolver != null) _resolver.SetBaopaiNumber(n);
                Close();
            }));
        }

        /// <summary>出千选数（1-6）：投掷收到 StateChanged("dice-rig-select") 后调用 → OnDiceNumberSelected（不跨回合）。</summary>
        public void ShowDiceRigPick()
        {
            StartCoroutine(BuildAndShow("选择骰子点数（1-6）", 6, n =>
            {
                var flow = _flowProvider != null ? _flowProvider() : null;
                if (flow != null) flow.OnDiceNumberSelected(n);
                Close();
            }));
        }

        /// <summary>清空 Grp_Numbers → 按 1..max 生成 Btn_Number（写数字 + 接回调）；面板已激活后运行（协程依赖 active）。</summary>
        private IEnumerator BuildAndShow(string info, int max, Action<int> onPicked)
        {
            if (_infoText != null) _infoText.text = info ?? string.Empty;
            if (_numbersRoot != null)
            {
                for (int i = _numbersRoot.childCount - 1; i >= 0; i--)
                {
                    var child = _numbersRoot.GetChild(i);
                    if (child != null) Destroy(child.gameObject);
                }
            }
            if (_numberTemplate == null)
            {
                var handle = Addressables.LoadAssetAsync<GameObject>("Btn_Number");
                yield return handle;
                if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
                {
                    _numberTemplate = handle.Result;
                }
            }
            if (_numberTemplate == null || _numbersRoot == null)
            {
                Debug.LogWarning("[SelectNumber] Btn_Number 模板或 Grp_Numbers 缺失——无法生成数字按钮（面板内容为空）");
                yield break;
            }
            for (int n = 1; n <= max; n++)
            {
                var go = Instantiate(_numberTemplate, _numbersRoot);
                go.name = $"Btn_Number_{n}";
                var text = FindDeep(go.transform, "Text (TMP)")?.GetComponent<TMP_Text>();
                if (text != null) text.text = n.ToString();
                var btn = go.GetComponent<Button>();
                if (btn != null)
                {
                    int picked = n;
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(() =>
                    {
                        UiSfx.Play();
                        onPicked(picked);
                    });
                }
            }
            RefreshLayout(); // 按钮就位后重排（Grp_Numbers 布局组）
        }

        /// <summary>递归按名查找（容错 prefab 层级嵌套）。</summary>
        private static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }
    }
}
