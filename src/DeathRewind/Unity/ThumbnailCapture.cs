using System;
using System.Threading.Tasks;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 锚点缩略图抓取：把主相机直接渲染到小尺寸 RT（192×108），再读像素编码为 PNG。
    /// 与旧的 ScreenCapture.CaptureScreenshotAsTexture 相比，省掉了"全屏纹理分配 + 全屏回读 + 缩放"的开销，
    /// 主线程只剩 192×108 的 ReadPixels；PNG 编码可放到后台线程（CaptureAsync）。
    /// </summary>
    public static class ThumbnailCapture
    {
        public const int DefaultWidth = 192;
        public const int DefaultHeight = 108;

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
                var cam = LevelAccess.MainCamera;
                if (cam == null) return Task.FromResult<byte[]>(null);

                var rt = RenderTexture.GetTemporary(width, height, 16, RenderTextureFormat.ARGB32);
                var prevActive = RenderTexture.active;
                var prevTarget = cam.targetTexture;
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
                    cam.targetTexture = prevTarget;
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
    }
}
