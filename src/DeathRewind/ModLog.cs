namespace DeathRewind
{
    /// <summary>
    /// 诊断日志统一出口：发布版 <see cref="USE_DEBUG"/> 为 false 时静默；
    /// 排查问题改为 true 重新编译即可恢复全部 Info/Warning 日志。
    /// Error 级日志仍由 UnityEngine.Debug.LogError 直接输出，不受本开关影响。
    /// </summary>
    internal static class ModLog
    {
        // 统一开关：发布版保持 false；排查问题改为 true 重新编译即可恢复全部诊断日志。
        public static bool USE_DEBUG = false;

        public static void Log(object message)
        {
            if (USE_DEBUG) UnityEngine.Debug.Log(message);
        }

        public static void LogWarning(object message)
        {
            if (USE_DEBUG) UnityEngine.Debug.LogWarning(message);
        }

        /// <summary>
        /// 决策级日志：**不受 USE_DEBUG 开关影响**，始终输出。
        /// 用于"为什么没有回溯能力"这类必须能在发布版日志里读到的事件：
        /// 回溯机会用尽、没有合格锚点、目标空间跨关卡、控件注入失败、回溯结果、跨空间传送。
        /// </summary>
        public static void LogImportant(object message) => UnityEngine.Debug.Log(message);
    }
}
