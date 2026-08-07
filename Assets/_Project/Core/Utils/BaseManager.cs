namespace TheLaw.Core
{
    /// <summary>
    /// 纯 C# 单例基类（非 MonoBehaviour）。
    /// 用法：EventCenter / SaveManager / RandomManager / SettingsSystem / GameState 继承。
    /// </summary>
    public abstract class BaseManager<T> where T : BaseManager<T>, new()
    {
        private static T _instance;

        public static T Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new T();
                }
                return _instance;
            }
        }
    }
}
