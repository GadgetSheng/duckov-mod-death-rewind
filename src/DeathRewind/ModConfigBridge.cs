using System;
using System.Reflection;
using UnityEngine;

namespace DeathRewind
{
    /// <summary>
    /// ModConfig 创意工坊框架（steam://id=3590674339）的桥接：纯反射互操作，无编译期依赖。
    /// 框架未安装时所有方法静默降级，继续走 config.txt；接入后可在游戏内设置界面调整参数。
    /// API v1：ModConfig.ModBehaviour.AddDropdownList / AddInputWithSlider / AddBoolDropdownList /
    /// AddOnOptionsChangedDelegate；ModConfig.OptionsManager_Mod.Load&lt;T&gt; / Save&lt;T&gt;（ES3 持久化）。
    /// </summary>
    public static class ModConfigBridge
    {
        public const string ModName = "DeathRewind";

        private static Type _modBehaviourType;
        private static Type _optionsManagerType;
        private static bool _initialized;

        public static bool Available => Initialize();

        private static bool Initialize()
        {
            if (_initialized) return _modBehaviourType != null;
            _initialized = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _modBehaviourType = _modBehaviourType ?? asm.GetType("ModConfig.ModBehaviour");
                    _optionsManagerType = _optionsManagerType ?? asm.GetType("ModConfig.OptionsManager_Mod");
                }
                if (_modBehaviourType == null || _optionsManagerType == null)
                {
                    Debug.Log("[DeathRewind] ModConfig 框架未安装，跳过游戏内配置接入（继续使用 config.txt）");
                    return false;
                }
                if (_modBehaviourType.GetField("VERSION")?.GetValue(null) is int v && v != 1)
                {
                    Debug.LogWarning($"[DeathRewind] ModConfig API 版本不匹配（框架 v{v}，桥接 v1），跳过接入");
                    _modBehaviourType = null;
                    return false;
                }
                Debug.Log("[DeathRewind] ModConfig 框架已接入");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] ModConfig 接入失败: {e.Message}");
                _modBehaviourType = null;
            }
            return _modBehaviourType != null;
        }

        private static void Invoke(string method, object[] args)
        {
            _modBehaviourType.GetMethod(method, BindingFlags.Static | BindingFlags.Public)?.Invoke(null, args);
        }

        /// <summary>添加下拉列表（string 值；labels 与 values 一一对应）。</summary>
        public static void AddDropdown(string key, string description, string[] values, string[] labels, string defaultValue)
        {
            if (!Available) return;
            try
            {
                var options = new System.Collections.Generic.SortedDictionary<string, object>();
                for (int i = 0; i < values.Length; i++) options[labels[i]] = values[i];
                Invoke("AddDropdownList", new object[] { ModName, Prefixed(key), description, options, typeof(string), defaultValue });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] ModConfig 注册 {key} 失败: {e.Message}");
            }
        }

        /// <summary>添加滑条输入框（int/float 值，range 为滑条范围）。</summary>
        public static void AddSlider(string key, string description, Type valueType, object defaultValue, Vector2 range)
        {
            if (!Available) return;
            try
            {
                Invoke("AddInputWithSlider", new object[] { ModName, Prefixed(key), description, valueType, defaultValue, range });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] ModConfig 注册 {key} 失败: {e.Message}");
            }
        }

        /// <summary>添加布尔下拉。</summary>
        public static void AddBool(string key, string description, bool defaultValue)
        {
            if (!Available) return;
            try
            {
                Invoke("AddBoolDropdownList", new object[] { ModName, Prefixed(key), description, defaultValue });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] ModConfig 注册 {key} 失败: {e.Message}");
            }
        }

        /// <summary>注册"任意选项保存"回调（参数为带前缀的完整 key）。</summary>
        public static void AddOptionsChanged(Action<string> handler)
        {
            if (!Available) return;
            try
            {
                Invoke("AddOnOptionsChangedDelegate", new object[] { handler });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] ModConfig 注册回调失败: {e.Message}");
            }
        }

        /// <summary>从 ModConfig 持久化存储读取覆盖值（key 缺失时框架会写入并返回 fallback）。</summary>
        public static T Load<T>(string key, T fallback)
        {
            if (!Available) return fallback;
            try
            {
                var generic = _optionsManagerType.GetMethod("Load", BindingFlags.Static | BindingFlags.Public)
                    ?.MakeGenericMethod(typeof(T));
                var value = generic?.Invoke(null, new object[] { Prefixed(key), fallback });
                return value != null ? (T)value : fallback;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] ModConfig 读取 {key} 失败: {e.Message}");
                return fallback;
            }
        }

        public static string Prefixed(string key) => ModName + "_" + key;
    }
}
