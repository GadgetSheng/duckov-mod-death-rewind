using System;

namespace DeathRewind.UI
{
    /// <summary>
    /// 死亡回退提示面板（IMGUI）。
    /// TODO: 后续任务实现（Task 13）。当前为最小桩：不绘制任何界面。
    /// </summary>
    public static class RewindPrompt
    {
        /// <summary>面板是否处于打开状态。</summary>
        public static bool IsOpen { get; private set; }

        /// <summary>TODO: 后续任务实现 —— 打开面板并注册回调。</summary>
        public static void Open(Action<bool> callback)
        {
        }

        /// <summary>TODO: 后续任务实现 —— 关闭面板并清除回调。</summary>
        public static void Close()
        {
        }

        /// <summary>TODO: 后续任务实现 —— 提交选择（true=回退，false=继续）。</summary>
        public static void Submit(bool rewind)
        {
        }

        /// <summary>TODO: 后续任务实现 —— 绘制面板（只能在 OnGUI 中调用）。</summary>
        public static void Draw()
        {
        }
    }
}
