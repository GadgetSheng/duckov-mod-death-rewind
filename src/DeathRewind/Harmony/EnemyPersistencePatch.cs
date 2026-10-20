using DeathRewind.Unity;

namespace DeathRewind.Harmony
{
    /// <summary>
    /// 敌人持久化补丁（接管敌人销毁时机，改为延迟销毁）。
    /// TODO: 后续任务实现（Task 14）。当前为最小桩：不安装任何 Harmony 补丁。
    /// </summary>
    public static class EnemyPersistencePatch
    {
        /// <summary>TODO: 后续任务实现 —— 安装敌人销毁接管补丁。</summary>
        public static void Install(EntitySampler sampler)
        {
        }

        /// <summary>TODO: 后续任务实现 —— 卸载敌人销毁接管补丁。</summary>
        public static void Uninstall()
        {
        }
    }
}
