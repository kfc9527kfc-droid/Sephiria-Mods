using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using UnityEngine;

[BepInPlugin("more.enemyhp.perfloor", "More Enemy HP Per Floor", "1.0.0")]
public class MoreEnemyHPPerFloorPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    private Harmony _harmony;

    private static ConfigEntry<int> DummyMul;
    private static ConfigEntry<int> Floors;
    private static ConfigEntry<int> NormalBase; // 2^floor
    private static ConfigEntry<int> BossBase;   // 4^floor

    // 标记：某只怪是不是 Boss/训练假人
    private struct SpawnTag
    {
        public bool IsBoss;
        public bool IsDummy;
    }

    // 用 instanceId 做 key（Unity 的 GetInstanceID），避免重复叠乘
    private static readonly Dictionary<int, SpawnTag> Tags = new Dictionary<int, SpawnTag>();
    private static readonly HashSet<int> Scaled = new HashSet<int>();

    // 训练假人关键词（你可以自己再加）
    private static readonly string[] DummyKeywords = new string[]
    {
        "dummy", "training", "scarecrow", "straw", "test"
    };

    private void Awake()
    {
        Log = Logger;

        DummyMul = Config.Bind("倍率", "DummyMultiplier", 10, "训练假人/稻草人血量倍率");
        Floors = Config.Bind("倍率", "Floors", 5, "层数（默认 5）");
        NormalBase = Config.Bind("倍率", "NormalBase", 2, "小怪倍率底数：2^floor");
        BossBase = Config.Bind("倍率", "BossBase", 4, "Boss倍率底数：4^floor");

        _harmony = new Harmony("more.enemyhp.perfloor");
        PatchAllSafe();

        // 兜底：有些训练假人可能不是走 SpawnEntity.Spawn 出来的（比如常驻在场景里）
        StartCoroutine(ScanDummyCoroutine());

        Log.LogInfo("[MoreEnemyHP] Loaded.");
    }

    private void OnDestroy()
    {
        try { _harmony.UnpatchSelf(); } catch { }
    }

    private void PatchAllSafe()
    {
        try
        {
            // 1) Patch EnemySpawnEntity.Spawn(...) -> Postfix 记录 Boss/Dummy 标签
            var enemySpawnEntityType = AccessTools.TypeByName("EnemySpawnEntity");
            if (enemySpawnEntityType == null)
            {
                Log.LogWarning("[MoreEnemyHP] Type not found: EnemySpawnEntity (skip tag patch).");
            }
            else
            {
                // 找到名为 Spawn 的方法（通常只有一个主要 Spawn）
                MethodInfo spawnMi = null;
                foreach (var mi in enemySpawnEntityType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (mi.Name != "Spawn") continue;
                    // 返回值最好是 UnitAvatar（或其子类），我们尽量选“参数最多的那个”作为主 Spawn
                    if (spawnMi == null || mi.GetParameters().Length > spawnMi.GetParameters().Length)
                        spawnMi = mi;
                }

                if (spawnMi == null)
                {
                    Log.LogWarning("[MoreEnemyHP] Method not found: EnemySpawnEntity.Spawn (skip tag patch).");
                }
                else
                {
                    var postfix = new HarmonyMethod(typeof(MoreEnemyHPPerFloorPlugin).GetMethod(
                        nameof(EnemySpawnEntity_Spawn_Postfix),
                        BindingFlags.Static | BindingFlags.NonPublic));

                    _harmony.Patch(spawnMi, postfix: postfix);
                    Log.LogInfo("[MoreEnemyHP] Patched EnemySpawnEntity.Spawn (postfix).");
                }
            }

            // 2) Patch UnitAvatar.HealPercent(float,bool) -> Prefix 放大血量
            var unitAvatarType = AccessTools.TypeByName("UnitAvatar");
            if (unitAvatarType == null)
            {
                Log.LogWarning("[MoreEnemyHP] Type not found: UnitAvatar (skip HP patch).");
                return;
            }

            var healMi = AccessTools.Method(unitAvatarType, "HealPercent", new Type[] { typeof(float), typeof(bool) });
            if (healMi == null)
            {
                // 有些版本 HealPercent 可能参数不同，退而求其次按名字找
                foreach (var mi in unitAvatarType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (mi.Name == "HealPercent")
                    {
                        healMi = mi;
                        break;
                    }
                }
            }

            if (healMi == null)
            {
                Log.LogWarning("[MoreEnemyHP] Method not found: UnitAvatar.HealPercent (skip HP patch).");
                return;
            }

            var prefix2 = new HarmonyMethod(typeof(MoreEnemyHPPerFloorPlugin).GetMethod(
                nameof(UnitAvatar_HealPercent_Prefix),
                BindingFlags.Static | BindingFlags.NonPublic));

            _harmony.Patch(healMi, prefix: prefix2);
            Log.LogInfo("[MoreEnemyHP] Patched UnitAvatar.HealPercent (prefix).");
        }
        catch (Exception e)
        {
            Log.LogError("[MoreEnemyHP] PatchAllSafe exception: " + e);
        }
    }

    // -------------------------
    // Patch 1: 记录 Boss/Dummy
    // -------------------------
    private static void EnemySpawnEntity_Spawn_Postfix(object __instance, ref object __result)
    {
        try
        {
            if (__result == null) return;

            var ua = __result as UnityEngine.Component;// UnitAvatar / 子类一般都是 Component
            if (ua == null) return;

            int id = ua.GetInstanceID();
            var tag = new SpawnTag();

            // Dummy：优先看 prefab/entity 名称 + 生成出来的物体名
            string n1 = "";
            string n2 = "";
            try { n1 = (__instance != null) ? __instance.ToString() : ""; } catch { }
            try { n2 = ua.gameObject != null ? ua.gameObject.name : ""; } catch { }

            if (LooksLikeDummy(n1) || LooksLikeDummy(n2))
                tag.IsDummy = true;

            // Boss：尽量读 monsterType（EMonsterType），否则用名字兜底
            bool isBoss = false;
            if (__instance != null)
            {
                var t = __instance.GetType();
                var f = AccessTools.Field(t, "monsterType");
                if (f != null)
                {
                    object v = f.GetValue(__instance);
                    if (v != null)
                    {
                        string s = v.ToString();
                        // EnemySpawner 里：Normal vs 其他（miniboss/boss/elite）
                        if (!string.Equals(s, "Normal", StringComparison.OrdinalIgnoreCase))
                            isBoss = true;
                        if (s.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0)
                            isBoss = true;
                    }
                }
            }

            // 兜底：名字包含 boss/elite 也认为是 boss
            if (!isBoss)
            {
                if (n1.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0) isBoss = true;
                if (n2.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0) isBoss = true;
                if (n1.IndexOf("elite", StringComparison.OrdinalIgnoreCase) >= 0) isBoss = true;
                if (n2.IndexOf("elite", StringComparison.OrdinalIgnoreCase) >= 0) isBoss = true;
            }

            tag.IsBoss = isBoss;

            Tags[id] = tag;
        }
        catch (Exception e)
        {
            Log.LogError("[MoreEnemyHP] Spawn_Postfix exception: " + e);
        }
    }

    private static bool LooksLikeDummy(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        for (int i = 0; i < DummyKeywords.Length; i++)
        {
            if (name.IndexOf(DummyKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    // ------------------------------------
    // Patch 2: 满血时一次性乘倍率（服务器）
    // ------------------------------------
    private static void UnitAvatar_HealPercent_Prefix(object __instance, float percent)
    {
        try
        {
            // 只在服务器改（单机=服务器；联机=房主服务器）
            if (!NetworkServer.active) return;

            // 我们只想抓“刷怪/初始化时的 100% 满血”
            if (percent < 99.9f) return;

            var comp = __instance as UnityEngine.Component;// UnitAvatar 是 Component
            if (comp == null) return;

            // 排除玩家：有 PlayerAvatar 就跳过
            if (comp.GetComponent("PlayerAvatar") != null) return;

            int id = comp.GetInstanceID();
            if (Scaled.Contains(id)) return;

            // 判定标签
            SpawnTag tag;
            bool hasTag = Tags.TryGetValue(id, out tag);

            bool isDummy = (hasTag && tag.IsDummy) || LooksLikeDummy(comp.gameObject != null ? comp.gameObject.name : "");
            bool isBoss = (hasTag && tag.IsBoss);

            int floor = GetFloorIndex_1to5();
            int mul = 1;

            if (isDummy)
            {
                mul = Mathf.Max(1, DummyMul.Value);
            }
            else if (isBoss)
            {
                mul = PowInt(Mathf.Max(1, BossBase.Value), floor);
            }
            else
            {
                mul = PowInt(Mathf.Max(1, NormalBase.Value), floor);
            }

            if (mul <= 1) { Scaled.Add(id); return; }

            // 通过反射改 UnitAvatar 的字段/属性（避免你版本字段名微调导致编译期报错）
            int changed = ApplyHpMultiplierViaReflection(__instance, mul);

            if (changed > 0)
            {
                Scaled.Add(id);
                // Log.LogInfo(string.Format("[MoreEnemyHP] Applied x{0} floor={1} boss={2} dummy={3} to {4}",
                //     mul, floor, isBoss, isDummy, comp.gameObject != null ? comp.gameObject.name : "UnitAvatar"));
            }
        }
        catch (Exception e)
        {
            Log.LogError("[MoreEnemyHP] HealPercent_Prefix exception: " + e);
        }
    }

    private static int ApplyHpMultiplierViaReflection(object unitAvatarObj, int mul)
    {
        int changed = 0;
        var t = unitAvatarObj.GetType();

        // 常见字段/属性名（你贴的刷怪代码里用到这些）
        // maxHp (field), currentHp (field), NetworkmaxHp (property), NetworkcurrentHp (property)
        // 有些版本可能是 MaxHp/CurrentHp 等，我们都试一下
        changed += MulIntFieldIfExists(t, unitAvatarObj, "maxHp", mul);
        changed += MulIntFieldIfExists(t, unitAvatarObj, "MaxHp", mul);

        // NetworkmaxHp 属性（Mirror SyncVar property）
        changed += MulIntPropertyIfExists(t, unitAvatarObj, "NetworkmaxHp", mul);
        changed += MulIntPropertyIfExists(t, unitAvatarObj, "NetworkMaxHp", mul);

        // currentHp
        changed += MulIntFieldIfExists(t, unitAvatarObj, "currentHp", mul);
        changed += MulIntFieldIfExists(t, unitAvatarObj, "CurrentHp", mul);

        // NetworkcurrentHp
        changed += MulIntPropertyIfExists(t, unitAvatarObj, "NetworkcurrentHp", mul);
        changed += MulIntPropertyIfExists(t, unitAvatarObj, "NetworkCurrentHp", mul);

        // 最后：如果能拿到 NetworkmaxHp，就强制把 currentHp 同步到 max（避免满血显示不一致）
        int netMax = ReadIntPropertyOrField(t, unitAvatarObj, "NetworkmaxHp", "NetworkMaxHp", "maxHp", "MaxHp");
        if (netMax > 0)
        {
            SetIntFieldIfExists(t, unitAvatarObj, "currentHp", netMax);
            SetIntFieldIfExists(t, unitAvatarObj, "CurrentHp", netMax);
            SetIntPropertyIfExists(t, unitAvatarObj, "NetworkcurrentHp", netMax);
            SetIntPropertyIfExists(t, unitAvatarObj, "NetworkCurrentHp", netMax);
        }

        return changed;
    }

    private static int MulIntFieldIfExists(Type t, object obj, string fieldName, int mul)
    {
        var f = AccessTools.Field(t, fieldName);
        if (f == null) return 0;
        if (f.FieldType != typeof(int)) return 0;
        int v = (int)f.GetValue(obj);
        if (v <= 0) return 0;
        f.SetValue(obj, checked(v * mul));
        return 1;
    }

    private static int MulIntPropertyIfExists(Type t, object obj, string propName, int mul)
    {
        var p = AccessTools.Property(t, propName);
        if (p == null) return 0;
        if (p.PropertyType != typeof(int)) return 0;
        if (!p.CanRead || !p.CanWrite) return 0;
        int v = (int)p.GetValue(obj, null);
        if (v <= 0) return 0;
        p.SetValue(obj, checked(v * mul), null);
        return 1;
    }

    private static void SetIntFieldIfExists(Type t, object obj, string fieldName, int value)
    {
        var f = AccessTools.Field(t, fieldName);
        if (f == null) return;
        if (f.FieldType != typeof(int)) return;
        f.SetValue(obj, value);
    }

    private static void SetIntPropertyIfExists(Type t, object obj, string propName, int value)
    {
        var p = AccessTools.Property(t, propName);
        if (p == null) return;
        if (p.PropertyType != typeof(int)) return;
        if (!p.CanWrite) return;
        p.SetValue(obj, value, null);
    }

    private static int ReadIntPropertyOrField(Type t, object obj, params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            var p = AccessTools.Property(t, names[i]);
            if (p != null && p.PropertyType == typeof(int) && p.CanRead)
            {
                try { return (int)p.GetValue(obj, null); } catch { }
            }

            var f = AccessTools.Field(t, names[i]);
            if (f != null && f.FieldType == typeof(int))
            {
                try { return (int)f.GetValue(obj); } catch { }
            }
        }
        return 0;
    }

    // -------------------------
    // Floor：尽量从 DungeonManager 里读（反射）
    // -------------------------
    private static int GetFloorIndex_1to5()
    {
        int floor = 1;

        try
        {
            var dmType = AccessTools.TypeByName("DungeonManager");
            if (dmType == null) return 1;

            // DungeonManager.Instance
            object inst = null;
            var instProp = AccessTools.Property(dmType, "Instance");
            if (instProp != null) inst = instProp.GetValue(null, null);
            if (inst == null)
            {
                var instField = AccessTools.Field(dmType, "Instance");
                if (instField != null) inst = instField.GetValue(null);
            }
            if (inst == null) return 1;

            // 常见字段名候选（你版本可能不同）
            // 如果读到 0~4，当作 floorIndex -> +1
            // 如果读到 1~5，直接用
            int raw = TryReadInt(inst, dmType, new string[]
            {
                "currentFloor", "CurrentFloor",
                "floor", "Floor",
                "floorIndex", "FloorIndex",
                "stage", "Stage",
                "stageIndex", "StageIndex",
                "currentStage", "CurrentStage",
                "currentStageIndex", "CurrentStageIndex",
            });

            if (raw >= 1 && raw <= 50) floor = raw;
            else if (raw >= 0 && raw <= 49) floor = raw + 1;
        }
        catch { }

        int max = Mathf.Max(1, Floors.Value);
        if (floor < 1) floor = 1;
        if (floor > max) floor = max;
        return floor;
    }

    private static int TryReadInt(object inst, Type t, string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            var p = AccessTools.Property(t, names[i]);
            if (p != null && p.PropertyType == typeof(int) && p.CanRead)
            {
                try { return (int)p.GetValue(inst, null); } catch { }
            }
            var f = AccessTools.Field(t, names[i]);
            if (f != null && f.FieldType == typeof(int))
            {
                try { return (int)f.GetValue(inst); } catch { }
            }
        }
        return int.MinValue;
    }

    private static int PowInt(int b, int exp)
    {
        // exp 从 1 开始：floor=1 -> b^1
        if (exp < 1) exp = 1;
        long r = 1;
        for (int i = 0; i < exp; i++) r *= b;
        if (r > int.MaxValue) return int.MaxValue;
        return (int)r;
    }

    // -------------------------
    // 兜底扫描：常驻 Dummy 可能不走 Spawn
    // -------------------------
    private IEnumerator ScanDummyCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(1.0f);

            if (!NetworkServer.active) continue;

            try
            {
                // 找场景中所有带 UnitAvatar 的对象，名字像 dummy 就强行打标签
                var unitAvatarType = AccessTools.TypeByName("UnitAvatar");
                if (unitAvatarType == null) continue;

                var all = GameObject.FindObjectsOfType<MonoBehaviour>();
                for (int i = 0; i < all.Length; i++)
                {
                    var mb = all[i];
                    if (mb == null) continue;
                    if (mb.GetType() != unitAvatarType && !mb.GetType().IsSubclassOf(unitAvatarType)) continue;

                    var go = mb.gameObject;
                    if (go == null) continue;

                    if (!LooksLikeDummy(go.name)) continue;

                    int id = mb.GetInstanceID();
                    SpawnTag tag;
                    Tags.TryGetValue(id, out tag);
                    if (!tag.IsDummy)
                    {
                        tag.IsDummy = true;
                        Tags[id] = tag;
                    }
                }
            }
            catch { }
        }
    }
}
