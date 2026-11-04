using System;
using System.IO;
using DeathRewind.Core;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 「1UP」提示音播放。
    ///
    /// 【为什么主路径是游戏自带的 FMOD 事件】
    /// Duckov 的音频**全部**是 FMOD 事件（<c>Duckov.AudioManager.Post</c> → <c>FMOD.Studio.EventInstance</c>），
    /// Unity 自己的 <c>AudioSource</c> 在游戏里**听不到**：实测用 AudioSource 播自定义 WAV 完全无声，
    /// 玩家日志里也没有任何报错（说明 clip 有数据，是 Unity 这条输出通路被游戏静音了）。
    /// 所以主路径改为 post 游戏自带的 UI 事件 <c>UI/level_up</c>——语义正好是"升级/拿到 1UP"，
    /// 而且跟随游戏自己的音量与静音设置。
    /// Unity AudioSource + 内嵌 WAV（<c>Audio/oneup.wav</c>）保留为兜底（FMOD 未就绪时）。
    ///
    /// 音频来源：仓库里的 <c>Audio/1_Up_sound.mp3</c>，构建期转成 PCM WAV（<c>Audio/oneup.wav</c>）
    /// 并作为**内嵌资源**打进程序集——运行时零文件 IO、零异步加载。
    /// </summary>
    public static class OneUpSound
    {
        /// <summary>内嵌 WAV 资源的文件名后缀（内嵌资源名带命名空间前缀，用后缀匹配避免写死完整名）。</summary>
        private const string ResourceSuffix = "oneup.wav";

        /// <summary>游戏自带的 UI 事件（FMOD）：升级 / 拿到 1UP 的提示音。</summary>
        private const string GameLevelUpEvent = "UI/level_up";

        private static byte[] _wavBytes;        // 内嵌 WAV 原始字节
        private static AudioClip _clip;         // Unity 兜底路径
        private static AudioSource _source;
        private static bool _warned;
        private static bool _readyLogged;

        /// <summary>播放一次提示音。volume 为 0~1 线性音量；&lt;= 0 直接跳过。</summary>
        public static void Play(float volume)
        {
            if (volume <= 0f) return;
            try
            {
                if (PostGameEvent()) return;   // 首选：游戏自己的 FMOD 音频（一定能听见）
                PlayUnityFallback(volume);      // 兜底：Unity AudioSource + 内嵌 WAV
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
            _wavBytes = null;
        }

        /// <summary>
        /// post 游戏自带的 FMOD UI 事件（<c>AudioManager.Post</c> 是 public static，
        /// 内部会自己补 <c>event:/</c> 前缀并走 FMOD Studio 播放）。
        /// </summary>
        private static bool PostGameEvent()
        {
            try
            {
                Duckov.AudioManager.Post(GameLevelUpEvent);
                LogReady($"游戏 FMOD 事件 {GameLevelUpEvent}");
                return true;
            }
            catch (Exception e)
            {
                WarnOnce($"游戏音频事件播放失败: {e.Message}");
                return false;
            }
        }

        /// <summary>兜底：Unity AudioSource 播内嵌 WAV（FMOD 不可用时）。</summary>
        private static void PlayUnityFallback(float volume)
        {
            var clip = GetUnityClip();
            if (clip == null) return;
            var source = GetUnitySource();
            if (source == null) return;
            source.PlayOneShot(clip, Mathf.Clamp01(volume));
            LogReady("Unity AudioSource");
        }

        private static AudioClip GetUnityClip()
        {
            if (_clip != null) return _clip;
            var bytes = LoadResourceBytes();
            if (bytes == null) return null;
            if (!WavPcm.TryDecode(bytes, out int channels, out int sampleRate, out float[] samples)) return null;
            int frames = samples.Length / channels;
            var clip = AudioClip.Create("DeathRewind_1Up", frames, channels, sampleRate, false);
            clip.SetData(samples, 0);
            _clip = clip;
            return _clip;
        }

        private static AudioSource GetUnitySource()
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

        private static byte[] LoadResourceBytes()
        {
            if (_wavBytes != null) return _wavBytes;
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
                        _wavBytes = buffer.ToArray();
                        return _wavBytes;
                    }
                }
            }
            return null;
        }

        /// <summary>提示音就绪/失败都用**决策级日志**输出（发布版也能看到，便于排查"没声音"）。</summary>
        private static void LogReady(string backend)
        {
            if (_readyLogged) return;
            _readyLogged = true;
            ModLog.LogImportant($"[DeathRewind] 1UP 提示音已就绪（{backend}）");
        }

        private static void WarnOnce(string reason)
        {
            if (_warned) return;
            _warned = true;
            ModLog.LogImportant($"[DeathRewind] 1UP 提示音不可用（{reason}），已跳过音效");
        }
    }
}
