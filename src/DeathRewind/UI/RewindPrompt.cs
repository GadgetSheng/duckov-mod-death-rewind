using System;
using UnityEngine;

namespace DeathRewind.UI
{
    /// <summary>
    /// 冻结期间绘制的死亡裁决面板（IMGUI）。
    /// 不依赖 timeScale，也不依赖游戏原生 UI——原生死亡结算界面
    /// 只在玩家"接受死亡"后由重放的 OnMainCharacterDie → CharacterDieTask 打开。
    /// </summary>
    public static class RewindPrompt
    {
        private static Action<bool> _callback;
        private static string _title = "";
        private static string _detail = "";
        private static string _rewindLabel = "回退";
        private static string _continueLabel = "接受死亡";
        private static KeyCode _rewindKey;
        private static KeyCode _continueKey;

        private static GUIStyle _titleStyle;
        private static GUIStyle _detailStyle;

        public static bool IsOpen => _callback != null;

        public static void Open(string title, string detail, string rewindLabel, string continueLabel,
            KeyCode rewindKey, KeyCode continueKey, Action<bool> callback)
        {
            _title = title;
            _detail = detail;
            _rewindLabel = rewindLabel;
            _continueLabel = continueLabel;
            _rewindKey = rewindKey;
            _continueKey = continueKey;
            _callback = callback;
        }

        public static void Close()
        {
            _callback = null;
        }

        private static void Submit(bool rewind)
        {
            var cb = _callback;
            if (cb == null) return;
            _callback = null;
            cb(rewind);
        }

        /// <summary>只能在 Unity 的 OnGUI 回调中调用；未打开时零开销。</summary>
        public static void Draw()
        {
            if (_callback == null) return;

            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown)
            {
                if (_rewindKey != KeyCode.None && e.keyCode == _rewindKey) { e.Use(); Submit(true); return; }
                if (_continueKey != KeyCode.None && e.keyCode == _continueKey) { e.Use(); Submit(false); return; }
            }

            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 28,
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                };
                _detailStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                };
            }

            const float w = 540f, h = 210f;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(rect, string.Empty);
            GUILayout.BeginArea(rect);
            GUILayout.Space(28);
            GUILayout.Label(_title, _titleStyle);
            GUILayout.Space(10);
            GUILayout.Label(_detail, _detailStyle);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(ButtonLabel(_continueLabel, _continueKey), GUILayout.Width(200f), GUILayout.Height(44f)))
                Submit(false);
            GUILayout.Space(24);
            if (GUILayout.Button(ButtonLabel(_rewindLabel, _rewindKey), GUILayout.Width(200f), GUILayout.Height(44f)))
                Submit(true);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(24);
            GUILayout.EndArea();
        }

        private static string ButtonLabel(string label, KeyCode key)
            => key == KeyCode.None ? label : $"{label}（{key}）";
    }
}
