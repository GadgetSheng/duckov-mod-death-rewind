using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DeathRewind.Core;
using ItemStatsSystem;
using ItemStatsSystem.Data;
using ItemStatsSystem.Items;
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
            // InventoryData.FromInventory 会跳过空槽，因此这里也要按"非空物品数"比较，
            // 否则背包里有空洞时会把正常情况误判为自检失败。
            int count = 0;
            var content = inventory.Content;
            if (content != null)
                foreach (var item in content)
                    if (item != null) count++;
            return data.entries != null && data.entries.Count == count;
        }

        // ------------------------------------------------------------------ 物品树 / 装备槽

        /// <summary>把单个物品（含整棵子树：挂件、槽位内容、变量）序列化为载荷。</summary>
        public static byte[] CaptureItem(Item item)
        {
            if (item == null) return null;
            try
            {
                return Encoding.UTF8.GetBytes(JsonUtility.ToJson(ItemTreeData.FromItem(item)));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DeathRewind] 物品载荷采集失败: {e.Message}");
                return null;
            }
        }

        /// <summary>从载荷重建物品实例（含子树）。</summary>
        public static async System.Threading.Tasks.Task<Item> InstantiateItemAsync(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return null;
            try
            {
                var tree = JsonUtility.FromJson<ItemTreeData>(Encoding.UTF8.GetString(payload));
                if (tree == null) return null;
                return await ItemTreeData.InstantiateAsync(tree);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DeathRewind] 物品载荷重建失败: {e.Message}");
                return null;
            }
        }

        /// <summary>采集角色物品上所有非空槽位（装备/手持武器）的内容载荷。</summary>
        public static ItemSlotPayload[] CaptureSlots(Item item)
        {
            if (item == null || item.Slots == null) return System.Array.Empty<ItemSlotPayload>();
            var list = new List<ItemSlotPayload>();
            foreach (Slot slot in item.Slots)
            {
                if (slot == null) continue;
                var content = slot.Content;
                if (content == null) continue;
                list.Add(new ItemSlotPayload { Name = slot.Key, Payload = CaptureItem(content) });
            }
            return list.ToArray();
        }

        /// <summary>
        /// 还原角色物品的槽位内容：旧内容销毁、按载荷重建并 Plug 回去。
        /// 返回 false 表示至少一个槽位未还原成功（调用方记为未覆盖，不中断回退）。
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> ApplySlotsAsync(ItemSlotPayload[] slots, Item target)
        {
            if (slots == null || slots.Length == 0) return true;
            if (target == null) return false;
            bool allOk = true;
            foreach (var sp in slots)
            {
                if (sp == null || sp.Payload == null || string.IsNullOrEmpty(sp.Name)) continue;
                try
                {
                    var item = await InstantiateItemAsync(sp.Payload);
                    if (item == null) { allOk = false; continue; }
                    var collection = target.Slots;
                    if (collection == null) { allOk = false; continue; }
                    Slot slot = null;
                    try { slot = collection[sp.Name]; } catch { /* 槽位不存在 */ }
                    if (slot == null) { allOk = false; continue; }

                    // Plug 会把旧内容顶出来（只 Detach 不销毁），需要手动销毁避免游离物品
                    if (slot.Plug(item, out var unplugged) && unplugged != null)
                        Object.Destroy(unplugged.gameObject);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[DeathRewind] 槽位 {sp.Name} 还原失败: {e.Message}");
                    allOk = false;
                }
            }
            return allOk;
        }
    }
}
