using System;

namespace DeathRewind.Core
{
    /// <summary>
    /// 16bit PCM WAV 解码（纯逻辑，不依赖 Unity）。
    ///
    /// 只支持 Mod 自己用 <c>afconvert -f WAVE -d LEI16</c> 转出来的那种 WAV：
    /// RIFF/WAVE、fmt 为 PCM、每采样 16bit、有 data chunk。
    /// chunk 按序扫描（转换产物在 fmt 与 data 之间可能夹着 FLER 之类的附属 chunk），
    /// 固定偏移解析会读错。
    /// </summary>
    public static class WavPcm
    {
        /// <summary>
        /// 尝试解码 WAV。成功时 samples 为**交错**的 [-1, 1] 浮点采样
        /// （长度 = 帧数 × 声道数），channels / sampleRate 同时给出。
        /// </summary>
        public static bool TryDecode(byte[] wav, out int channels, out int sampleRate, out float[] samples)
        {
            channels = 0;
            sampleRate = 0;
            samples = null;

            if (wav == null || wav.Length < 44) return false;
            if (!Matches(wav, 0, "RIFF") || !Matches(wav, 8, "WAVE")) return false;

            int bits = 0;
            byte[] data = null;

            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                int body = pos + 8;
                if (size < 0 || body + size > wav.Length) break;

                if (id == "fmt " && size >= 16)
                {
                    channels = BitConverter.ToInt16(wav, body + 2);
                    sampleRate = BitConverter.ToInt32(wav, body + 4);
                    bits = BitConverter.ToInt16(wav, body + 14);
                }
                else if (id == "data")
                {
                    data = new byte[size];
                    Array.Copy(wav, body, data, 0, size);
                }

                pos = body + size + (size % 2);   // chunk 按偶数字节对齐
            }

            if (channels <= 0 || sampleRate <= 0 || bits != 16 || data == null) return false;

            int frameCount = data.Length / (channels * 2);
            if (frameCount <= 0) return false;

            var decoded = new float[frameCount * channels];
            for (int i = 0; i < decoded.Length; i++)
            {
                decoded[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
            }

            samples = decoded;
            return true;
        }

        private static bool Matches(byte[] bytes, int offset, string ascii)
        {
            if (offset + ascii.Length > bytes.Length) return false;
            for (int i = 0; i < ascii.Length; i++)
            {
                if (bytes[offset + i] != (byte)ascii[i]) return false;
            }
            return true;
        }
    }
}
