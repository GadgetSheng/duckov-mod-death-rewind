using System;
using DeathRewind.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DeathRewind.UI
{
    /// <summary>
    /// HUD 左侧状态栏的"1up"指示图标，四态：
    /// 可用（硬币全亮 + "1UP"）/ 冷却中（置灰 + 倒计时秒数）/
    /// 用掉（仅增强模式：置灰 + 删除线"1UP"+ 当前积分）/ 隐藏。
    ///
    /// 挂载点：HUDCanvas 下名为 "Buffs" 的竖向状态栏容器（原生状态/增益图标就排在这里）。
    /// 我们的图标作为该容器的**第一个子物体**加入，尺寸与原生条目一致，
    /// 因此由原生 VerticalLayoutGroup 统一排版：
    ///   · 永远排在状态栏最上方（原生条目是 SetAsLastSibling 追加的）
    ///   · 状态栏向上生长时不会被压到 HP 条上
    /// </summary>
    public static class RewindIndicator
    {
        private const string HudCanvasName = "HUDCanvas";
        private const string StatusColumnName = "Buffs";
        private const float RowHeight = 50f;      // 与原生 BuffsDisplayEntry 行高一致
        private const float IconSize = 40f;
        private const float IconOffsetX = 35f;     // 与原生 IconContainer 的横向偏移一致
        private const float IconOffsetY = -22f;

        private static GameObject _row;
        private static Image _icon;
        private static TextMeshProUGUI _label;
        private static Texture2D _coinTex;
        private static Sprite _coinSprite;
        private static Texture2D _lockTex;
        private static Sprite _lockSprite;
        private static bool _visible;
        private static string _lastText;        // 外观缓存：文案+颜色没变就跳过 TMP 重建
        private static Color _lastColor = Color.white;
        private static float _retryNotBefore;   // HUD 未就绪时的重试节流（每帧递归查找太贵）

        /// <summary>当前是否已显示（供日志/测试查询）。</summary>
        public static bool Visible => _visible;

        /// <summary>
        /// 更新指示器四态（每帧调用，状态未变时开销极小）：
        /// · visible=false → 隐藏（1UP 用完且非增强 / 基地 / 环未就绪）
        /// · 有 1UP 且 ready → 硬币全亮 + "1UP"
        /// · 有 1UP 且冷却中 → 硬币置灰 + 删除线"1UP"+ 倒计时"Ns"（重新获得的 1UP 未成熟）
        /// · 无 1UP（仅增强模式）→ 硬币置灰 + 删除线"1UP"+ 当前积分（如 "1̶U̶P̶ 45"）
        /// 若底层 GameObject 因场景切换等原因被销毁，会在下次 EnsureCreated 时重建。
        /// </summary>
        public static void SetState(ScoreState score, bool visible, bool ready, double remainSeconds)
        {
            bool hasUp = score != null && score.Held > 0;
            bool enhanced = score != null && score.Enhanced;
            int progress = score != null ? score.Score : 0;

            if (_visible == visible && (_row != null || !visible))
            {
                if (visible) ApplyVisual(hasUp, ready, remainSeconds, progress, enhanced);
                return;
            }
            if (visible)
            {
                if (!EnsureCreated())
                {
                    _visible = false;
                    return;
                }
                _row.SetActive(true);
            }
            else
            {
                // 隐藏态：若 row 已销毁（场景切换）就 leave 它销毁，EnsureCreated 下一次 visible=true 时会重建。
                // 但必须把 _visible 设为 false，避免下次 Update 仍返回 true 时 early-return。
                if (_row != null) _row.SetActive(false);
            }
            if (_visible != visible)
            {
                ModLog.Log($"[DR-Trace] RewindIndicator SetState — 可见性 {_visible}→{visible}");
            }
            _visible = visible;
            if (visible) ApplyVisual(hasUp, ready, remainSeconds, progress, enhanced);
        }

        /// <summary>四态外观；文案没变（含倒计时秒数、积分）时跳过 TMP 重建。</summary>
        private static void ApplyVisual(bool hasUp, bool ready, double remainSeconds, int progress, bool enhanced)
        {
            if (_icon != null)
            {
                bool locked = !hasUp;   // 攒积分态：锁图标（不透明）；有 1UP 态：硬币
                if (locked && _lockSprite != null && _icon.sprite != _lockSprite) _icon.sprite = _lockSprite;
                if (!locked && _coinSprite != null && _icon.sprite != _coinSprite) _icon.sprite = _coinSprite;
                _icon.color = (!hasUp || ready) ? Color.white : new Color(0.55f, 0.55f, 0.55f, 0.85f);
            }
            if (_label == null) return;

            string text;
            Color color;
            if (hasUp && ready)
            {
                text = "1UP";
                color = Color.white;
            }
            else if (hasUp)
            {
                // 冷却中（重新获得 1UP 后 CD 未归零）：这枚 1UP 还不可用 → 保留删除线 + 倒计时
                text = $"<s>1UP</s> {Mathf.CeilToInt((float)remainSeconds)}s";
                color = new Color(1f, 1f, 1f, 0.6f);
            }
            else if (enhanced)
            {
                // 攒积分态：仅 "1UP" 字样带删除线，后跟 当前积分/100（锁图标，不透明）
                text = $"<s>1UP</s> {progress}/{ScoreState.PointsPerUp}";
                color = Color.white;
            }
            else
            {
                return;   // 非增强且无 1UP：调用方不会给 visible=true，保险起见不动外观
            }

            if (text == _lastText && color == _lastColor) return;
            _lastText = text;
            _lastColor = color;
            _label.text = text;
            _label.color = color;
        }

        /// <summary>销毁图标（Mod 停用时调用）。</summary>
        public static void Dispose()
        {
            if (_row != null)
            {
                UnityEngine.Object.Destroy(_row);
                _row = null;
            }
            if (_coinSprite != null)
            {
                UnityEngine.Object.Destroy(_coinSprite);
                _coinSprite = null;
            }
            if (_coinTex != null)
            {
                UnityEngine.Object.Destroy(_coinTex);
                _coinTex = null;
            }
            if (_lockSprite != null)
            {
                UnityEngine.Object.Destroy(_lockSprite);
                _lockSprite = null;
            }
            if (_lockTex != null)
            {
                UnityEngine.Object.Destroy(_lockTex);
                _lockTex = null;
            }
            _icon = null;
            _label = null;
            _visible = false;
            _lastText = null;
            _lastColor = Color.white;
        }

        private static bool EnsureCreated()
        {
            if (_row != null) return true;
            if (Time.unscaledTime < _retryNotBefore) return false;
            _retryNotBefore = Time.unscaledTime + 1f;   // 失败后 1s 内不再全场景递归查找

            var hud = FindHudCanvas();
            if (hud == null) return false;

            var column = FindStatusColumn(hud);
            if (column == null) return false;

            // 与原生 BuffsDisplayEntry 相同的行布局（锚点/轴心/行高），交给原生布局组排版
            _row = new GameObject("DeathRewind_1Up", typeof(RectTransform));
            var rowRect = _row.GetComponent<RectTransform>();
            rowRect.SetParent(column, false);
            rowRect.anchorMin = Vector2.zero;
            rowRect.anchorMax = Vector2.zero;
            rowRect.pivot = new Vector2(0f, 0.5f);
            rowRect.sizeDelta = new Vector2(0f, RowHeight);
            rowRect.anchoredPosition = Vector2.zero;

            // 硬币图标
            _coinTex = BuildCoinTexture(96);
            _coinSprite = Sprite.Create(_coinTex, new Rect(0, 0, _coinTex.width, _coinTex.height),
                new Vector2(0.5f, 0.5f), 100f);
            // 锁图标（攒积分状态）
            _lockTex = BuildLockTexture(96);
            _lockSprite = Sprite.Create(_lockTex, new Rect(0, 0, _lockTex.width, _lockTex.height),
                new Vector2(0.5f, 0.5f), 100f);
            var iconObj = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            iconObj.transform.SetParent(rowRect, false);
            var iconRect = iconObj.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 1f);
            iconRect.anchorMax = new Vector2(0f, 1f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.sizeDelta = new Vector2(IconSize, IconSize);
            iconRect.anchoredPosition = new Vector2(IconOffsetX, IconOffsetY);
            _icon = iconObj.GetComponent<Image>();
            _icon.sprite = _coinSprite;
            _icon.raycastTarget = false;
            _icon.preserveAspect = true;

            // "1UP" 文字标签（硬币右侧，白色粗体）
            var labelObj = new GameObject("Label", typeof(RectTransform));
            labelObj.transform.SetParent(rowRect, false);
            var tmp = labelObj.AddComponent<TextMeshProUGUI>();
            tmp.text = "1UP";
            tmp.color = Color.white;
            tmp.fontSize = 24f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.richText = true;                     // 用掉态要显示删除线 1UP（<s> 标签）
            tmp.overflowMode = TextOverflowModes.Overflow;   // "1UP 100" 比 1UP 宽，溢出而非换行
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.raycastTarget = false;
            var labelRect = (RectTransform)labelObj.transform;
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.sizeDelta = new Vector2(90f, RowHeight);
            labelRect.anchoredPosition = new Vector2(IconOffsetX + IconSize * 0.5f + 8f, IconOffsetY);
            _label = tmp;

            // 排在状态栏最上方（原生条目是追加到末尾的）
            rowRect.SetAsFirstSibling();
            _row.SetActive(_visible);
            _lastText = null;   // 重建后强制刷新一次外观缓存
            _lastColor = Color.white;
            return true;
        }

        private static GameObject FindHudCanvas()
        {
            var lm = LevelManager.Instance;
            if (lm != null)
            {
                var found = FindChild(lm.transform, HudCanvasName);
                if (found != null) return found.gameObject;
            }
            return GameObject.Find(HudCanvasName);
        }

        /// <summary>状态栏容器：HUDCanvas 下名为 "Buffs" 且带竖向布局组的那个（原生状态图标所在）。</summary>
        private static RectTransform FindStatusColumn(GameObject hud)
        {
            var groups = hud.GetComponentsInChildren<VerticalLayoutGroup>(true);
            foreach (var g in groups)
            {
                if (g.gameObject.name == StatusColumnName) return (RectTransform)g.transform;
            }
            var named = FindChild(hud.transform, StatusColumnName);
            return named as RectTransform;
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (root == null) return null;
            foreach (Transform c in root)
            {
                if (c.name == name) return c;
            }
            foreach (Transform c in root)
            {
                var deep = FindChild(c, name);
                if (deep != null) return deep;
            }
            return null;
        }

        /// <summary>程序化画一把白色挂锁（攒积分状态图标：机会已锁定，积分攒满 100 解锁）。</summary>
        private static Texture2D BuildLockTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            float cx = size * 0.5f;
            float ringR = size * 0.25f, ringHalf = size * 0.05f;    // 环半径与半带宽
            float bodyTop = size * 0.52f, bodyBot = size * 0.08f;   // 锁体上下缘
            float bodyL = size * 0.16f, bodyR = size * 0.84f;       // 锁体左右缘
            float holeR = size * 0.07f, holeCy = size * 0.34f;      // 锁孔
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    bool on;
                    // 锁环：以锁体顶中点为圆心的上半圆环
                    float dx = fx - cx, dy = fy - bodyTop;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    on = dy > 0f && d >= ringR - ringHalf && d <= ringR + ringHalf;
                    // 锁体
                    if (!on) on = fx >= bodyL && fx <= bodyR && fy >= bodyBot && fy <= bodyTop;
                    // 锁孔镂空
                    if (on)
                    {
                        float hx = fx - cx, hy = fy - holeCy;
                        if (hx * hx + hy * hy < holeR * holeR) on = false;
                    }
                    px[y * size + x] = on ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        /// <summary>程序生成的金币贴图（不依赖游戏素材，避免图标资源缺失）。</summary>
        private static Texture2D BuildCoinTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var pixels = new Color32[size * size];
            float r = size * 0.5f;
            float outer = r - 1f;
            float rim = r * 0.86f;     // 外圈边缘
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r;
                    float dy = y + 0.5f - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color32 c;
                    if (d > outer)
                    {
                        c = new Color32(0, 0, 0, 0);
                    }
                    else if (d > rim)
                    {
                        // 外圈：深金
                        c = new Color32(150, 104, 24, 255);
                    }
                    else
                    {
                        // 币面：中心亮、边缘暗，带一点左上高光
                        float t = d / rim;
                        float shade = Mathf.Lerp(1f, 0.72f, t);
                        float hi = Mathf.Clamp01(1f - Mathf.Sqrt((dx + outer * 0.28f) * (dx + outer * 0.28f) +
                                                              (dy + outer * 0.28f) * (dy + outer * 0.28f)) / (outer * 0.6f));
                        float v = shade * Mathf.Lerp(1f, 1.25f, hi * 0.6f);
                        c = new Color32((byte)Mathf.Clamp(255 * v, 0, 255),
                                        (byte)Mathf.Clamp(196 * v, 0, 255),
                                        (byte)Mathf.Clamp(72 * v, 0, 255), 255);
                    }
                    pixels[y * size + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }
    }
}
