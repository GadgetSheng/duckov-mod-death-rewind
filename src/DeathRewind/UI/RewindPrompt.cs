using System;
using UnityEngine;

namespace DeathRewind.UI
{
    /// <summary>冻结期间绘制的确认面板。屏幕绝对坐标绘制，不挂到玩家对象上。</summary>
    public static class RewindPrompt
    {
        private static Action<bool> _callback;
        private static bool _submitted;

        public static bool IsOpen => _callback != null && !_submitted;

        public static void Open(Action<bool> callback) { _callback = callback; _submitted = false; }
        public static void Close() { _callback = null; _submitted = false; }

        public static void Submit(bool rewind)
        {
            if (_callback == null || _submitted) return;
            _submitted = true;
            var cb = _callback;
            _callback = null;
            cb(rewind);
        }

        /// <summary>只能在 Unity 的 OnGUI 回调中调用。</summary>
        public static void Draw()
        {
            if (_callback == null || _submitted) return;
            float w = 460f, h = 180f;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label("被击败了");
            GUILayout.Label("回退到约 120–180 秒前的世界状态？");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("继续（接受死亡）")) Submit(false);
            if (GUILayout.Button("回退")) Submit(true);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
