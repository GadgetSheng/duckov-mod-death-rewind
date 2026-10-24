namespace DeathRewind.Core
{
    /// <summary>
    /// 敌人对象生命周期策略（纯逻辑，不依赖 Unity / 游戏程序集）。
    ///
    /// 背景：游戏自己也会"停用"敌人对象——`SetActiveByPlayerDistance` 会在玩家离开 100m 外时
    /// 把角色根物体 SetActive(false)，玩家回来再激活；死亡时 `Health.Hurt` 也会停用 Health 所在物体。
    /// 因此"对象处于停用状态"**不能**当成"这东西已经死了、可以销毁"的判据：
    /// 误销毁会让 Boss（如零号区·矮鸭）永久消失——游戏的刷怪器不会再次生成它。
    /// </summary>
    public static class EnemyPolicy
    {
        /// <summary>
        /// Mod 是否可以销毁该敌人的 GameObject。
        /// 只有"Mod 自己停用的"对象才允许销毁：Mod 停用 = Mod 接管了它的生命周期
        /// （死亡持久化补丁停用的尸体、回退差量表移除的敌人）。
        /// 游戏自己停用的（距离卸载、场景切换、死亡后原生清理）一律不得销毁。
        /// </summary>
        public static bool CanDestroy(bool modDeactivated, bool currentlyInactive)
            => modDeactivated && currentlyInactive;

        /// <summary>
        /// 回退差量表是否允许"移除"锚点时刻不存在的敌人（锚点后刷出的怪）。
        /// 普通刷怪怪可以移除（世界回滚语义）；Boss 是唯一实体，游戏的刷怪器不会再次生成它，
        /// 移除即永久丢失，因此必须保留。
        /// </summary>
        public static bool CanRemoveLateSpawned(bool isBossCharacter)
            => !isBossCharacter;

        /// <summary>
        /// 该 Health 是否算"敌人"（可进入锚点差量表、随回退一起还原的实体）。
        /// 可破坏物（爆炸桶、木箱、障碍）同样带 Health 组件，但它们不是角色：
        /// 被炸毁后必须保持炸毁状态，绝不能被回退"复活"（玩家会看到桶凭空复原）。
        /// 判据：有角色（CharacterMainControl）或有 AI 控制器。
        /// </summary>
        public static bool IsEnemy(bool hasCharacter, bool hasAiController)
            => hasCharacter || hasAiController;
    }
}
