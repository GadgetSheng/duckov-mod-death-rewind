using DeathRewind.Core;
using UnityEngine;

namespace DeathRewind.UI
{
    /// <summary>
    /// 游戏内设置面板（<c>Ctrl+N</c> 开关；避开游戏原有的任务键 Ctrl+I）。
    ///
    /// 展示：当前得分、1UP 存量（**已用掉的画删除线**）、复活冷却倒计时。
    /// 设置：增强模式开关、每关 1UP 上限（1~3）、复活冷却间隔（30/60/90 秒）——
    /// 全部用「− 值 +」三段式调节，**不允许直接点数值/输入**，当前值用金色徽章高亮。
    /// 改动立即生效并写回 config.txt。
    ///
    /// 面板不暂停游戏，也不吞输入（关闭时零开销）。
    /// </summary>
    public static class RewindSettingsPanel
    {
        /// <summary>选中项的强调色（金色）。</summary>
        private static readonly Color SelectedColor = new Color(0.98f, 0.78f, 0.28f);
        private static readonly Color SelectedTextColor = new Color(0.13f, 0.10f, 0.05f);

        private static bool _open;
        private static bool _stylesReady;
        /// <summary>面板自建的 1×1 底色贴图（Mod 停用时销毁）。</summary>
        private static readonly System.Collections.Generic.List<Texture2D> _textures = new System.Collections.Generic.List<Texture2D>();
        private static GUIStyle _titleStyle;
        private static GUIStyle _versionStyle;
        private static GUIStyle _sectionStyle;
        private static GUIStyle _labelStyle;
        private static GUIStyle _valueStyle;
        private static GUIStyle _valueMutedStyle;
        private static GUIStyle _tokenStyle;
        private static GUIStyle _mutedStyle;
        private static GUIStyle _hintStyle;
        private static GUIStyle _stepStyle;
        private static GUIStyle _badgeStyle;
        private static GUIStyle _badgeOnStyle;
        private static GUIStyle _toggleOnStyle;
        private static GUIStyle _toggleOffStyle;
        private static GUIStyle _footerStyle;

        /// <summary>面板是否打开。</summary>
        public static bool IsOpen => _open;

        /// <summary>切换面板开关（Ctrl+N）。</summary>
        public static void Toggle() => _open = !_open;

        /// <summary>关闭面板。</summary>
        public static void Close() => _open = false;

        /// <summary>释放面板持有的运行时资源（Mod 停用时调用）。</summary>
        public static void Dispose()
        {
            foreach (var tex in _textures)
            {
                if (tex != null) Object.Destroy(tex);
            }
            _textures.Clear();
            _stylesReady = false;
        }

        /// <summary>只能在 Unity 的 OnGUI 回调里调用；未打开时零开销。</summary>
        public static void Draw(ModBehaviour owner)
        {
            if (!_open || owner == null) return;
            EnsureStyles();

            var area = new Rect(Screen.width - 508f, 24f, 484f, 440f);
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 20f, area.y + 16f, area.width - 40f, area.height - 32f));

