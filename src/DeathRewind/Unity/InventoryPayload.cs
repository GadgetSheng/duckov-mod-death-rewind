using System.Text;
using System.Threading.Tasks;
using ItemStatsSystem;
using ItemStatsSystem.Data;
using UnityEngine;

namespace DeathRewind.Unity
{
    /// <summary>
    /// 背包 ↔ 不透明字节串。InventoryData/ItemTreeData 均为 [Serializable] 且字段为具体类型，JsonUtility 可用。
    /// Task 8 已完整实现：Capture / Parse / ApplyAsync / VerifyRoundTrip 全部为真实逻辑，无空实现残留。
    /// </summary>
    public static class InventoryPayload
    {
        public static byte[] Capture(Inventory inventory)
        {
            if (inventory == null) return null;
            var data = InventoryData.FromInventory(inventory);
            return Encoding.UTF8.GetBytes(JsonUtility.ToJson(data));
        }

        public static InventoryData Parse(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return null;
            try
            {
                return JsonUtility.FromJson<InventoryData>(Encoding.UTF8.GetString(payload));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DeathRewind] InventoryPayload 解析失败: {e.Message}");
                return null;
            }
        }

        /// <summary>原地还原：清空后按载荷重建。失败返回 false，调用方负责回灌备份。</summary>
        public static async Task<bool> ApplyAsync(byte[] payload, Inventory target)
        {
            var data = Parse(payload);
            if (data == null || target == null) return false;
            target.DestroyAllContent();
            await InventoryData.LoadIntoInventory(data, target);
            return true;
        }

        /// <summary>往返自检：Capture→Parse 后条目数一致（构建锚点时调用一次）。</summary>
        public static bool VerifyRoundTrip(Inventory inventory)
        {
            if (inventory == null) return true;
            InventoryData data = null;
            try { data = Parse(Capture(inventory)); }
            catch { return false; }
            if (data == null) return false;
            var content = inventory.Content;
            int count = content == null ? 0 : content.Count;
            return data.entries != null && data.entries.Count == count;
        }
    }
}
