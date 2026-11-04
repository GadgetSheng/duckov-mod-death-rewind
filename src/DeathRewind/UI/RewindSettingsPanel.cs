using DeathRewind.Core;
using UnityEngine;

namespace DeathRewind.UI
{
    /// <summary>
    /// 游戏内设置面板（<c>Ctrl+I</c> 开关）。
    ///
    /// 展示：当前得分、1UP 存量（**已用掉的画删除线**）、复活冷却倒计时。
    /// 设置：每关 1UP 上限（1~3）、复活冷却间隔（30/60/90 秒）——改动立即生效并写回 config.txt。
    ///
    /// 面板不暂停游戏，也不吞输入（关闭时零开销）。
    /// </summary>
    public static class RewindSettingsPanel
    {
        private static bool _open;
        private static bool _stylesReady;
        private static GUIStyle _titleStyle;
        private static GUIStyle _labelStyle;
        private static GUIStyle _valueStyle;
        private static GUIStyle _tokenStyle;
        private static GUIStyle _mutedStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _buttonOnStyle;

        /// <summary>面板是否打开。</summary>
        public static bool IsOpen => _open;

        /// <summary>切换面板开关（Ctrl+I）。</summary>
        public static void Toggle() => _open = !_open;

        /// <summary>关闭面板。</summary>
        public static void Close() => _open = false;

        /// <summary>只能在 Unity 的 OnGUI 回调里调用；未打开时零开销。</summary>
        public static void Draw(ModBehaviour owner)
        {
            if (!_open || owner == null) return;
            EnsureStyles();

            var area = new Rect(Screen.width - 360f, 90f, 340f, 300f);
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 14f, area.y + 10f, area.width - 28f, area.height - 20f));

            GUILayout.Label("死亡回溯 · 设置", _titleStyle);
            GUILayout.Space(4f);

            var score = owner.ScoreState;
            var cooldown = owner.RewindCooldown;
            bool enhanced = score != null && score.Enhanced;
            double remain = cooldown != null ? cooldown.Remaining(owner.CurrentTick) : 0;

            // 得分
            GUILayout.BeginHorizontal();
            GUILayout.Label("得分", _labelStyle, GUILayout.Width(64f));
            if (score != null)
            {
                string text;
                if (!enhanced) text = "未开启（增强模式）";
                else if (score.AtCap) text = $"{score.Score} / {ScoreState.PointsPerUp}（1UP 已满）";
                else text = $"{score.Score} / {ScoreState.PointsPerUp}";
                GUILayout.Label(text, enhanced ? _valueStyle : _mutedStyle);
            }
            GUILayout.EndHorizontal();

            // 1UP：已用掉的逐个画删除线，剩余的显示 ×N UP
            GUILayout.BeginHorizontal();
            GUILayout.Label("1UP", _labelStyle, GUILayout.Width(64f));
            GUILayout.BeginHorizontal();
            if (score != null)
            {
                for (int i = 0; i < score.UpsUsed; i++)
                {
                    GUILayout.Label("×1", _tokenStyle);
                    DrawStrike(GUILayoutUtility.GetLastRect());
                }
                if (score.Held > 0) GUILayout.Label($"×{score.Held} UP", _tokenStyle);
                else GUILayout.Label("无", _mutedStyle);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndHorizontal();

            // 冷却
            GUILayout.BeginHorizontal();
            GUILayout.Label("冷却", _labelStyle, GUILayout.Width(64f));
            if (!enhanced) GUILayout.Label("未启用（增强模式）", _mutedStyle);
            else GUILayout.Label(remain > 0 ? $"{remain:0} 秒" : "就绪", _valueStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);

            int cap = owner.Config != null ? owner.Config.MaxRewinds : 1;
            int cd = owner.Config != null ? owner.Config.RewindCooldownSeconds : 60;

            // 增强模式开关：默认关闭（保持旧行为：每关一次复活、不计分、无冷却）
            GUILayout.Label("增强模式", _labelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(enhanced ? "已开启" : "已关闭", enhanced ? _buttonOnStyle : _buttonStyle))
                owner.ApplyPanelSettings(!enhanced, cap, cd);
            GUILayout.EndHorizontal();
            GUILayout.Label(enhanced
                ? "开启中：积分可换 1UP，每关上限与冷却生效"
                : "关闭中：保持默认（每关一次复活，不计分、无冷却）", _mutedStyle);

            GUILayout.Space(4f);

            // 以下两项只在增强模式开启时生效
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && enhanced;
            GUILayout.Label("每关 1UP 上限", _labelStyle);
            GUILayout.BeginHorizontal();
            for (int i = 1; i <= 3; i++)
            {
                if (GUILayout.Button(i.ToString(), i == cap ? _buttonOnStyle : _buttonStyle))
                    owner.ApplyPanelSettings(enhanced, i, cd);
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("复活冷却（秒）", _labelStyle);
            GUILayout.BeginHorizontal();
            foreach (int value in new[] { 30, 60, 90 })
            {
                if (GUILayout.Button(value.ToString(), value == cd ? _buttonOnStyle : _buttonStyle))
                    owner.ApplyPanelSettings(enhanced, cap, value);
            }
            GUILayout.EndHorizontal();
            GUI.enabled = previousEnabled;

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("关闭 (Ctrl+I)")) Close();
            GUILayout.EndArea();
        }

        /// <summary>在文字上画一条删除线（Unity 富文本没有删除线，只能自己画）。</summary>
        private static void DrawStrike(Rect rect)
        {
            var line = new Rect(rect.x, rect.y + rect.height * 0.5f, rect.width, Mathf.Max(1f, rect.height * 0.08f));
            var previous = GUI.color;
            GUI.color = new Color(1f, 0.35f, 0.3f, 0.95f);
            GUI.DrawTexture(line, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static void EnsureStyles()
        {
            if (_stylesReady) return;
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            _labelStyle = new GUIStyle(GUI.skin.label);
            _valueStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _tokenStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _mutedStyle = new GUIStyle(GUI.skin.label);
            _mutedStyle.normal.textColor = new Color(1f, 1f, 1f, 0.45f);
            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonOnStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
            _stylesReady = true;
        }
    }
}