            // 标题行
            GUILayout.BeginHorizontal();
            GUILayout.Label("死亡回溯 · 设置", _titleStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label("v" + Config.ModConfig.Version, _versionStyle);
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
            DrawSeparator();

            var score = owner.ScoreState;
            var cooldown = owner.RewindCooldown;
            bool enhanced = score != null && score.Enhanced;
            double remain = cooldown != null ? cooldown.Remaining(owner.CurrentTick) : 0;

            // 状态区：得分 / 1UP / 冷却
            string scoreText;
            if (score == null) scoreText = "-";
            else if (!enhanced) scoreText = "未开启（增强模式）";
            else if (score.AtCap) scoreText = $"{score.Score} / {ScoreState.PointsPerUp}（1UP 已满）";
            else scoreText = $"{score.Score} / {ScoreState.PointsPerUp}";
            DrawStatusRow("得分", scoreText, !enhanced);

            GUILayout.BeginHorizontal();
            GUILayout.Label("1UP", _labelStyle, GUILayout.Width(96f));
            GUILayout.FlexibleSpace();
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

            DrawStatusRow("冷却", !enhanced ? "未启用（增强模式）" : (remain > 0 ? $"{remain:0} 秒" : "就绪"), !enhanced);

            GUILayout.Space(8f);
            DrawSeparator();

            int cap = owner.Config != null ? owner.Config.MaxRewinds : 1;
            int cd = owner.Config != null ? owner.Config.RewindCooldownSeconds : 60;

            // 增强模式（默认关闭：保持旧行为）——单个切换按钮：开启=金色，关闭=深底
            GUILayout.Label("增强模式", _sectionStyle);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(enhanced ? "已开启" : "已关闭",
                                 enhanced ? _toggleOnStyle : _toggleOffStyle,
                                 GUILayout.Width(150f), GUILayout.Height(30f)))
            {
                owner.ApplyPanelSettings(!enhanced, cap, cd);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(enhanced
                ? "开启中：自己击杀的生物可积分换 1UP，每关上限与复活冷却生效。"
                : "关闭中：保持默认——每关一次复活，不计分、无冷却。", _hintStyle);

            GUILayout.Space(8f);

            // 以下两项只在增强模式开启时生效
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && enhanced;

            // 每关 1UP 上限（1~3）
            GUILayout.Label("每关 1UP 上限", _sectionStyle);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (StepButton("−", cap > 1)) owner.ApplyPanelSettings(enhanced, cap - 1, cd);
            DrawBadge(cap.ToString(), true);
            if (StepButton("+", cap < 3)) owner.ApplyPanelSettings(enhanced, cap + 1, cd);
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);

            // 复活冷却（30/60/90）
            GUILayout.Label("复活冷却（秒）", _sectionStyle);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (StepButton("−", cd > 30)) owner.ApplyPanelSettings(enhanced, cap, cd == 90 ? 60 : 30);
            DrawBadge(cd.ToString(), true);
            if (StepButton("+", cd < 90)) owner.ApplyPanelSettings(enhanced, cap, cd == 30 ? 60 : 90);
            GUILayout.EndHorizontal();

            GUI.enabled = previousEnabled;

