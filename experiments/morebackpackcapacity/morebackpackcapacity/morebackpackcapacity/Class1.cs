using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

[BepInPlugin("com.yourname.morebackpackcapacity", "More Backpack Capacity", "1.0.0")]
public class MoreBackpackCapacityPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    private Harmony _harmony;

    // 你要增加的背包格子数（在原有基础上 +N）
    private ConfigEntry<int> ExtraSlots;

    // 可选：只在“默认背包容量=某值”时才扩（避免误伤其他容器）
    private ConfigEntry<int> OnlyWhenDefaultStorageEquals;

    // 安全上限，避免扩太大把 UI/逻辑弄爆
    private ConfigEntry<int> MaxStorage;

    private static MoreBackpackCapacityPlugin _inst;

    // 记录已经处理过的对象，避免重复加格子越加越大
    private readonly HashSet<int> _appliedInstances = new HashSet<int>();

    private void Awake()
    {
        _inst = this;
        Log = Logger;

        ExtraSlots = Config.Bind("General", "ExtraSlots", 20, "Add N extra slots to backpack storage.");
        OnlyWhenDefaultStorageEquals = Config.Bind("General", "OnlyWhenDefaultStorageEquals", 27,
            "Only expand InventoryInstance when its constructor defaultStorage equals this value. Set to -1 to disable this check.");
        MaxStorage = Config.Bind("General", "MaxStorage", 200, "Hard cap for target storage slots.");

        _harmony = new Harmony("com.yourname.morebackpackcapacity");
        PatchInventoryInstanceCtor();
        PatchGridInventoryIfExists();

        // 兜底：如果某些对象在插件加载前就创建了，再扫一遍
        StartCoroutine(PeriodicScan());

        Log.LogInfo("[MoreBackpackCapacity] Loaded.");
    }

    private void OnDestroy()
    {
        try { _harmony.UnpatchSelf(); } catch { }
    }

    // -------------------------
    // Patch 1) InventoryInstance(int defaultStorage) 构造后扩 slot list
    // -------------------------
    private void PatchInventoryInstanceCtor()
    {
        try
        {
            var invInstType = AccessTools.TypeByName("InventoryInstance");
            if (invInstType == null)
            {
                Log.LogWarning("[MoreBackpackCapacity] Type InventoryInstance not found.");
                return;
            }

            // 找最常见的 ctor: .ctor(int defaultStorage)
            var ctor = invInstType.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new Type[] { typeof(int) },
                null
            );

            if (ctor == null)
            {
                Log.LogWarning("[MoreBackpackCapacity] InventoryInstance(int) ctor not found.");
                return;
            }

            var postfix = new HarmonyMethod(typeof(MoreBackpackCapacityPlugin).GetMethod(
                "InventoryInstanceCtor_Postfix",
                BindingFlags.Static | BindingFlags.NonPublic));

            _harmony.Patch(ctor, null, postfix);

            Log.LogInfo("[MoreBackpackCapacity] Patched: InventoryInstance(int) ctor.");
        }
        catch (Exception e)
        {
            Log.LogError("[MoreBackpackCapacity] PatchInventoryInstanceCtor failed: " + e);
        }
    }

    // Postfix 签名：object __instance, int defaultStorage
    private static void InventoryInstanceCtor_Postfix(object __instance, int defaultStorage)
    {
        if (_inst == null) return;
        _inst.TryExpandInventoryInstance(__instance, defaultStorage);
    }

    private void TryExpandInventoryInstance(object invInstanceObj, int defaultStorage)
    {
        if (invInstanceObj == null) return;

        int id = GetStableId(invInstanceObj);
        if (_appliedInstances.Contains(id)) return;

        // 可选：只对“默认背包容量=27”这种典型背包用的 InventoryInstance 生效
        int only = OnlyWhenDefaultStorageEquals.Value;
        if (only >= 0 && defaultStorage != only)
        {
            return;
        }

        try
        {
            Type t = invInstanceObj.GetType();

            // private List<ItemOwnInstance> slot;
            var slotField = AccessTools.Field(t, "slot");
            if (slotField == null)
            {
                Log.LogWarning("[MoreBackpackCapacity] InventoryInstance.slot field not found.");
                return;
            }

            object slotListObj = slotField.GetValue(invInstanceObj);
            if (slotListObj == null)
            {
                Log.LogWarning("[MoreBackpackCapacity] InventoryInstance.slot is null.");
                return;
            }

            var listAsIList = slotListObj as System.Collections.IList;
            if (listAsIList == null)
            {
                Log.LogWarning("[MoreBackpackCapacity] InventoryInstance.slot is not IList.");
                return;
            }

            int current = listAsIList.Count;
            int target = current + Mathf.Max(0, ExtraSlots.Value);
            if (MaxStorage.Value > 0) target = Math.Min(target, MaxStorage.Value);
            if (target <= current)
            {
                _appliedInstances.Add(id);
                return;
            }

            // List<T> 的 T
            Type elemType = null;
            if (slotField.FieldType.IsGenericType)
            {
                Type[] args = slotField.FieldType.GetGenericArguments();
                if (args != null && args.Length == 1) elemType = args[0];
            }

            int added = 0;
            while (listAsIList.Count < target)
            {
                object toAdd;
                if (elemType != null && elemType.IsValueType)
                {
                    toAdd = Activator.CreateInstance(elemType);
                }
                else
                {
                    toAdd = null; // ItemOwnInstance 很大概率是 class，null 代表空格
                }

                listAsIList.Add(toAdd);
                added++;
                if (added > 5000) break; // 防爆
            }

            _appliedInstances.Add(id);

            Log.LogInfo(string.Format(
                "[MoreBackpackCapacity] Expanded InventoryInstance slots: {0} -> {1} (added {2}). defaultStorage={3}",
                current, listAsIList.Count, added, defaultStorage
            ));
        }
        catch (Exception e)
        {
            Log.LogError("[MoreBackpackCapacity] TryExpandInventoryInstance exception: " + e);
        }
    }

    // -------------------------
    // Patch 2) GridInventory（如果存在）尽量同步 width/height/storage 字段，帮助 UI 跟随
    // -------------------------
    private void PatchGridInventoryIfExists()
    {
        try
        {
            var gridType = AccessTools.TypeByName("GridInventory");
            if (gridType == null)
            {
                Log.LogWarning("[MoreBackpackCapacity] Type GridInventory not found. (This is OK; ctor patch may already work.)");
                return;
            }

            // Patch 所有 ctor 的 postfix
            var ctors = gridType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ctors == null || ctors.Length == 0)
            {
                Log.LogWarning("[MoreBackpackCapacity] GridInventory ctors not found.");
                return;
            }

            var postfix = new HarmonyMethod(typeof(MoreBackpackCapacityPlugin).GetMethod(
                "GridInventoryCtor_Postfix",
                BindingFlags.Static | BindingFlags.NonPublic));

            int patched = 0;
            foreach (var c in ctors)
            {
                _harmony.Patch(c, null, postfix);
                patched++;
            }

            Log.LogInfo(string.Format("[MoreBackpackCapacity] Patched GridInventory ctors: {0}", patched));
        }
        catch (Exception e)
        {
            Log.LogError("[MoreBackpackCapacity] PatchGridInventoryIfExists failed: " + e);
        }
    }

    private static void GridInventoryCtor_Postfix(object __instance)
    {
        if (_inst == null) return;
        _inst.TryAdjustGridInventory(__instance);
    }

    private void TryAdjustGridInventory(object gridObj)
    {
        if (gridObj == null) return;

        int id = GetStableId(gridObj) ^ 0x5A5A5A5A;
        if (_appliedInstances.Contains(id)) return;

        try
        {
            Type t = gridObj.GetType();

            // 尝试读 Width/Height（字段或属性都试）
            int width = GetIntByName(t, gridObj, new string[] { "Width", "width" }, -1);
            int height = GetIntByName(t, gridObj, new string[] { "Height", "height" }, -1);

            // 尝试读 storage（常见字段/属性名）
            int storage = GetIntByName(t, gridObj, new string[]
            {
                "CurrentInventoryStorage",
                "currentInventoryStorage",
                "inventoryStorage",
                "Storage",
                "storage"
            }, -1);

            // 找到内部 InventoryInstance（常见字段名不确定，所以用“类型名=InventoryInstance”）
            object invInst = FindFirstFieldValueByTypeName(gridObj, "InventoryInstance");
            int invSlotCount = -1;

            if (invInst != null)
            {
                invSlotCount = GetInventoryInstanceSlotCount(invInst);
            }

            // 目标：以“实际 slot 数”为准（更可靠）
            int baseCount = invSlotCount > 0 ? invSlotCount : storage;
            if (baseCount <= 0)
            {
                _appliedInstances.Add(id);
                return;
            }

            int target = baseCount; // 这里不直接 +ExtraSlots，因为 ctor patch 已经做了；避免 double-add
            if (MaxStorage.Value > 0) target = Math.Min(target, MaxStorage.Value);

            // 如果 GridInventory 有 width/height 且 storage≈width*height-k，就推高 height
            if (width > 0 && height > 0)
            {
                int totalCells = width * height;
                if (storage > 0 && storage <= totalCells)
                {
                    int k = totalCells - storage; // 保留差值（比如 30-27=3）
                    int needCells = target + k;
                    int newHeight = (needCells + width - 1) / width;
                    if (newHeight > height)
                    {
                        // 尝试写 height 字段
                        bool heightSet = SetIntByName(t, gridObj, new string[] { "Height", "height" }, newHeight);
                        if (heightSet)
                        {
                            Log.LogInfo(string.Format("[MoreBackpackCapacity] GridInventory height {0}->{1} (width={2}, k={3}, targetStorage={4})",
                                height, newHeight, width, k, target));
                        }
                    }
                }
            }

            // 如果 GridInventory 有 storage 字段并且能写，就尽量写成“slot 数”（UI更可能用它）
            if (storage > 0 && target > storage)
            {
                bool storageSet = SetIntByName(t, gridObj, new string[]
                {
                    "currentInventoryStorage",
                    "inventoryStorage",
                    "Storage",
                    "storage"
                }, target);

                if (storageSet)
                {
                    Log.LogInfo(string.Format("[MoreBackpackCapacity] GridInventory storage {0}->{1}", storage, target));
                }
            }

            _appliedInstances.Add(id);
        }
        catch (Exception e)
        {
            Log.LogError("[MoreBackpackCapacity] TryAdjustGridInventory exception: " + e);
        }
    }

    // -------------------------
    // Periodic Scan：兜底处理“已经存在”的对象
    // -------------------------
    private IEnumerator PeriodicScan()
    {
        while (true)
        {
            // 不要在 catch 里 yield（C# 7.3 会报 CS1626/CS1631）
            bool needWait = false;

            try
            {
                // 扫所有已加载对象，找名字包含 GridInventory / InventoryInstance 的
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var o = all[i];
                        if (o == null) continue;

                        string n = o.GetType().Name;
                        if (n == "GridInventory")
                        {
                            TryAdjustGridInventory(o);
                        }
                        else if (n == "InventoryInstance")
                        {
                            // 这里不知道 defaultStorage，只能跳过（避免误伤），主要靠 ctor patch
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogError("[MoreBackpackCapacity] PeriodicScan exception: " + e);
            }

            needWait = true;
            if (needWait)
                yield return new WaitForSeconds(2.0f);
        }
    }

    // -------------------------
    // Helpers
    // -------------------------
    private static int GetStableId(object obj)
    {
        // 运行期稳定即可
        return obj.GetHashCode();
    }

    private static int GetIntByName(Type t, object inst, string[] names, int fallback)
    {
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];

            // property
            var p = AccessTools.Property(t, name);
            if (p != null && p.CanRead)
            {
                try
                {
                    object v = p.GetValue(inst, null);
                    if (v is int) return (int)v;
                }
                catch { }
            }

            // field
            var f = AccessTools.Field(t, name);
            if (f != null && f.FieldType == typeof(int))
            {
                try
                {
                    object v = f.GetValue(inst);
                    if (v is int) return (int)v;
                }
                catch { }
            }
        }
        return fallback;
    }

    private static bool SetIntByName(Type t, object inst, string[] names, int value)
    {
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];

            // field first（更常见）
            var f = AccessTools.Field(t, name);
            if (f != null && f.FieldType == typeof(int))
            {
                try
                {
                    f.SetValue(inst, value);
                    return true;
                }
                catch { }
            }

            // property with setter
            var p = AccessTools.Property(t, name);
            if (p != null && p.CanWrite && p.PropertyType == typeof(int))
            {
                try
                {
                    p.SetValue(inst, value, null);
                    return true;
                }
                catch { }
            }
        }
        return false;
    }

    private static object FindFirstFieldValueByTypeName(object inst, string typeName)
    {
        if (inst == null) return null;
        Type t = inst.GetType();
        var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (fields == null) return null;

        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            if (f == null) continue;
            Type ft = f.FieldType;
            if (ft != null && ft.Name == typeName)
            {
                try
                {
                    return f.GetValue(inst);
                }
                catch { return null; }
            }
        }
        return null;
    }

    private static int GetInventoryInstanceSlotCount(object invInst)
    {
        if (invInst == null) return -1;
        try
        {
            Type t = invInst.GetType();
            var slotField = AccessTools.Field(t, "slot");
            if (slotField == null) return -1;
            object slotListObj = slotField.GetValue(invInst);
            var listAsIList = slotListObj as System.Collections.IList;
            if (listAsIList == null) return -1;
            return listAsIList.Count;
        }
        catch
        {
            return -1;
        }
    }
}
