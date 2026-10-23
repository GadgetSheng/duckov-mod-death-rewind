using System.Diagnostics;
using Duckov.Scenes;
using HarmonyLib;
using HarmonyLibHarmony = HarmonyLib.Harmony;
using Debug = UnityEngine.Debug;
using UnityEngine;

namespace DeathRewind.Harmony
{
    /// <summary>
    /// 伤害门控诊断补丁：定位"复活后无敌"的外部来源。
    /// - 主角 Health.Hurt 返回 false（没掉血）→ 记录当时三道门（IsLoading/invincible/isDead）状态；
    /// - 主角 Health.SetInvincible(true) → 记录完整调用栈（抓出是谁在事后置无敌）。
    /// 两条日志均限频（2s），正常游玩零感知。
    /// </summary>
    public static class HurtGatePatch
    {
        private const double LogIntervalSeconds = 2.0;
        private static double _nextHurtLogAt;
        private static double _nextInvincibleLogAt;
        private static HarmonyLibHarmony _harmony;

        public static void Install()
        {
            if (!HarmonyGuard.CanRegister("com.deathrewind.hurtgate"))
            {
                ModLog.LogWarning("[DeathRewind] 因 Harmony 冲突，伤害门控诊断补丁未启用");
                return;
            }
            var hurt = AccessTools.Method(typeof(Health), "Hurt");
            var setInvincible = AccessTools.Method(typeof(Health), "SetInvincible");
            if (hurt == null || setInvincible == null)
            {
                Debug.LogError($"[DeathRewind] 未找到 Health.Hurt({hurt == null}) / SetInvincible({setInvincible == null})");
                return;
            }
            _harmony = new HarmonyLibHarmony("com.deathrewind.hurtgate");
            _harmony.Patch(hurt,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HurtGatePatch), "HurtPostfix",
                    new[] { typeof(Health), typeof(bool) })));
            _harmony.Patch(setInvincible,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HurtGatePatch), "SetInvinciblePrefix",
                    new[] { typeof(Health), typeof(bool) })));
        }

        public static void Uninstall()
        {
            _harmony?.UnpatchAll("com.deathrewind.hurtgate");
            _harmony = null;
        }

        /// <summary>主角没掉血时记录当前门控状态（限频）。</summary>
        private static void HurtPostfix(Health __instance, bool __result)
        {
            try
            {
                if (__result || __instance == null || !__instance.IsMainCharacterHealth) return;
                double now = Time.realtimeSinceStartup;
                if (now < _nextHurtLogAt) return;
                _nextHurtLogAt = now + LogIntervalSeconds;

                bool loading = MultiSceneCore.Instance != null && MultiSceneCore.Instance.IsLoading;
                ModLog.LogWarning("[DeathRewind] 主角伤害被拦截（Hurt=false）: "
                    + $"invincible={__instance.Invincible} isDead={__instance.IsDead} "
                    + $"isLoading={loading} hp={__instance.CurrentHealth:0.##}");
            }
            catch
            {
                /* 诊断补丁绝不抛出 */
            }
        }

        /// <summary>主角被置为无敌时记录调用栈（限频）——抓"事后翻回无敌"的元凶。</summary>
        private static void SetInvinciblePrefix(Health __instance, bool value)
        {
            try
            {
                if (!value || __instance == null || !__instance.IsMainCharacterHealth) return;
                double now = Time.realtimeSinceStartup;
                if (now < _nextInvincibleLogAt) return;
                _nextInvincibleLogAt = now + LogIntervalSeconds;

                ModLog.LogWarning("[DeathRewind] 主角被置为无敌（SetInvincible(true)），调用栈:\n" + new StackTrace(false));
            }
            catch
            {
                /* 诊断补丁绝不抛出 */
            }
        }
    }
}