            GUILayout.Space(10f);
            if (GUILayout.Button("关闭 (Ctrl+N)", _footerStyle, GUILayout.Height(34f))) Close();
            GUILayout.EndArea();
        }

        /// <summary>一行状态：左边标签，右边值。</summary>
        private static void DrawStatusRow(string label, string value, bool muted)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.Width(96f));
            GUILayout.FlexibleSpace();
            GUILayout.Label(value, muted ? _valueMutedStyle : _valueStyle);
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 调节按钮（「−」/「+」）：不可用时置灰。**数值本身不可点**——只能靠这两个按钮调。
        /// </summary>
        private static bool StepButton(string glyph, bool available)
        {
            bool previous = GUI.enabled;
            GUI.enabled = previous && available;
            bool clicked = GUILayout.Button(glyph, _stepStyle, GUILayout.Width(40f), GUILayout.Height(30f));
            GUI.enabled = previous;
            return clicked;
        }

        /// <summary>当前值的徽章（选中态金色高亮）。</summary>
        private static void DrawBadge(string text, bool highlighted)
        {
            GUILayout.Label(text, highlighted ? _badgeOnStyle : _badgeStyle, GUILayout.Width(110f), GUILayout.Height(30f));
        }

        /// <summary>细分隔线。</summary>
        private static void DrawSeparator()
        {
            var rect = GUILayoutUtility.GetRect(10f, 1f);
            GUI.color = new Color(1f, 1f, 1f, 0.18f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        /// <summary>在文字上画一条删除线（Unity 富文本没有删除线，只能自己画）。</summary>
        private static void DrawStrike(Rect rect)
        {
            var line = new Rect(rect.x, rect.y + rect.height * 0.5f, rect.width, Mathf.Max(1.5f, rect.height * 0.09f));
            var previous = GUI.color;
            GUI.color = new Color(1f, 0.35f, 0.3f, 0.95f);
            GUI.DrawTexture(line, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static void EnsureStyles()
        {
            if (_stylesReady) return;

            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 19, fontStyle = FontStyle.Bold };
            _versionStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleRight };
            _versionStyle.normal.textColor = new Color(1f, 1f, 1f, 0.55f);

            _sectionStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            _valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            _valueMutedStyle = new GUIStyle(_valueStyle);
            _valueMutedStyle.normal.textColor = new Color(1f, 1f, 1f, 0.5f);
            _tokenStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
            _mutedStyle = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            _mutedStyle.normal.textColor = new Color(1f, 1f, 1f, 0.5f);
            _hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            _hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.65f);

            // 调节按钮：中性深底（当前值由金色徽章表示，按钮本身保持低调）
            _stepStyle = new GUIStyle(GUI.skin.button) { fontSize = 17, fontStyle = FontStyle.Bold };
            _stepStyle.normal.background = MakeTexture(new Color(0.12f, 0.12f, 0.14f, 0.8f));
            _stepStyle.normal.textColor = new Color(0.92f, 0.92f, 0.92f, 0.95f);
            _stepStyle.hover.background = MakeTexture(new Color(0.26f, 0.26f, 0.30f, 0.95f));
            _stepStyle.hover.textColor = Color.white;
            _stepStyle.active.background = MakeTexture(new Color(0.32f, 0.32f, 0.36f, 0.95f));

            // 徽章：未选中为深底，选中为金色底 + 深色粗体字
            //（GUIStyleState 没有 backgroundTintColor，颜色直接烘进 1×1 贴图）
            _badgeStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(10, 10, 4, 4),
            };
            _badgeStyle.normal.background = MakeTexture(new Color(0.10f, 0.10f, 0.12f, 0.8f));
            _badgeStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f, 0.95f);

            _badgeOnStyle = new GUIStyle(_badgeStyle);
            _badgeOnStyle.normal.background = MakeTexture(SelectedColor);
            _badgeOnStyle.normal.textColor = SelectedTextColor;

            // 增强模式切换按钮：开启=金色高亮，关闭=深底灰字
            _toggleOnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(10, 10, 4, 4),
            };
            _toggleOnStyle.normal.background = MakeTexture(SelectedColor);
            _toggleOnStyle.normal.textColor = SelectedTextColor;
            _toggleOnStyle.hover.background = MakeTexture(new Color(1f, 0.87f, 0.42f));
            _toggleOnStyle.hover.textColor = SelectedTextColor;
            _toggleOnStyle.active.background = MakeTexture(new Color(0.88f, 0.68f, 0.18f));
            _toggleOnStyle.active.textColor = SelectedTextColor;

            _toggleOffStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(10, 10, 4, 4),
            };
            _toggleOffStyle.normal.background = MakeTexture(new Color(0.12f, 0.12f, 0.14f, 0.8f));
            _toggleOffStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f, 0.95f);
            _toggleOffStyle.hover.background = MakeTexture(new Color(0.26f, 0.26f, 0.30f, 0.95f));
            _toggleOffStyle.hover.textColor = Color.white;

            _footerStyle = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold };
            _footerStyle.normal.background = MakeTexture(new Color(0.16f, 0.16f, 0.18f, 0.85f));
            _footerStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f, 0.95f);
            _footerStyle.hover.background = MakeTexture(new Color(0.26f, 0.26f, 0.30f, 0.95f));
            _footerStyle.hover.textColor = Color.white;

            _stylesReady = true;
        }

        /// <summary>1×1 纯色贴图（按钮/徽章底色用）。</summary>
        private static Texture2D MakeTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            _textures.Add(tex);
            return tex;
        }
    }
}
