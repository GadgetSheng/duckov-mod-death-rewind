using System;
using System.Threading.Tasks;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 锚点缩略图抓取：用一个**专用抓图相机**把画面渲染到小尺寸 RT（192×108），再读像素编码为 PNG。
    ///
    /// 【为什么不能直接拿主相机渲】
    /// 旧实现是 <c>cam.targetTexture = rt; cam.Render();</c>——在**游戏主相机**上改 targetTexture
    /// 并在帧中途手动 Render，会让主相机在同一帧里多渲一次。Metal/macOS 上这会表现为
    /// **屏幕闪一帧**（黑屏/花屏），而且每 5 秒（锚点网格间隔）发生一次。
    ///
    /// 现在改成：用一个不参与正常渲染的专用抓图相机（<c>CopyFrom</c> 主相机的渲染设置），
    /// 只让它渲到 RT，**完全不碰主相机** → 不闪屏。
    /// 渲染开销与旧实现相同（都是每 5 秒多渲一次场景），主线程只剩 192×108 的 ReadPixels，
    /// PNG 编码仍在后台线程。
    /// </summary>
    public static class ThumbnailCapture
    {
        public const int DefaultWidth = 192;
        public const int DefaultHeight = 108;

        /// <summary>专用抓图相机（长期复用，避免每次抓图新建对象）。</summary>
        private static Camera _captureCamera;

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

                var cam = GetCaptureCamera(main);
                var rt = RenderTexture.GetTemporary(width, height, 16, RenderTextureFormat.ARGB32);
                var prevActive = RenderTexture.active;
                Texture2D tex = null;
                try
                {
                    cam.targetTexture = rt;
                    cam.aspect = (float)width / height;
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
                return Task.Run(() =>
                {
                    try { return ImageConversion.EncodeToPNG(captured); }
                    finally { UnityEngine.Object.Destroy(captured); }
                });
            }
            catch (Exception e)
            {
                ModLog.LogWarning($"[DeathRewind] 缩略图抓取失败: {e.Message}");
                return Task.FromResult<byte[]>(null);
            }
        }

        /// <summary>
        /// 取（并按主相机刷新）专用抓图相机。
        /// <c>CopyFrom</c> 会复制渲染设置（剔除遮罩、天空盒、FOV、near/far、清屏色…），
        /// 但**不复制** transform / targetTexture / enabled → 这几项自己设。
        /// </summary>
        private static Camera GetCaptureCamera(Camera main)
        {
            if (_captureCamera == null)
            {
                var go = new GameObject("DeathRewind_ThumbnailCamera");
                go.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);
                _captureCamera = go.AddComponent<Camera>();
            }

            _captureCamera.CopyFrom(main);
            // CopyFrom 不带 transform，也不保证 enabled → 自己摆位，并确保它不参与正常渲染
            _captureCamera.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
            _captureCamera.transform.localScale = main.transform.lossyScale;
            _captureCamera.enabled = false;
            _captureCamera.targetTexture = null;
            return _captureCamera;
        }

        /// <summary>释放抓图相机（Mod 停用时调用）。</summary>
        public static void Dispose()
        {
            if (_captureCamera == null) return;
            UnityEngine.Object.Destroy(_captureCamera.gameObject);
            _captureCamera = null;
        }
    }
}
