using UnityEngine;

namespace TheLaw.Core
{
    /// <summary>
    /// MonoBehaviour 单例基类：首次访问时查找，不存在则创建并 DontDestroyOnLoad。
    /// 用法：AudioManager 继承。
    /// </summary>
    public abstract class SingletonAutoMono<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T _instance;

        public static T Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<T>();
                    if (_instance == null)
                    {
                        var go = new GameObject(typeof(T).Name);
                        _instance = go.AddComponent<T>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }

        protected virtual void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
