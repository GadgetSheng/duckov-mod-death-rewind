using System;
using System.Reflection;
using UnityEngine;

namespace DeathRewind.Compat
{
    /// <summary>
    /// 集中反射访问游戏私有字段（LevelManager.dieTask、CharacterMainControl.hasHandledDeath、
    /// Health.isDead、TimeOfDayController.time）。字段失效时统一报错，不散落异常。
    /// </summary>
    public static class PrivateStateAccessor
    {
        private static bool _warned;

        private static FieldInfo FindField(object target, string name)
        {
            if (target == null) return null;
            var t = target.GetType();
            while (t != null)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        public static bool GetBool(object target, string name)
        {
            var f = FindField(target, name);
            if (f == null)
            {
                WarnMissing(target, name);
                return false;
            }
            try { return (bool)f.GetValue(target); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 读取私有字段 {name} 失败: {e.Message}"); return false; }
        }

        public static void SetBool(object target, string name, bool value)
        {
            var f = FindField(target, name);
            if (f == null) { WarnMissing(target, name); return; }
            try { f.SetValue(target, value); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 写入私有字段 {name} 失败: {e.Message}"); }
        }

        public static float GetFloat(object target, string name)
        {
            var f = FindField(target, name);
            if (f == null) { WarnMissing(target, name); return 0f; }
            try { return (float)f.GetValue(target); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 读取私有字段 {name} 失败: {e.Message}"); return 0f; }
        }

        public static void SetFloat(object target, string name, float value)
        {
            var f = FindField(target, name);
            if (f == null) { WarnMissing(target, name); return; }
            try { f.SetValue(target, value); }
            catch (Exception e) { Debug.LogError($"[DeathRewind] 写入私有字段 {name} 失败: {e.Message}"); }
        }

        private static void WarnMissing(object target, string name)
        {
            if (_warned) return;
            _warned = true;
            Debug.LogError($"[DeathRewind] 找不到私有字段 {name}（{target?.GetType().Name}），相关功能将降级；游戏更新后请同步本 Mod。");
        }
    }
}
