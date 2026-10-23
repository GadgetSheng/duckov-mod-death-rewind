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
    }
}
