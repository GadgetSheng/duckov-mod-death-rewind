using System;
using DeathRewind.Unity;
using HarmonyLib;
using HarmonyLibHarmony = HarmonyLib.Harmony;
using ItemStatsSystem;
using UnityEngine;

namespace DeathRewind.Harmony
{
    /// <summary>
    /// 阻止"敌人死亡时把装备丢进尸体箱"。
    /// <para>
    /// 原生逻辑：<c>CharacterMainControl.OnDead</c> 会用 <c>InteractableLootbox.CreateFromItem</c>
    /// 把角色的 CharacterItem（含装备）转成尸体箱。敌人被回溯复活时，装备已经在尸体箱里了 →
    /// 复活后赤手空拳。这里在创建尸体箱之前拦截**敌人的**物品，装备就留在敌人身上。
    /// 玩家的物品不受影响（照常掉落，保留死亡损失的设计）。
    /// </para>
    /// </summary>
    public static class EnemyLootboxPatch
    {
        private static EntitySampler _sampler;
        private static HarmonyLibHarmony _harmony;

        public static void Install(EntitySampler sampler)
        {
            _sampler = sampler;
            if (!HarmonyGuard.CanRegister("com.deathrewind.enemylootbox"))
            {
                ModLog.LogWarning("[DeathRewind] 因 Harmony 冲突，敌人装备保留补丁未启用");
                return;
            }
            var target = AccessTools.Method(typeof(InteractableLootbox), "CreateFromItem", new[]
            {
                typeof(Item), typeof(Vector3), typeof(Quaternion), typeof(bool), typeof(InteractableLootbox), typeof(bool),
            });
            if (target == null) { Debug.LogError("[DeathRewind] 未找到 InteractableLootbox.CreateFromItem"); return; }
            _harmony = new HarmonyLibHarmony("com.deathrewind.enemylootbox");
            var prefix = AccessTools.Method(typeof(EnemyLootboxPatch), nameof(Prefix));
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        public static void Uninstall() { _harmony?.UnpatchAll("com.deathrewind.enemylootbox"); _harmony = null; }

        private static bool Prefix(Item item, ref InteractableLootbox __result)
        {
            if (item == null) return true;
            if (_sampler == null || !_sampler.IsEnemyItem(item)) return true;
            // 敌人的装备留在身上（供回溯复活），不生成尸体箱
            __result = null;
            return false;
        }
    }
}
