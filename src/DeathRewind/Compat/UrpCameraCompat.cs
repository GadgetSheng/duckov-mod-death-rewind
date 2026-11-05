using System;
using System.Reflection;
using UnityEngine;

namespace DeathRewind.Compat
{
    /// <summary>
    /// 同步 URP 的每相机附加数据到抓图相机。
    ///
    /// <para>
    /// Duckov 跑在 URP 14.0.11 上。URP 把"这个相机用哪个 renderer、要不要后处理、是 Base 还是 Overlay"
    /// 放在**独立的组件** `UniversalAdditionalCameraData` 里，而不是 `Camera` 组件本身。
    /// `Camera.CopyFrom` 只复制 `Camera` 组件的属性（FOV / 剔除遮罩 / 天空盒 / clear flags…），
    /// **不碰这个组件** —— 于是抓图相机会退回 URP 默认值：renderer 0、无后处理、
    /// AA 关闭 → 缩略图和玩家实际看到的画面不是一回事。
    /// </para>
    ///
    /// <para>
    /// 这里用反射把主相机的关键字段抄过去（不编译期依赖 URP 程序集：游戏升级换了 URP 版本时
    /// 只会"抄不到"，不会程序集加载失败）。字段/类型缺失时静默降级 —— 只影响画面观感，
    /// 不影响功能。
    /// </para>
    /// </summary>
    public static class UrpCameraCompat
    {
        private const string TypeName = "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData";

        /// <summary>需要从主相机抄到抓图相机的字段（URP 14 的 UniversalAdditionalCameraData 实例字段）。</summary>
        private static readonly string[] CopiedFields =
        {
            "m_RendererIndex",            // 用哪个 renderer（阴影/后处理/中间纹理配置都可能不同）
            "m_RenderPostProcessing",    // 后处理开关
            "m_Antialiasing",
            "m_AntialiasingQuality",
            "m_RenderShadows",
            "m_RequiresDepthTexture",
            "m_RequiresColorTexture",
            "m_Dithering",
            "m_StopNaN",
            "m_ClearDepth",
            "m_VolumeLayerMask",
            "m_AllowHDROutput",
            "m_AllowXRRendering",
            "m_VolumeFrameworkUpdateModeOption",
            "m_RequiresDepthTextureOption",
            "m_RequiresOpaqueTextureOption",
        };

        private static Type _type;
        private static bool _typeSearched;

        /// <summary>最近一次同步的结果摘要（供日志核对）：renderer 索引、后处理开关、相机类型。</summary>
        public static string LastSummary { get; private set; } = "n/a";

        /// <summary>
        /// 把 <paramref name="source"/>（游戏主相机）的 URP 附加数据抄到 <paramref name="dest"/>（抓图相机）。
        /// 返回 true 表示已同步。
        /// </summary>
        public static bool TryCopy(Camera source, Camera dest)
        {
            if (source == null || dest == null) return false;
            var type = ResolveType();
            if (type == null) { LastSummary = "urp-type-missing"; return false; }

            try
            {
                var src = source.GetComponent(type);
                if (src == null) { LastSummary = "source-has-no-urp-data"; return false; }

                var dst = dest.GetComponent(type);
                if (dst == null) dst = dest.gameObject.AddComponent(type);

                int copied = 0;
                foreach (var name in CopiedFields)
                {
                    var f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f == null) continue;
                    object value;
                    try { value = f.GetValue(src); }
                    catch { continue; }
                    try { f.SetValue(dst, value); copied++; }
                    catch { /* 字段类型/只读 → 跳过 */ }
                }

                // 相机类型固定为 Base：抓图相机是独立渲染，不参与主相机的相机栈。
                // 抄成 Overlay 会让 URP 找不到 Base 父相机而直接跳过渲染。
                int cameraType = -1;
                var typeField = type.GetField("m_CameraType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (typeField != null)
                {
                    try { cameraType = Convert.ToInt32(typeField.GetValue(src)); }
                    catch { cameraType = -1; }
                    try { typeField.SetValue(dst, Enum.ToObject(typeField.FieldType, 0)); }  // 0 = CameraRenderType.Base
                    catch { /* 忽略 */ }
                }

                LastSummary = $"renderer={ReadInt(src, "m_RendererIndex")} post={ReadBool(src, "m_RenderPostProcessing")} " +
                              $"camType={(cameraType < 0 ? "?" : cameraType.ToString())} fields={copied}";
                return copied > 0;
            }
            catch (Exception e)
            {
                LastSummary = "error:" + e.Message;
                return false;
            }
        }

        private static Type ResolveType()
        {
            if (_typeSearched) return _type;
            _typeSearched = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType(TypeName, false);
                    if (t != null) { _type = t; break; }
                }
            }
            catch { /* 反射失败 → 保持 null（降级） */ }
            return _type;
        }

        private static int ReadInt(object comp, string field)
        {
            var t = comp?.GetType();
            var f = t?.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) return -1;
            try { return Convert.ToInt32(f.GetValue(comp)); } catch { return -1; }
        }

        private static bool ReadBool(object comp, string field)
        {
            var t = comp?.GetType();
            var f = t?.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) return false;
            try { return Convert.ToBoolean(f.GetValue(comp)); } catch { return false; }
        }
    }
}
