using System;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 锚点缩略图抓取：抓取当前屏幕画面 → 缩放到小尺寸 → 编码为 PNG 字节。
    /// 只在构建锚点时调用一次（主线程），回退确认界面按需解码显示。
    /// </summary>
    public static class ThumbnailCapture
    {
        public const int DefaultWidth = 192;
        public const int DefaultHeight = 108;

        /// <summary>抓取缩略图；任何异常都返回 null（不影响锚点构建）。</summary>
        public static byte[] Capture(int width = DefaultWidth, int height = DefaultHeight)
        {
            try
            {
                if (LevelAccess.MainCamera == null) return null;
                var full = ScreenCapture.CaptureScreenshotAsTexture();
                if (full == null) return null;
                try
                {
                    var small = new Texture2D(width, height, TextureFormat.RGB24, false);
                    try
                    {
                        // 使用 ReadPixels 替代 Graphics.Blit（后者要求 RenderTexture）
                        var prevActive = RenderTexture.active;
                        var rt = RenderTexture.GetTemporary(full.width, full.height);
                        Graphics.Blit(full, rt);
                        RenderTexture.active = rt;
                        small.ReadPixels(new Rect(0, 0, full.width, full.height), 0, 0);
                        small.Apply(false);
                        RenderTexture.active = prevActive;
                        RenderTexture.ReleaseTemporary(rt);
                        return small.EncodeToPNG();
                    }
                    finally
                    {
                        UnityEngine.Object.Destroy(small);
                    }
                }
                finally
                {
                    UnityEngine.Object.Destroy(full);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeathRewind] 缩略图抓取失败: {e.Message}");
                return null;
            }
        }
    }
}
