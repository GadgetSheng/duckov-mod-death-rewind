using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DeathRewind.Compat;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 锚点缩略图抓取：用**专用抓图相机**把画面渲染到小尺寸 RT（192×108），再读像素编码为 PNG。
    ///
    /// <para>
    /// 【为什么不能直接拿主相机渲】
    /// 早期实现是 <c>cam.targetTexture = rt; cam.Render();</c>——在**游戏主相机**上改写输出目标
    /// 并在帧中途手动多渲一次。游戏跑在 URP 上，主相机由渲染管线管理；帧中途改它的 targetTexture
    /// 会让管线这帧的渲染目标错乱，Metal/macOS 上表现为**屏幕闪一帧**（黑屏/花屏），
    /// 而且每 5 秒（锚点网格间隔）发生一次。
    /// </para>
    ///
    /// <para>
    /// 现在改成：不参与正常渲染的专用抓图相机（<c>enabled = false</c>，永不进管线的相机列表），
    /// 用 <c>CopyFrom</c> 抄主相机的渲染设置并单独摆位，只让它渲到自己的 RT。
    /// **游戏主相机的任何属性都不再被修改** → 帧的呈现状态不被污染 → 不闪。
    /// 渲染开销不变（每 5 秒多渲一次场景），主线程只剩 192×108 的 ReadPixels，PNG 编码在后台线程。
    /// </para>
    ///
    /// <para>
    /// 【画面一致性】
    /// URP 把 renderer 索引、后处理开关等放在独立的 <c>UniversalAdditionalCameraData</c> 组件上，
    /// <c>CopyFrom</c> 不碰它 —— 抄过来（见 <see cref="UrpCameraCompat"/>）才和玩家实际看到的一致。
    /// 另外 RT 是 16:9 而游戏窗口未必是：抓图相机按**主相机的水平 FOV** 反算垂直 FOV，
    /// 保证玩家当时能看到的横向范围一点不少（纵向多露出一点，好过横向被裁掉）。
    /// </para>
    /// </summary>
    public static class ThumbnailCapture
    {
        public const int DefaultWidth = 192;
        public const int DefaultHeight = 108;

        /// <summary>专用抓图相机（长期复用，避免每次抓图新建对象）。</summary>
        private static Camera _captureCamera;

        /// <summary>后台线程编码完 PNG 后，把纹理交回主线程销毁（Object.Destroy 只能在主线程调用）。</summary>
        private static readonly ConcurrentQueue<Texture2D> _pendingDestroy = new ConcurrentQueue<Texture2D>();

        /// <summary>本次会话已抓图次数（供日志核对"抓图链路活着"）。</summary>
        private static int _captureCount;

        /// <summary>
        /// FOV 换算时保留的主相机宽高比上限（21:9）。超宽屏再宽就只能裁一点横向，
        /// 否则垂直视野被撑到畸变（人物变小、地上多出一大片）。
        /// </summary>
        private const float MaxKeptAspect = 21f / 9f;

        /// <summary>同步抓取缩略图；任何异常都返回 null（不影响锚点构建）。</summary>
        public static byte[] Capture(int width = DefaultWidth, int height = DefaultHeight)
        {
            return CaptureAsync(width, height).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 异步抓取：主线程完成相机渲染 + ReadPixels，PNG 编码在后台线程进行。
        /// 调用方必须在主线程调用（相机渲染与 ReadPixels 不可跨线程）。
        /// </summary>
        public static Task<byte[]> CaptureAsync(int width = DefaultWidth, int height = DefaultHeight)
        {
            try
            {
                var main = LevelAccess.MainCamera;
                if (main == null) return Task.FromResult<byte[]>(null);

                var cam = GetCaptureCamera(main, width, height);
                LastStatus = $"count={_captureCount + 1} size={width}x{height} aspect={cam.aspect:0.00} " +
                             $"fov={cam.fieldOfView:0.0} mainAspect={main.aspect:0.00} urp={UrpCameraCompat.LastSummary}";
                var rt = RenderTexture.GetTemporary(width, height, 16, RenderTextureFormat.ARGB32);
                var prevActive = RenderTexture.active;
                Texture2D tex = null;
                try
                {
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;

                    tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tex.Apply(false);
                }
                finally
                {
                    cam.targetTexture = null;
                    RenderTexture.active = prevActive;
                    RenderTexture.ReleaseTemporary(rt);
                }

                // PNG 编码（纯计算，不碰 Unity 渲染）放到后台线程，进一步压缩主线程尖峰
                var captured = tex;
                _captureCount++;
                if (!_statusLogged)
                {
                    _statusLogged = true;
                    ModLog.LogImportant($"[DeathRewind] 缩略图抓取正常：{LastStatus}");
                }
                return Task.Run(() =>
                {
                    try { return ImageConversion.EncodeToPNG(captured); }
                    finally { _pendingDestroy.Enqueue(captured); }   // 纹理回主线程销毁
                });
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 缩略图抓取失败: {e.Message}");
                return Task.FromResult<byte[]>(null);
            }
        }

        /// <summary>
        /// 主线程每帧调用：回收后台线程编码完 PNG 的纹理。
        /// <see cref="UnityEngine.Object.Destroy"/> 只能在主线程调用，后台线程直接调会报错。
        /// </summary>
        public static void Tick()
        {
            while (_pendingDestroy.TryDequeue(out var tex))
            {
                if (tex == null) continue;
                try { UnityEngine.Object.Destroy(tex); }
                catch (Exception e) { ModLog.LogWarning($"[DeathRewind] 缩略图纹理回收失败: {e.Message}"); }
            }
        }

        /// <summary>本次会话累计抓图次数（供日志/调试核对）。</summary>
        public static int CaptureCount => _captureCount;

        /// <summary>本关是否已打过状态日志（换关时复位）。</summary>
        private static bool _statusLogged;

        /// <summary>换关时复位状态日志（每关打一条，便于实机核对抓图链路是否正常）。</summary>
        public static void ResetStatusLog() { _statusLogged = false; }

        /// <summary>
        /// 最近一次抓图的配置摘要（供实机核对：抓图相机参数 + URP 附加数据是否抄到）。
        /// 例：<c>count=12 size=192x108 aspect=1.78 fov=60.0 urp=renderer=0 post=True camType=0</c>
        /// </summary>
        public static string LastStatus { get; private set; } = "none";

        /// <summary>
        /// 取（并按主相机刷新）专用抓图相机。
        /// <c>CopyFrom</c> 会复制渲染设置（剔除遮罩、天空盒、FOV、near/far、清屏色…），
        /// 也会连带 transform 与 layer；但 <b>targetTexture / enabled 不受控</b> → 这两项自己设。
        /// </summary>
        private static Camera GetCaptureCamera(Camera main, int width, int height)
        {
            if (_captureCamera == null)
            {
                var go = new GameObject("DeathRewind_ThumbnailCamera");
                go.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);
                _captureCamera = go.AddComponent<Camera>();
            }

            _captureCamera.CopyFrom(main);
            // URP 的每相机附加数据（renderer 索引 / 后处理 / AA…）不在 Camera 组件里，要单独抄
            UrpCameraCompat.TryCopy(main, _captureCamera);
            // 摆位 + 缩放（CopyFrom 已抄 transform，这里显式再摆一次以免父级缩放差异）
            _captureCamera.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
            _captureCamera.transform.localScale = main.transform.lossyScale;
            // 关键：抓图相机不进管线的相机列表（否则它会跟着主相机一起渲到屏幕上）
            _captureCamera.enabled = false;
            _captureCamera.targetTexture = null;
            // RT 是固定的宽高比：强制 aspect，并按主相机的水平 FOV 反算垂直 FOV，避免横向被裁
            float targetAspect = (float)width / height;
            _captureCamera.aspect = targetAspect;
            if (!main.orthographic)
                _captureCamera.fieldOfView = VerticalFovForHorizontalMatch(main, targetAspect);
            return _captureCamera;
        }

        /// <summary>
        /// 给定主相机的水平视野，返回"在 <paramref name="targetAspect"/> 下保持同样水平视野所需的垂直 FOV。
        /// Unity 的 fieldOfView 是垂直 FOV：直接把主相机的垂直 FOV 抄过来，横向视野会比玩家看到的窄
        /// （超宽屏下更明显）—— 缩略图会把玩家当时能看到的左右两侧裁掉。
        /// <para>
        /// 超宽屏（宽高比 &gt; 21:9）按 21:9 封顶：再宽就只能裁一点横向，
        /// 否则垂直视野会被撑到畸变（人物变小、地上多出一大片）。
        /// </para>
        /// </summary>
        private static float VerticalFovForHorizontalMatch(Camera main, float targetAspect)
        {
            float mainAspect = main.aspect;
            if (mainAspect <= 0.01f || targetAspect <= 0.01f) return main.fieldOfView;
            try
            {
                float effectiveAspect = Math.Min(mainAspect, MaxKeptAspect);
                double tanV = Math.Tan(main.fieldOfView * 0.5 * Math.PI / 180.0);
                double tanH = effectiveAspect * tanV;        // 水平半视野正切
                double tanV2 = tanH / targetAspect;          // 换算到目标宽高比下的垂直半视野
                double fov = 2.0 * Math.Atan(tanV2) * 180.0 / Math.PI;
                if (double.IsNaN(fov) || double.IsInfinity(fov)) return main.fieldOfView;
                return (float)Math.Min(fov, 170.0);
            }
            catch
            {
                return main.fieldOfView;
            }
        }

        /// <summary>释放抓图相机（Mod 停用时调用）。</summary>
        public static void Dispose()
        {
            Tick();
            if (_captureCamera == null) return;
            UnityEngine.Object.Destroy(_captureCamera.gameObject);
            _captureCamera = null;
        }
    }
}
