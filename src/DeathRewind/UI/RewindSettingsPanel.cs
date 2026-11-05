using DeathRewind.Core;
using UnityEngine;

namespace DeathRewind.UI
{
    /// <summary>
    /// 游戏内设置面板（<c>Ctrl+N</c> 开关；避开游戏原有的任务键 Ctrl+I）。
    ///
    /// 展示（全部为增强模式数据）：当前得分进度条（填充 = 积分/100，数值叠在条中央）。
    /// 1UP 存量与冷却倒计时不在面板展示（HUD 硬币已承载）。
    /// 设置：增强模式开关、复活冷却间隔（0/30/60/90 秒，0 = 无冷却）——
    /// 全部用「− 值 +」三段式调节，**不允许直接点数值/输入**，当前值用金色徽章高亮。
    /// 改动立即生效并写回 config.txt。
    ///
    /// v1.0.14 起取消「每关 1UP 上限」：积分满 100 必发 1UP（不限次数），真正的限制是复活冷却。
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
        private static GUIStyle _barTextStyle;
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

        /// <summary>面板内容的实测高度（上一帧量得，首帧用保守默认值）。</summary>
        private static float _contentHeight = 470f;

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

            // 面板高度贴合内容实测值（上一帧量得），且不超过屏幕高度
            float maxHeight = Mathf.Max(360f, Screen.height - 48f);
            float panelHeight = Mathf.Min(maxHeight, _contentHeight);
            var area = new Rect(Screen.width - 508f, 24f, 484f, panelHeight);
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 20f, area.y + 16f, area.width - 40f, maxHeight - 32f));

            // 标题行
            GUILayout.BeginHorizontal();
            GUILayout.Label("死亡回溯 · 设置", _titleStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label("v" + Config.ModConfig.Version, _versionStyle);
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
            DrawSeparator();

            var score = owner.ScoreState;
            bool enhanced = score != null && score.Enhanced;
            int cd = owner.Config != null ? owner.Config.RewindCooldownSeconds : 60;

            // 增强模式开关放在面板最上方（它是下面所有数据与设置的总开关）
            // 单个切换按钮：开启=金色，关闭=深底
            GUILayout.Label("增强模式", _sectionStyle);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(enhanced ? "已开启" : "已关闭",
                                 enhanced ? _toggleOnStyle : _toggleOffStyle,
                                 GUILayout.Width(150f), GUILayout.Height(30f)))
            {
                owner.ApplyPanelSettings(!enhanced, cd);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(enhanced
                ? "开启中：击杀生物可赚回 1UP（分值见下方积分规则），满 100 分发放、不限次数；获得后进入冷却，冷却结束才能再次复活。"
                : "关闭中：保持默认——每关一次复活，不计分、无冷却。", _hintStyle);

            GUILayout.Space(8f);
            DrawSeparator();
            GUILayout.Space(8f);   // 得分行上边距，与下边距一致

            // 状态区：得分进度条（唯一的状态展示；未开启增强模式时空槽置灰）
            GUILayout.BeginHorizontal();
            GUILayout.Label("得分", _labelStyle, GUILayout.Width(96f));
            DrawScoreProgress(score, enhanced);
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            DrawSeparator();

            // 积分规则（未开启增强模式时随状态区一起置灰；分值取自 KillScore 常量，改表自动同步）
            bool rulesEnabled = GUI.enabled;
            GUI.enabled = rulesEnabled && enhanced;

            GUILayout.Label("积分规则", _sectionStyle);
            GUILayout.BeginHorizontal();
            DrawScoreCell("BOSS", KillScore.BossPoints);
            DrawScoreCell("风暴生物", KillScore.StormPoints);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            DrawScoreCell("雇佣兵/游荡者", KillScore.MercPoints);
            DrawScoreCell("其他生物", KillScore.CommonPoints);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            DrawScoreCell("精英（带★）额外", KillScore.StarBonus);
            GUILayout.EndHorizontal();
            GUILayout.Label("仅玩家击杀计分 · 本关用掉 1UP 后开始累计 · 满 100 分 +1UP（溢出分数结转）", _hintStyle);

            GUILayout.Space(8f);

            // 复活冷却只在增强模式开启时生效
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && enhanced;

            // 复活冷却（0/30/60/90，0 = 无冷却）
            GUILayout.Label("复活冷却（秒）", _sectionStyle);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (StepButton("−", cd > 0)) owner.ApplyPanelSettings(enhanced, cd == 90 ? 60 : cd - 30);
            DrawBadge(cd == 0 ? "无" : cd.ToString(), true);
            if (StepButton("+", cd < 90)) owner.ApplyPanelSettings(enhanced, cd == 0 ? 30 : cd + 30);
            GUILayout.EndHorizontal();

            GUI.enabled = previousEnabled;

            GUILayout.Space(10f);
            if (GUILayout.Button("关闭 (Ctrl+N)", _footerStyle, GUILayout.Height(34f))) Close();

            // 内容实测：取「关闭」按钮的下沿 + 上下内边距，供下一帧确定面板高度
            if (Event.current.type == EventType.Repaint)
            {
                _contentHeight = Mathf.Clamp(GUILayoutUtility.GetLastRect().yMax - area.y + 16f, 360f, 900f);
            }
            GUILayout.EndArea();
        }

        /// <summary>积分规则网格的半宽单元：类别名在左、分值在右。</summary>
        private static void DrawScoreCell(string name, int points)
        {
            GUILayout.Label(name, _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Space(20f);
            GUILayout.Label($"+{points}", _valueStyle);
            GUILayout.Space(20f);
        }

        /// <summary>
        /// 得分进度条：底槽 + 填充（比例 = 积分/100），数值文字叠在条中央。
        /// 满 100（刚发 1UP）填充为绿色，进行中为琥珀色；未开启增强模式时空槽 + 置灰文字。
        /// </summary>
        private static void DrawScoreProgress(Core.ScoreState score, bool enhanced)
        {
            var rect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            var bg = new Rect(rect.x, rect.y + 2f, rect.width, rect.height - 4f);

            var previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, enhanced ? 0.12f : 0.06f);   // 底槽
            GUI.DrawTexture(bg, Texture2D.whiteTexture, ScaleMode.StretchToFill);

            int points = score != null && enhanced ? score.Score : 0;
            float ratio = Mathf.Clamp01(points / (float)ScoreState.PointsPerUp);
            if (ratio > 0f)
            {
                GUI.color = points >= ScoreState.PointsPerUp
                    ? new Color(0.35f, 0.85f, 0.45f, 0.9f)     // 满 100：绿色（1UP 已发放）
                    : new Color(0.95f, 0.76f, 0.25f, 0.85f);   // 进行中：琥珀
                var fill = new Rect(bg.x + 1.5f, bg.y + 1.5f, Mathf.Max(0f, (bg.width - 3f) * ratio), bg.height - 3f);
                GUI.DrawTexture(fill, Texture2D.whiteTexture, ScaleMode.StretchToFill);
            }
            GUI.color = previous;

            string text = score == null ? "-" : (!enhanced ? "未开启（增强模式）" : $"{points} / {ScoreState.PointsPerUp}");
            GUI.Label(bg, text, _barTextStyle);
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

        private static void EnsureStyles()
        {
            if (_stylesReady) return;

            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 19, fontStyle = FontStyle.Bold };
            _versionStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleRight };
            _versionStyle.normal.textColor = new Color(1f, 1f, 1f, 0.55f);

            _sectionStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            _valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            _barTextStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
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
