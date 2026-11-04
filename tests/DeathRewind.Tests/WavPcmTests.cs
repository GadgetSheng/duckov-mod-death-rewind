using System;
using System.IO;
using System.Reflection;
using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    /// <summary>
    /// 「1UP」提示音解码测试：直接用 Mod 内嵌的那份 WAV 验证解码器，
    /// 避免"资源没打进去 / 格式解析错 → 静默没声音"这类只在游戏里才发现的问题。
    /// </summary>
    public class WavPcmTests
    {
        private static byte[] LoadOneUpWav()
        {
            var assembly = typeof(WavPcmTests).Assembly;
            using (var stream = assembly.GetManifestResourceStream("DeathRewind.Tests.oneup.wav"))
            {
                Assert.NotNull(stream);
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }

        [Fact]
        public void OneUpWav_DecodesToMonoPcm22k()
        {
            var wav = LoadOneUpWav();

            Assert.True(WavPcm.TryDecode(wav, out int channels, out int sampleRate, out float[] samples));

            Assert.Equal(1, channels);
            Assert.Equal(22050, sampleRate);
            Assert.NotNull(samples);
            Assert.True(samples.Length > 0, "解码后没有采样");
        }

        [Fact]
        public void OneUpWav_DurationMatchesSourceClip()
        {
            var wav = LoadOneUpWav();

            Assert.True(WavPcm.TryDecode(wav, out int channels, out int sampleRate, out float[] samples));

            double seconds = samples.Length / (double)channels / sampleRate;
            // 源文件 1_Up_sound.mp3 时长 ≈ 0.94s，留 50ms 容差
            Assert.InRange(seconds, 0.85, 1.0);
        }

        [Fact]
        public void OneUpWav_ContainsAudibleSignal()
        {
            var wav = LoadOneUpWav();

            Assert.True(WavPcm.TryDecode(wav, out _, out _, out float[] samples));

            float peak = 0f;
            foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
            Assert.True(peak > 0.1f, $"音频几乎是静音（峰值 {peak}）");
        }

        [Fact]
        public void TryDecode_RejectsGarbage()
        {
            Assert.False(WavPcm.TryDecode(null, out _, out _, out _));
            Assert.False(WavPcm.TryDecode(new byte[10], out _, out _, out _));

            var notRiff = new byte[64];
            Assert.False(WavPcm.TryDecode(notRiff, out _, out _, out _));
        }

        [Fact]
        public void TryDecode_RejectsTruncatedDataChunk()
        {
            var wav = LoadOneUpWav();

            // 砍掉一半 data：fmt 还在但 data 长度超出文件 → 必须判定为解码失败而不是读越界
            var truncated = new byte[wav.Length / 2];
            Array.Copy(wav, truncated, truncated.Length);

            bool ok = WavPcm.TryDecode(truncated, out _, out _, out _);

            Assert.False(ok);
        }
    }
}
