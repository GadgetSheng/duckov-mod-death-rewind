using System;
using System.IO;
using DeathRewind.Core;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 「1UP」提示音播放。
    ///
    /// 音源：仓库里的 <c>Audio/1_Up_sound.mp3</c>，构建期转成 PCM WAV（<c>Audio/oneup.wav</c>）
    /// 并作为**内嵌资源**打进程序集——运行时零文件 IO、零异步加载，
    /// 也不会因为 Mod 目录路径 / 权限问题读不到音频。
    ///
    /// 播放走 Unity 自带的 <see cref="AudioSource"/>（2D、无空间衰减、无混响），
    /// 不走游戏自己的 FMOD 管线：FMOD 只能播 bank 里的事件，塞不进自定义音频文件。
    ///
    /// 任何失败都静默降级——音效只是锦上添花，绝不能影响回溯功能。
    /// </summary>
    public static class OneUpSound
    {
        /// <summary>内嵌 WAV 资源的文件名后缀（内嵌资源名带命名空间前缀，用后缀匹配避免写死完整名）。</summary>
        private const string ResourceSuffix = "oneup.wav";

        private static AudioClip _clip;
        private static AudioSource _source;
        private static bool _warned;

        /// <summary>播放一次提示音。volume 为 0~1 线性音量；&lt;= 0 直接跳过。</summary>
        public static void Play(float volume)
        {
            if (volume <= 0f) return;
            try
            {
                var clip = GetClip();
                if (clip == null) return;
                var source = GetSource();
                if (source == null) return;
                source.PlayOneShot(clip, Mathf.Clamp01(volume));
            }
            catch (Exception e)
            {
                WarnOnce($"播放失败: {e.Message}");
            }
        }

        /// <summary>释放音频资源（Mod 停用时调用）。</summary>
        public static void Dispose()
        {
            if (_source != null)
            {
                UnityEngine.Object.Destroy(_source);
                _source = null;
            }
            if (_clip != null)
            {
                UnityEngine.Object.Destroy(_clip);
                _clip = null;
            }
        }

        private static AudioSource GetSource()
        {
            if (_source != null) return _source;
            var go = new GameObject("DeathRewind_OneUpSound");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideInHierarchy;
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;     // 2D：与玩家位置无关，全场等音量
            _source = source;
            return source;
        }

        private static AudioClip GetClip()
        {
            if (_clip != null) return _clip;
            var bytes = LoadResourceBytes();
            if (bytes == null)
            {
                WarnOnce("内嵌音频资源缺失");
                return null;
            }
            _clip = BuildClip(bytes);
            if (_clip == null) WarnOnce("音频格式无法解析（需要 16bit PCM WAV）");
            return _clip;
        }

        private static byte[] LoadResourceBytes()
        {
            var assembly = typeof(OneUpSound).Assembly;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                using (var stream = assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) continue;
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        return buffer.ToArray();
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 解析 RIFF/WAVE 并构造 <see cref="AudioClip"/>（解析逻辑见 <see cref="DeathRewind.Core.WavPcm"/>）。
        /// </summary>
        private static AudioClip BuildClip(byte[] wav)
        {
            if (!WavPcm.TryDecode(wav, out int channels, out int sampleRate, out float[] samples)) return null;

            int frames = samples.Length / channels;
            var clip = AudioClip.Create("DeathRewind_1Up", frames, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static void WarnOnce(string reason)
        {
            if (_warned) return;
            _warned = true;
            ModLog.LogWarning($"[DeathRewind] 1UP 提示音不可用（{reason}），已跳过音效");
        }
    }
}
