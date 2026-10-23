using System;
using System.Linq;
using UnityEngine;

namespace DeathRewind.Harmony
{
    /// <summary>补丁注册前的 Harmony 冲突检测（规格 §10）。</summary>
    public static class HarmonyGuard
    {
        /// <summary>本 Mod 随包携带的 Harmony 版本（vendor/0Harmony.dll）。</summary>
        public static readonly Version ExpectedVersion = new Version(2, 4, 1, 0);

        /// <summary>
        /// 返回 true 表示可以注册补丁；false 表示检测到不兼容，应整体降级为"不拦截"。
        /// 兼容策略（v1.0.1）：接受任何 Harmony 2.x —— 我们用到的 API（Harmony/AccessTools/
        /// Traverse/前缀）在 2.x 全系稳定；其他 Mod（如 SuperPet）会内嵌并提前加载旧版
        /// 2.3.3，严格 equal-match 会导致本 Mod 在装了它们的机器上整体静默失效。
        /// 仅拒绝 1.x / 3.x+（API 不兼容）。
        /// </summary>
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
                    if (v == null || v.Major != 2)
                    {
                        Debug.LogError($"[DeathRewind] Harmony 版本不兼容（发现 {v}，需要 2.x），补丁 {tag} 未注册，Mod 降级为不拦截。");
                        return false;
                    }
                    if (v < ExpectedVersion)
                        ModLog.LogWarning($"[DeathRewind] 检测到其他 Mod 加载的旧版 Harmony {v}（本 Mod 携带 {ExpectedVersion}），API 兼容，补丁 {tag} 照常注册。");
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
