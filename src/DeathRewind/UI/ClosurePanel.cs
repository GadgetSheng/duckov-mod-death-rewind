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
    ///   · 原生"继续"按钮**保持原样**（不收窄、不重排，避免与其父级布局组互相拉扯造成位移），
    ///     回溯按钮（克隆）排在其**下一行**：同宽、水平居中；
    ///   · 回溯按钮正下方：白色箭头（向上指向按钮）+ 白色提示文字；
    ///   · 屏幕（全屏 Canvas）左侧垂直居中：锚点时刻缩略图（16:9，随左侧空白区自适应）。
    ///
    /// 全部坐标都在结算界面根 rect 的本地空间里算，
    /// 几何计算在 Core/DeathPanelLayout（纯函数、可单测）里做，
    /// 这里只负责 Unity 对象的创建与 RectTransform 回写。
    /// 原生按钮被布局组件重排时会每帧重新对齐；
    /// 原生按钮自身的 Punch 缩放动画不会影响排版（宽度取自布局尺寸，不取缩放后的包围盒）。
    /// </summary>
    public sealed class ClosurePanel : IDisposable
    {
        private RectTransform _root;          // 结算界面根 rect（布局空间 + 注入对象的父节点）
        private RectTransform _nativeRect;    // 原生"继续"按钮
        private RectTransform _nativeParent;  // 原生按钮的父节点
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
            _root = viewRoot as RectTransform;
            if (_root == null) throw new InvalidOperationException("结算界面根节点缺少 RectTransform");

            _nativeRect = continueBtn.GetComponent<RectTransform>();
            _nativeParent = _nativeRect.parent as RectTransform;
            if (_nativeParent == null) throw new InvalidOperationException("原生按钮缺少 RectTransform 父节点");

            CreateRewindButton(continueBtn, rewindLabel);
            CreateHintText(continueBtn);
            CreateArrow();
            CreatePreview(previewPng);
            Layout();
        }

        private void CreateRewindButton(Button continueBtn, string rewindLabel)
        {
            // 克隆原生按钮保留原生样式（图片/字体/音效），挂在结算界面根节点下自行定位
            var cloneObj = UnityEngine.Object.Instantiate(continueBtn.gameObject, _root);
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
            _rewindRect.anchorMin = new Vector2(0.5f, 0.5f);
            _rewindRect.anchorMax = new Vector2(0.5f, 0.5f);
            _rewindRect.pivot = new Vector2(0.5f, 0.5f);

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
            go.transform.SetParent(_root, false);
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
            _textRect.anchorMin = new Vector2(0.5f, 0.5f);
            _textRect.anchorMax = new Vector2(0.5f, 0.5f);
            _textRect.pivot = new Vector2(0.5f, 0.5f);
        }

        /// <summary>指向回溯按钮的白色箭头（程序生成的三角贴图，垂直翻转后向上指向按钮）。</summary>
        private void CreateArrow()
        {
            _arrowTex = BuildTriangleTexture(64, 48);
            _arrowSprite = Sprite.Create(_arrowTex, new Rect(0, 0, _arrowTex.width, _arrowTex.height),
                new Vector2(0.5f, 0.5f), 100f);
            var go = new GameObject("DeathRewind_Arrow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(_root, false);
            var img = go.GetComponent<Image>();
            img.sprite = _arrowSprite;
            img.color = Color.white;
            img.raycastTarget = false;
            img.preserveAspect = true;
            _arrowRect = go.GetComponent<RectTransform>();
            _arrowRect.anchorMin = new Vector2(0.5f, 0.5f);
            _arrowRect.anchorMax = new Vector2(0.5f, 0.5f);
            _arrowRect.pivot = new Vector2(0.5f, 0.5f);
            _arrowRect.localScale = new Vector3(1f, -1f, 1f);   // 贴图朝下 → 翻转为朝上指向按钮
        }

        /// <summary>锚点时刻的缩略图（16:9），显示在屏幕左侧、垂直居中。</summary>
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
            go.transform.SetParent(_root, false);
            var img = go.GetComponent<RawImage>();
            img.texture = _previewTex;
            img.raycastTarget = false;
            _previewRect = go.GetComponent<RectTransform>();
            _previewRect.anchorMin = new Vector2(0.5f, 0.5f);
            _previewRect.anchorMax = new Vector2(0.5f, 0.5f);
            _previewRect.pivot = new Vector2(0.5f, 0.5f);
        }

        /// <summary>
        /// 重新排版：原生按钮被布局组件重排后调用，保证整组控件跟随。
        /// </summary>
        public void Layout()
        {
            if (_root == null || _nativeRect == null) return;

            RectF native = NativeRectInRootSpace();
            RectF canvas = RootRect();                          // 按钮行参照根节点 rect（与已验收布局完全一致）
            RectF previewCanvas = CanvasRectInRootSpace();      // 仅缩略图参照全屏 Canvas（屏幕左下角）
            if (native.Width <= 0f || native.Height <= 0f || canvas.Width <= 0f) return;

            var layout = DeathPanelLayout.Compute(native, canvas, previewCanvas);

            ApplyToChild(_rewindRect, layout.RewindButton);
            ApplyToChild(_arrowRect, layout.Arrow);
            ApplyToChild(_textRect, layout.HintText);
            if (_previewRect != null)
            {
                if (layout.PreviewSpaceShortfall > 0f)
                    Debug.Log($"[DeathRewind] 缩略图空间不足 {layout.PreviewSpaceShortfall:0}px，缩到 {layout.Preview.Height:0}px");
                ApplyToChild(_previewRect, layout.Preview);
            }
        }

        /// <summary>
        /// 原生按钮在根 rect 空间中的矩形。
        /// 宽度取自布局尺寸（sizeDelta × 父级缩放），不取缩放后的世界包围盒 ——
        /// 原生按钮带 Punch 缩放动画，用包围盒会让整组控件跟着抖。
        /// </summary>
        private RectF NativeRectInRootSpace()
        {
            float unitX = _nativeParent.lossyScale.x / Mathf.Max(_root.lossyScale.x, 1e-6f);
            float unitY = _nativeParent.lossyScale.y / Mathf.Max(_root.lossyScale.y, 1e-6f);
            float w = _nativeRect.sizeDelta.x * unitX;
            float h = _nativeRect.sizeDelta.y * unitY;

            // 中心：世界角点求中心（Punch 缩放以轴心为中心，中心不变）
            var corners = new Vector3[4];
            _nativeRect.GetWorldCorners(corners);
            var c = _root.InverseTransformPoint((corners[0] + corners[2]) * 0.5f);
            return new RectF(c.x - w * 0.5f, c.y - h * 0.5f, c.x + w * 0.5f, c.y + h * 0.5f);
        }

        /// <summary>结算界面根 rect 在自身空间的边界。</summary>
        private RectF RootRect()
        {
            var r = _root.rect;
            return new RectF(r.xMin, r.yMin, r.xMax, r.yMax);
        }

        /// <summary>
        /// Canvas（全屏）矩形换算到根节点本地空间。
        /// ClosureView 根节点不一定是全屏 rect；"窗口左下角"必须以 Canvas 为准，
        /// 否则预览图会贴在根节点（可能是内嵌面板）的左下角而不是屏幕左下角。
        /// </summary>
        private RectF CanvasRectInRootSpace()
        {
            var canvas = _root.GetComponentInParent<Canvas>();
            var crt = canvas != null ? canvas.transform as RectTransform : null;
            if (crt == null || crt == _root) return RootRect();

            var corners = new Vector3[4];
            crt.GetWorldCorners(corners);
            var a = _root.InverseTransformPoint(corners[0]);   // 左下
            var b = _root.InverseTransformPoint(corners[2]);   // 右上
            return new RectF(
                Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        /// <summary>把根空间里的目标矩形写回注入对象（父节点就是根 rect）。</summary>
        private void ApplyToChild(RectTransform rect, RectF target)
        {
            if (rect == null) return;
            var parentRect = rect.parent as RectTransform;
            if (parentRect == null) return;
            float unitX = _root.lossyScale.x / Mathf.Max(parentRect.lossyScale.x, 1e-6f);
            float unitY = _root.lossyScale.y / Mathf.Max(parentRect.lossyScale.y, 1e-6f);
            float w = target.Width * unitX;
            float h = target.Height * unitY;
            rect.sizeDelta = new Vector2(w, h);

            var anchorPoint = new Vector2(
                parentRect.rect.xMin + rect.anchorMin.x * parentRect.rect.width,
                parentRect.rect.yMin + rect.anchorMin.y * parentRect.rect.height);
            var pivotPoint = new Vector2(
                target.XMin + rect.pivot.x * w,
                target.YMin + rect.pivot.y * h);
            rect.anchoredPosition = pivotPoint - anchorPoint;
        }

        /// <summary>把根空间里的矩形换算到某个父节点的本地空间。</summary>
        private RectF ToParentSpace(RectF target, RectTransform parent)
        {
            Vector3 worldMin = _root.TransformPoint(new Vector3(target.XMin, target.YMin, 0f));
            Vector3 worldMax = _root.TransformPoint(new Vector3(target.XMax, target.YMax, 0f));
            Vector3 localMin = parent.InverseTransformPoint(worldMin);
            Vector3 localMax = parent.InverseTransformPoint(worldMax);
            return new RectF(
                Mathf.Min(localMin.x, localMax.x), Mathf.Min(localMin.y, localMax.y),
                Mathf.Max(localMin.x, localMax.x), Mathf.Max(localMin.y, localMax.y));
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
            if (_rewindRect != null)
            {
                UnityEngine.Object.Destroy(_rewindRect.gameObject);
                _rewindRect = null;
            }
            if (_textRect != null)
            {
                UnityEngine.Object.Destroy(_textRect.gameObject);
                _textRect = null;
            }
            if (_arrowRect != null)
            {
                UnityEngine.Object.Destroy(_arrowRect.gameObject);
                _arrowRect = null;
            }
            if (_previewRect != null)
            {
                UnityEngine.Object.Destroy(_previewRect.gameObject);
                _previewRect = null;
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
            _nativeRect = null;
            _nativeParent = null;
            _root = null;
        }
    }
}
