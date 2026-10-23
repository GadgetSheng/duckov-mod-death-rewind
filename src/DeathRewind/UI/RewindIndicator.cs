using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DeathRewind.UI
{
    /// <summary>
    /// HUD 左侧状态栏的"1up"指示图标：
    /// 本 Mod 生效（还有一次回溯机会）时，在左侧状态栏显示一枚硬币；
    /// 回溯机会用掉（回溯次数达到上限）后图标消失。
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
        private static Texture2D _coinTex;
        private static Sprite _coinSprite;
        private static bool _visible;
        private static float _retryNotBefore;   // HUD 未就绪时的重试节流（每帧递归查找太贵）

        /// <summary>当前是否已显示（供日志/测试查询）。</summary>
        public static bool Visible => _visible;

        /// <summary>
        /// 设置指示图标状态。available=true 显示硬币，false 隐藏。
        /// 每帧调用开销很小：状态没变时直接返回。
        /// 若底层 GameObject 因场景切换等原因被销毁，会在下次 EnsureCreated 时重建（隐藏态下重建后立即 SetActive(false)）。
        /// </summary>
        public static void SetAvailable(bool available)
        {
            if (_visible == available && (_row != null || !available)) return;
            if (available)
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
            if (_visible != available)
            {
                ModLog.Log($"[DR-Trace] RewindIndicator SetAvailable({available}) — 可见性 {_visible}→{available}");
            }
            _visible = available;
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
            _icon = null;
            _visible = false;
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
            tmp.richText = false;                    // 禁富文本，防父级/字体资产注入 <s>/<strikethrough>
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.raycastTarget = false;
            var labelRect = (RectTransform)labelObj.transform;
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.sizeDelta = new Vector2(90f, RowHeight);
            labelRect.anchoredPosition = new Vector2(IconOffsetX + IconSize * 0.5f + 8f, IconOffsetY);

            // 排在状态栏最上方（原生条目是追加到末尾的）
            rowRect.SetAsFirstSibling();
            _row.SetActive(_visible);
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
