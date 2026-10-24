using System;
using System.Collections.Generic;
using DeathRewind.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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
    ///   · 屏幕（全屏 Canvas）左侧垂直居中：锚点时刻缩略图（16:9，随左侧空白区自适应）；
    ///   · 缩略图正下方：回溯时间轴（1~4 个节点，旧→新），可拖拽选择；
    ///     选中节点的缩略图显示在大图上，回溯按钮上的秒数同步更新。
    ///
    /// 全部坐标都在结算界面根 rect 的本地空间里算，
    /// 几何计算在 Core/DeathPanelLayout + Core/TimelineLayout（纯函数、可单测）里做，
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
        private RectTransform _timelineRect;
        private RectTransform _dragRect;
        private Image _timelineStrip;
        private Image[] _timelineNodes;
        private TextMeshProUGUI[] _timelineLabels;
        private float[] _timelineCenters;
        private Button _rewindButton;
        private Texture2D _previewTex;
        private Texture2D _arrowTex;
        private Sprite _arrowSprite;
        private Texture2D _nodeTex;
        private Sprite _nodeSprite;
        private List<RewindCandidate> _candidates = new List<RewindCandidate>();
        private int _selectedIndex = -1;

        /// <summary>注入的回溯按钮（供外部挂 onClick）。</summary>
        public Button RewindButton => _rewindButton;

        /// <summary>缩略图是否成功显示。</summary>
        public bool HasPreview => _previewRect != null && _previewTex != null;

        /// <summary>当前选中的时间轴节点索引（-1 = 无候选）。</summary>
        public int SelectedIndex => _selectedIndex;

        /// <summary>候选节点数量（时间轴节点数）。</summary>
        public int CandidateCount => _candidates.Count;

        /// <summary>选中节点变化时触发（参数为节点索引，旧→新顺序）。</summary>
        public event Action<int> NodeSelected;

        /// <summary>
        /// 在原生结算界面里注入回溯控件组。
        /// viewRoot：结算界面根节点（ClosureView 的 Transform）。
        /// continueBtn：原生"继续"按钮。
        /// candidates：可回溯候选节点（1~4 个，旧→新）。
        /// </summary>
        public static ClosurePanel Create(Transform viewRoot, Button continueBtn, string rewindLabel,
                                          IReadOnlyList<RewindCandidate> candidates)
        {
            var panel = new ClosurePanel();
            try
            {
                panel.Build(viewRoot, continueBtn, rewindLabel, candidates);
                return panel;
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 回溯控件注入失败: {e.Message}");
                panel.Dispose();
                return null;
            }
        }

        private void Build(Transform viewRoot, Button continueBtn, string rewindLabel,
                           IReadOnlyList<RewindCandidate> candidates)
        {
            if (viewRoot == null || continueBtn == null) throw new ArgumentNullException(nameof(viewRoot));
            _root = viewRoot as RectTransform;
            if (_root == null) throw new InvalidOperationException("结算界面根节点缺少 RectTransform");

            _nativeRect = continueBtn.GetComponent<RectTransform>();
            _nativeParent = _nativeRect.parent as RectTransform;
            if (_nativeParent == null) throw new InvalidOperationException("原生按钮缺少 RectTransform 父节点");

            _candidates = candidates != null ? new List<RewindCandidate>(candidates) : new List<RewindCandidate>();
            // 默认选**最老**的节点（索引 0）：与 DeathInterceptor 的默认回溯目标一致，
            // 保证"预览画面 / 按钮秒数 / 实际回溯目标"三者指向同一个锚点。
            _selectedIndex = _candidates.Count > 0 ? 0 : -1;

            CreateRewindButton(continueBtn, rewindLabel);
            CreateHintText(continueBtn);
            CreateArrow();
            CreatePreview(SelectedPreviewPng());
            CreateTimeline();
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

        /// <summary>更新回溯按钮上的文案（选中节点变化时同步秒数）。</summary>
        public void SetRewindLabel(string label)
        {
            var tmp = _rewindButton != null ? _rewindButton.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            if (tmp != null) tmp.text = label;
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
            var go = new GameObject("DeathRewind_Preview", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(_root, false);
            var img = go.GetComponent<RawImage>();
            img.raycastTarget = false;
            _previewRect = go.GetComponent<RectTransform>();
            _previewRect.anchorMin = new Vector2(0.5f, 0.5f);
            _previewRect.anchorMax = new Vector2(0.5f, 0.5f);
            _previewRect.pivot = new Vector2(0.5f, 0.5f);
            ApplyPreviewTexture(png);
        }

        /// <summary>把候选节点的缩略图换到大图上（选中节点变化时调用）。</summary>
        private void ApplyPreviewTexture(byte[] png)
        {
            if (_previewRect == null) return;
            var img = _previewRect.GetComponent<RawImage>();
            if (img == null) return;

            if (_previewTex != null)
            {
                UnityEngine.Object.Destroy(_previewTex);
                _previewTex = null;
            }
            if (png == null || png.Length == 0)
            {
                img.texture = null;
                return;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(png))
            {
                UnityEngine.Object.Destroy(tex);
                img.texture = null;
                return;
            }
            _previewTex = tex;
            img.texture = tex;
        }

        /// <summary>
        /// 回溯时间轴：横条 + 1~4 个节点圆点 + 每个节点的"距今多少秒"标签，
        /// 拖拽热区覆盖整条时间轴（EventTrigger：按下 / 拖动 → 吸附到最近节点）。
        /// </summary>
        private void CreateTimeline()
        {
            if (_candidates.Count == 0) return;

            var stripGo = new GameObject("DeathRewind_Timeline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            stripGo.transform.SetParent(_root, false);
            _timelineStrip = stripGo.GetComponent<Image>();
            _timelineStrip.color = new Color(1f, 1f, 1f, 0.35f);
            _timelineStrip.raycastTarget = false;
            _timelineRect = stripGo.GetComponent<RectTransform>();
            _timelineRect.anchorMin = new Vector2(0.5f, 0.5f);
            _timelineRect.anchorMax = new Vector2(0.5f, 0.5f);
            _timelineRect.pivot = new Vector2(0.5f, 0.5f);

            _nodeTex = BuildCircleTexture(64);
            _nodeSprite = Sprite.Create(_nodeTex, new Rect(0, 0, _nodeTex.width, _nodeTex.height),
                new Vector2(0.5f, 0.5f), 100f);

            var reference = _rewindButton != null
                ? FindReferenceFont(_rewindButton)
                : null;

            _timelineNodes = new Image[_candidates.Count];
            _timelineLabels = new TextMeshProUGUI[_candidates.Count];
            for (int i = 0; i < _candidates.Count; i++)
            {
                var nodeGo = new GameObject("Node", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                nodeGo.transform.SetParent(_root, false);
                var nodeImg = nodeGo.GetComponent<Image>();
                nodeImg.sprite = _nodeSprite;
                nodeImg.raycastTarget = false;
                var nodeRect = nodeGo.GetComponent<RectTransform>();
                nodeRect.anchorMin = new Vector2(0.5f, 0.5f);
                nodeRect.anchorMax = new Vector2(0.5f, 0.5f);
                nodeRect.pivot = new Vector2(0.5f, 0.5f);
                _timelineNodes[i] = nodeImg;

                var labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(_root, false);
                var tmp = labelGo.AddComponent<TextMeshProUGUI>();
                tmp.text = $"{_candidates[i].AgeSeconds:0}秒前";
                tmp.color = new Color(1f, 1f, 1f, 0.8f);
                tmp.fontSize = 18f;
                tmp.enableWordWrapping = false;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.raycastTarget = false;
                if (reference != null)
                {
                    tmp.font = reference.font;
                    tmp.fontSharedMaterial = reference.fontSharedMaterial;
                }
                var labelRect = labelGo.GetComponent<RectTransform>();
                labelRect.anchorMin = new Vector2(0.5f, 0.5f);
                labelRect.anchorMax = new Vector2(0.5f, 0.5f);
                labelRect.pivot = new Vector2(0.5f, 0.5f);
                _timelineLabels[i] = tmp;
            }

            // 拖拽热区：透明 Image + EventTrigger（按下 / 拖动 → 吸附最近节点）
            var dragGo = new GameObject("DeathRewind_TimelineDrag", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            dragGo.transform.SetParent(_root, false);
            var dragImg = dragGo.GetComponent<Image>();
            dragImg.color = new Color(0f, 0f, 0f, 0f);
            dragImg.raycastTarget = true;
            _dragRect = dragGo.GetComponent<RectTransform>();
            _dragRect.anchorMin = new Vector2(0.5f, 0.5f);
            _dragRect.anchorMax = new Vector2(0.5f, 0.5f);
            _dragRect.pivot = new Vector2(0.5f, 0.5f);
            var trigger = dragGo.AddComponent<EventTrigger>();
            var downEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            if (downEntry.callback == null) downEntry.callback = new EventTrigger.TriggerEvent();
            downEntry.callback.AddListener(OnTimelinePointer);
            var dragEntry = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
            if (dragEntry.callback == null) dragEntry.callback = new EventTrigger.TriggerEvent();
            dragEntry.callback.AddListener(OnTimelinePointer);
            trigger.triggers = new List<EventTrigger.Entry> { downEntry, dragEntry };

            UpdateNodeHighlight();
        }

        private void OnTimelinePointer(BaseEventData data)
        {
            var ped = data as PointerEventData;
            if (ped == null) return;

            var cam = ped.pressEventCamera;
            if (cam == null)
            {
                var canvas = _root.GetComponentInParent<Canvas>();
                if (canvas != null) cam = canvas.worldCamera;
            }
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, ped.position, cam, out local)) return;

            int index = TimelineLayout.NearestNode(local.x, _timelineCenters);
            SelectNode(index);
        }

        /// <summary>切换选中的时间轴节点：换缩略图 + 更新高亮 + 通知外部换回溯目标。</summary>
        private void SelectNode(int index)
        {
            if (index < 0 || index >= _candidates.Count) return;
            if (index == _selectedIndex) return;
            _selectedIndex = index;
            ApplyPreviewTexture(_candidates[index].PreviewPng);
            UpdateNodeHighlight();
            NodeSelected?.Invoke(index);
        }

        /// <summary>选中节点高亮（不透明度区分）。</summary>
        private void UpdateNodeHighlight()
        {
            if (_timelineNodes == null) return;
            for (int i = 0; i < _timelineNodes.Length; i++)
            {
                bool selected = i == _selectedIndex;
                if (_timelineNodes[i] != null)
                    _timelineNodes[i].color = selected ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                if (_timelineLabels != null && i < _timelineLabels.Length && _timelineLabels[i] != null)
                    _timelineLabels[i].color = selected ? Color.white : new Color(1f, 1f, 1f, 0.55f);
            }
        }

        private byte[] SelectedPreviewPng()
            => _selectedIndex >= 0 && _selectedIndex < _candidates.Count ? _candidates[_selectedIndex].PreviewPng : null;

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
                    ModLog.Log($"[DeathRewind] 缩略图空间不足 {layout.PreviewSpaceShortfall:0}px，缩到 {layout.Preview.Height:0}px");
                ApplyToChild(_previewRect, layout.Preview);
            }

            LayoutTimeline(layout.Preview);
        }

        /// <summary>时间轴排版：贴在缩略图下方，节点等距排布。</summary>
        private void LayoutTimeline(RectF preview)
        {
            if (_timelineRect == null) return;
            var tl = TimelineLayout.Compute(preview, _candidates.Count);
            if (!tl.HasLayout) return;

            _timelineCenters = tl.NodeCenters;
            ApplyToChild(_timelineRect, tl.Strip);
            for (int i = 0; i < _timelineNodes.Length && i < tl.Nodes.Length; i++)
            {
                ApplyToChild(_timelineNodes[i].rectTransform, tl.Nodes[i]);
                ApplyToChild(_timelineLabels[i].rectTransform, tl.Labels[i]);
            }

            // 拖拽热区：覆盖横条与标签行（左右各留半个节点半径）
            float pad = TimelineLayout.NodeRadius;
            ApplyToChild(_dragRect, new RectF(
                tl.Strip.XMin - pad, tl.Labels[tl.Labels.Length - 1].YMin,
                tl.Strip.XMax + pad, tl.Strip.YMax + pad));
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

        /// <summary>找参考字体：优先结算界面上"UGLY"标题那种手写大字，否则取字号最大的文字。</summary>
        private static TextMeshProUGUI FindReferenceFont(Button continueBtn)
        {
            if (continueBtn == null) return null;
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

        /// <summary>生成白色圆点贴图（时间轴节点）。</summary>
        private static Texture2D BuildCircleTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            float r = size * 0.5f;
            float edge = r - 1.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r;
                    float dy = y + 0.5f - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    pixels[y * size + x] = d <= edge
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(255, 255, 255, 0);
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
            NodeSelected = null;
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
            if (_timelineRect != null)
            {
                UnityEngine.Object.Destroy(_timelineRect.gameObject);
                _timelineRect = null;
            }
            if (_dragRect != null)
            {
                UnityEngine.Object.Destroy(_dragRect.gameObject);
                _dragRect = null;
            }
            if (_timelineNodes != null)
            {
                foreach (var n in _timelineNodes)
                {
                    if (n != null) UnityEngine.Object.Destroy(n.gameObject);
                }
                _timelineNodes = null;
            }
            if (_timelineLabels != null)
            {
                foreach (var l in _timelineLabels)
                {
                    if (l != null) UnityEngine.Object.Destroy(l.gameObject);
                }
                _timelineLabels = null;
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
            if (_nodeSprite != null)
            {
                UnityEngine.Object.Destroy(_nodeSprite);
                _nodeSprite = null;
            }
            if (_nodeTex != null)
            {
                UnityEngine.Object.Destroy(_nodeTex);
                _nodeTex = null;
            }
            _timelineStrip = null;
            _timelineCenters = null;
            _candidates = new List<RewindCandidate>();
            _selectedIndex = -1;
            _rewindButton = null;
            _nativeRect = null;
            _nativeParent = null;
            _root = null;
        }
    }
}
