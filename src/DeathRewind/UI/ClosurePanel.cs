using System;
using DeathRewind.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DeathRewind.UI
{
    /// <summary>
    /// 注入到原生 YOU DIED 结算界面（ClosureView）上的回溯控件组。
    ///
    /// 布局：
    ///   · 一行：[原生"继续"按钮] [回溯 N 秒按钮]（并排，整行尽量居中）
    ///   · 回溯按钮正上方：白色提示文字 + 指向按钮的白色箭头
    ///   · 按钮行下方：锚点时刻缩略图，按剩余空间尽量放大（按钮合并成一行后腾出的竖向空间）
    ///
    /// 几何计算在 Core/DeathPanelLayout（纯函数、可单测）里做，这里只负责
    /// Unity 对象的创建与坐标回写。所有坐标都在自建容器的本地空间里算，
    /// 因此原生按钮被布局组件重排后仍能跟随；原生按钮自身的 Punch 缩放动画
    /// 不会让整组控件抖动（测量用的是布局尺寸而非缩放后的包围盒）。
    /// </summary>
    public sealed class ClosurePanel : IDisposable
    {
        private RectTransform _container;
        private RectTransform _nativeRect;   // 原生"继续"按钮
        private RectTransform _viewRect;     // 结算界面根 rect（画布边界）
        private RectTransform _rewindRect;
        private RectTransform _textRect;
        private RectTransform _arrowRect;
        private RectTransform _previewRect;
        private Button _rewindButton;
        private Texture2D _previewTex;
        private Texture2D _arrowTex;
        private Sprite _arrowSprite;

        /// <summary>注入的回溯按钮（供外部挂 onClick）。</summary>
        public Button RewindButton => _rewindButton;

        /// <summary>缩略图是否成功显示。</summary>
        public bool HasPreview => _previewRect != null;

        /// <summary>
        /// 在原生结算界面里注入回溯控件组。
        /// viewRoot：结算界面根节点（ClosureView 的 Transform）。
        /// continueBtn：原生"继续"按钮。
        /// </summary>
        public static ClosurePanel Create(Transform viewRoot, Button continueBtn, string rewindLabel, byte[] previewPng)
        {
            var panel = new ClosurePanel();
            try
            {
                panel.Build(viewRoot, continueBtn, rewindLabel, previewPng);
                return panel;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 回溯控件注入失败: {e.Message}");
                panel.Dispose();
                return null;
            }
        }

        private void Build(Transform viewRoot, Button continueBtn, string rewindLabel, byte[] previewPng)
        {
            if (viewRoot == null || continueBtn == null) throw new ArgumentNullException(nameof(viewRoot));

            _nativeRect = continueBtn.GetComponent<RectTransform>();
            _viewRect = viewRoot as RectTransform;

            // 自建容器：父节点是结算界面根 rect，本地原点落在左下角，便于直接用本地坐标排版。
            var containerObj = new GameObject("DeathRewind_Panel", typeof(RectTransform));
            _container = containerObj.GetComponent<RectTransform>();
            _container.SetParent(viewRoot, false);
            _container.anchorMin = Vector2.zero;
            _container.anchorMax = Vector2.zero;
            _container.pivot = Vector2.zero;
            _container.sizeDelta = Vector2.zero;
            _container.anchoredPosition = Vector2.zero;
            _container.localScale = Vector3.one;

            CreateRewindButton(continueBtn, rewindLabel);
            CreateHintText(continueBtn);
            CreateArrow();
            CreatePreview(previewPng);
            Layout();
        }

        private void CreateRewindButton(Button continueBtn, string rewindLabel)
        {
            // 克隆原生按钮保留原生样式（图片/字体/音效），但换到自建容器里，脱离原生布局。
            var cloneObj = UnityEngine.Object.Instantiate(continueBtn.gameObject, _container);
            cloneObj.name = "DeathRewind_RewindButton";
            cloneObj.transform.localScale = Vector3.one;

            var btn = cloneObj.GetComponent<Button>();
            if (btn == null)
            {
                UnityEngine.Object.Destroy(cloneObj);
                throw new InvalidOperationException("原生按钮缺少 Button 组件");
            }
            btn.onClick.RemoveAllListeners();
            _rewindButton = btn;
            _rewindRect = cloneObj.GetComponent<RectTransform>();

            var tmp = cloneObj.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) tmp.text = rewindLabel;
        }

        /// <summary>
        /// 白色手写提示文字：字体参考结算界面上方的大字（UGLY 标题），显示在回溯按钮正上方。
        /// </summary>
        private void CreateHintText(Button continueBtn)
        {
            var reference = FindReferenceFont(continueBtn);
            var go = new GameObject("DeathRewind_Hint", typeof(RectTransform));
            go.transform.SetParent(_container, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "一次机会！";
            tmp.color = Color.white;
            tmp.fontSize = 44f;
            tmp.enableWordWrapping = false;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            if (reference != null)
            {
                tmp.font = reference.font;
                tmp.fontSharedMaterial = reference.fontSharedMaterial;
            }
            _textRect = go.GetComponent<RectTransform>();
        }

        /// <summary>指向回溯按钮的白色箭头（程序生成的向下三角贴图，不依赖字体是否有箭头字形）。</summary>
        private void CreateArrow()
        {
            _arrowTex = BuildTriangleTexture(64, 48);
            _arrowSprite = Sprite.Create(_arrowTex, new Rect(0, 0, _arrowTex.width, _arrowTex.height),
                new Vector2(0.5f, 0.5f), 100f);
            var go = new GameObject("DeathRewind_Arrow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(_container, false);
            var img = go.GetComponent<Image>();
            img.sprite = _arrowSprite;
            img.color = Color.white;
            img.raycastTarget = false;
            img.preserveAspect = true;
            _arrowRect = go.GetComponent<RectTransform>();
        }

        /// <summary>锚点时刻的缩略图（16:9），显示在按钮行下方。</summary>
        private void CreatePreview(byte[] png)
        {
            if (png == null || png.Length == 0) return;
            _previewTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!_previewTex.LoadImage(png))
            {
                UnityEngine.Object.Destroy(_previewTex);
                _previewTex = null;
                return;
            }

            var go = new GameObject("DeathRewind_Preview", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(_container, false);
            var img = go.GetComponent<RawImage>();
            img.texture = _previewTex;
            img.raycastTarget = false;
            _previewRect = go.GetComponent<RectTransform>();
        }

        /// <summary>
        /// 重新排版：原生按钮被布局组件重排 / Punch 动画缩放后调用，保证整组控件跟随。
        /// </summary>
        public void Layout()
        {
            if (_container == null || _nativeRect == null) return;

            RectF native = NativeLayoutRect();
            RectF bounds = LocalBounds();
            if (native.Width <= 0f || native.Height <= 0f || bounds.Width <= 0f) return;

            var layout = DeathPanelLayout.Compute(native, bounds);

            SetAnchored(_rewindRect, layout.RewindButton);
            SetAnchored(_arrowRect, layout.Arrow);
            SetAnchored(_textRect, layout.HintText);
            if (_previewRect != null)
            {
                if (layout.PreviewSpaceShortfall > 0f)
                    Debug.Log($"[DeathRewind] 缩略图空间不足 {layout.PreviewSpaceShortfall:0}px，缩到 {layout.Preview.Height:0}px");
                SetAnchored(_previewRect, layout.Preview);
            }
        }

        /// <summary>
        /// 原生按钮在本容器本地空间中的布局矩形。
        /// 用布局尺寸（rect.size）而不是缩放后的包围盒：原生按钮有 Punch 缩放动画，
        /// 用包围盒会让整组控件跟着抖。
        /// </summary>
        private RectF NativeLayoutRect()
        {
            var rect = _nativeRect.rect;
            // 把 native 所在分支的额外缩放折算掉（Content/ButtonContainer 一般是 1）
            float sx = _nativeRect.lossyScale.x / Mathf.Max(_container.lossyScale.x, 1e-6f);
            float sy = _nativeRect.lossyScale.y / Mathf.Max(_container.lossyScale.y, 1e-6f);
            float w = rect.width * sx;
            float h = rect.height * sy;

            // 中心点：TransformPoint 不受自身缩放影响（Punch 动画不会让中心漂移）
            Vector3 centerWorld = _nativeRect.TransformPoint(rect.center);
            Vector3 centerLocal = _container.InverseTransformPoint(centerWorld);
            return new RectF(centerLocal.x - w * 0.5f, centerLocal.y - h * 0.5f,
                centerLocal.x + w * 0.5f, centerLocal.y + h * 0.5f);
        }

        /// <summary>结算界面（画布）的本地边界。</summary>
        private RectF LocalBounds()
        {
            if (_viewRect == null) return new RectF(0, 0, Screen.width, Screen.height);
            var corners = new Vector3[4];
            _viewRect.GetWorldCorners(corners);
            var p0 = _container.InverseTransformPoint(corners[0]);
            var p2 = _container.InverseTransformPoint(corners[2]);
            float xMin = Mathf.Min(p0.x, p2.x);
            float xMax = Mathf.Max(p0.x, p2.x);
            float yMin = Mathf.Min(p0.y, p2.y);
            float yMax = Mathf.Max(p0.y, p2.y);
            return new RectF(xMin, yMin, xMax, yMax);
        }

        private static void SetAnchored(RectTransform rect, RectF r)
        {
            if (rect == null) return;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.sizeDelta = new Vector2(r.Width, r.Height);
            rect.anchoredPosition = new Vector2(r.XMin, r.YMin);
        }

        /// <summary>找参考字体：优先结算界面上"UGLY"标题那种手写大字，否则取字号最大的文字。</summary>
        private static TextMeshProUGUI FindReferenceFont(Button continueBtn)
        {
            var canvas = continueBtn.transform.root;
            var all = canvas.GetComponentsInChildren<TextMeshProUGUI>(true);
            TextMeshProUGUI reference = null;
            foreach (var t in all)
            {
                if (t.text != null && t.text.IndexOf("UGLY", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reference = t;
                    break;
                }
            }
            if (reference == null)
            {
                float best = 0f;
                foreach (var t in all)
                {
                    if (t.fontSize > best && t.isActiveAndEnabled) { best = t.fontSize; reference = t; }
                }
            }
            return reference;
        }

        /// <summary>生成白色向下三角贴图（避免依赖字体里的箭头字形）。</summary>
        private static Texture2D BuildTriangleTexture(int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            // 顶点：左上、右上、底部中央（像素坐标，y 向上）
            var a = new Vector2(0f, height);
            var b = new Vector2(width, height);
            var c = new Vector2(width * 0.5f, 0f);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    bool inside = PointInTriangle(p, a, b, c);
                    pixels[y * width + x] = inside ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b);
            float d2 = Sign(p, b, c);
            float d3 = Sign(p, c, a);
            bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(hasNeg && hasPos);
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
            => (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);

        public void Dispose()
        {
            if (_container != null)
            {
                UnityEngine.Object.Destroy(_container.gameObject);
                _container = null;
            }
            if (_previewTex != null)
            {
                UnityEngine.Object.Destroy(_previewTex);
                _previewTex = null;
            }
            if (_arrowSprite != null)
            {
                UnityEngine.Object.Destroy(_arrowSprite);
                _arrowSprite = null;
            }
            if (_arrowTex != null)
            {
                UnityEngine.Object.Destroy(_arrowTex);
                _arrowTex = null;
            }
            _rewindButton = null;
            _rewindRect = null;
            _textRect = null;
            _arrowRect = null;
            _previewRect = null;
            _nativeRect = null;
            _viewRect = null;
        }
    }
}
