using System;
using System.Collections.Generic;
using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// 全局事件中心（观察者模式）：谁改数据谁发事件，谁关心谁监听。
    /// 事件名 = 枚举（不会拼错，符合"UI 不加门面层"决策）；Core 不知道游戏内容，用泛型枚举承载。
    /// </summary>
    public class EventCenter : BaseManager<EventCenter>
    {
        private sealed class ListenerSet
        {
            public Action<object> Chain;
            public Action<object>[] Snapshot; // 增删时失效——广播时重建，避免每次广播 GetInvocationList 分配 Delegate[]
        }

        private readonly Dictionary<Type, Dictionary<int, ListenerSet>> _listeners = new();

        /// <summary>注册监听。</summary>
        public void AddEventListener<T>(T eventType, Action<object> handler) where T : Enum
        {
            var type = typeof(T);
            if (!_listeners.TryGetValue(type, out var map))
            {
                map = new Dictionary<int, ListenerSet>();
                _listeners[type] = map;
            }
            int key = Convert.ToInt32(eventType);
            if (!map.TryGetValue(key, out var set) || set == null)
            {
                set = new ListenerSet();
                map[key] = set;
            }
            set.Chain += handler;
            set.Snapshot = null;
        }

        /// <summary>移除监听。</summary>
        public void RemoveEventListener<T>(T eventType, Action<object> handler) where T : Enum
        {
            if (_listeners.TryGetValue(typeof(T), out var map))
            {
                int key = Convert.ToInt32(eventType);
                if (map.TryGetValue(key, out var set) && set != null)
                {
                    // ⚠️ 委托不可变：-= 产生新链，必须写回（否则移除从未生效——旧实例监听残留）
                    set.Chain -= handler;
                    set.Snapshot = null;
                    if (set.Chain == null)
                    {
                        map.Remove(key); // 空链移除——语义同旧 map 存 null（EventTrigger 跳过；Add 重建）
                    }
                }
            }
        }

        /// <summary>触发事件（广播给所有监听者）。data 携带事件信息（如伤害发生：攻击者/目标/伤害/是否死亡）。</summary>
        public void EventTrigger<T>(T eventType, object data = null) where T : Enum
        {
            if (_listeners.TryGetValue(typeof(T), out var map))
            {
                if (map.TryGetValue(Convert.ToInt32(eventType), out var set) && set != null)
                {
                    var snapshot = set.Snapshot;
                    if (snapshot == null)
                    {
                        var chain = set.Chain;
                        if (chain == null) return;
                        var invocations = chain.GetInvocationList();
                        snapshot = new Action<object>[invocations.Length];
                        for (int i = 0; i < invocations.Length; i++)
                        {
                            snapshot[i] = (Action<object>)invocations[i];
                        }
                        set.Snapshot = snapshot;
                    }
                    // ⚠️ 2026-08-12（大审查 H1）：逐监听者异常隔离——任一监听者崩溃不中断委托链、
                    // 不向上传播到规则层（防 Resolve* 落账半途中断=半落账）。历史"敌方未生成+WaveScores=0"即此链条。
                    foreach (var d in snapshot)
                    {
                        try
                        {
                            d(data);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[EventCenter] 监听者异常（{d.Target?.GetType().Name}）：{e}");
                        }
                    }
                }
            }
        }
    }
}
