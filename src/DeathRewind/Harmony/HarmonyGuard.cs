using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DeathRewind.Harmony
{
    /// <summary>补丁注册前的 Harmony 冲突检测（规格 §10）。</summary>
    public static class HarmonyGuard
    {
        /// <summary>本 Mod 期望的 Harmony 版本（与 vendor/0Harmony.dll 一致）。</summary>
        public static readonly Version ExpectedVersion = new Version(2, 4, 1, 0);

        /// <summary>返回 true 表示可以注册补丁；false 表示检测到冲突，应整体降级为"不拦截"。</summary>
        public static bool CanRegister(string tag)
        {
            try
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name == "0Harmony")
                    .ToList();
                if (loaded.Count == 0) return true;

                // 与"期望版本"比较，而不是与 typeof(Harmony) 绑定到的那份比较
                // （若别的 Mod 先加载了不同版本，类型绑定会解析到那一份，导致检测不到冲突）
                foreach (var asm in loaded)
                {
                    var v = asm.GetName().Version;
                    if (v == null || v.Major != ExpectedVersion.Major || v.Minor != ExpectedVersion.Minor)
                    {
                        Debug.LogError($"[DeathRewind] Harmony 版本冲突（发现 {v}，期望 {ExpectedVersion}），补丁 {tag} 未注册，Mod 降级为不拦截。");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeathRewind] Harmony 冲突检测异常（{tag}），按冲突处理：{e.Message}");
                return false;
            }
        }
    }
}
