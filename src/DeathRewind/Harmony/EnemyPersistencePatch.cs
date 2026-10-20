using DeathRewind.Unity;
using HarmonyLib;
using HarmonyLibHarmony = HarmonyLib.Harmony;
using UnityEngine;

namespace DeathRewind.Harmony
{
    /// <summary>接管敌人 <c>Health.DestroyOnDelay</c>，让死亡敌人留在场上以便回退时复活。</summary>
    public static class EnemyPersistencePatch
    {
        private static EntitySampler _sampler;
        private static HarmonyLibHarmony _harmony;

        public static void Install(EntitySampler sampler)
        {
            _sampler = sampler;
            if (!HarmonyGuard.CanRegister("com.deathrewind.enemypersistence"))
            {
                Debug.LogWarning("[DeathRewind] 因 Harmony 冲突，敌人持久化补丁未启用");
                return;
            }
            var target = AccessTools.Method(typeof(Health), "DestroyOnDelay");
            if (target == null) { Debug.LogError("[DeathRewind] 未找到 Health.DestroyOnDelay"); return; }
            _harmony = new HarmonyLibHarmony("com.deathrewind.enemypersistence");
            var prefix = AccessTools.Method(typeof(EnemyPersistencePatch), "Prefix", new[] { typeof(Health) });
            if (prefix == null) { Debug.LogError("[DeathRewind] 未找到 EnemyPersistencePatch.Prefix"); return; }
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        public static void Uninstall() { _harmony?.UnpatchAll("com.deathrewind.enemypersistence"); _harmony = null; }

        /// <summary>
        /// 前缀必须是同步方法；返回 false 即整条 async 状态机被跳过（调用方 await 到 default(UniTask) 立即完成）。
        /// 只接管"敌人角色"，非敌人的 Health（如可破坏物）照常销毁。
        /// </summary>
        private static bool Prefix(Health __instance)
        {
            if (__instance == null) return true;
            if (!EntitySampler.IsEnemyCharacter(__instance)) return true;
            _sampler?.RegisterDeadEnemy(__instance);
            return false;
        }
    }
}
