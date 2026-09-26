using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("hazel.MoreStartingTalent", "More Starting Talent", "1.2.0")]
public class MoreStartingTalentPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    internal static ConfigEntry<int> From;
    internal static ConfigEntry<int> Add;
    internal static ConfigEntry<bool> VerboseLog;

    internal static ConfigEntry<int> ScanSeconds;          // 扫描多久
    internal static ConfigEntry<float> ScanIntervalSeconds; // 扫描频率
    internal static ConfigEntry<bool> ScanResourcesAll;    // 是否用 FindObjectsOfTypeAll 扫更广

    private Harmony _harmony;

    private static bool _appliedOnce = false;
    private Coroutine _scanCoroutine;

    private void Awake()
    {
        Log = Logger;

        From = Config.Bind("General", "From", 40, "Replace this exact value (usually 40) when found.");
        Add = Config.Bind("General", "Add", 20, "Add this value to From (40+20=60).");
        VerboseLog = Config.Bind("General", "VerboseLog", false, "Log GetInt/SetInt keys and results (noisy).");

        ScanSeconds = Config.Bind("Scan", "ScanSeconds", 120, "How many seconds to keep scanning for runtime fields/properties.");
        ScanIntervalSeconds = Config.Bind("Scan", "ScanIntervalSeconds", 0.5f, "Seconds between scans.");
        ScanResourcesAll = Config.Bind("Scan", "ScanResourcesAll", true, "Also scan Resources.FindObjectsOfTypeAll (slower but more effective).");

        _harmony = new Harmony("hazel.MoreStartingTalent");
        TryPatchSaveData();

        // 场景切换时再扫一遍（很多值是在进入某个界面/场景后才生成）
        SceneManager.sceneLoaded += OnSceneLoaded;

        // 启动第一次扫描
        StartOrRestartScan("Awake");

        Log.LogInfo("[MoreStartingTalent] Loaded.");
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_appliedOnce) return;
        StartOrRestartScan("SceneLoaded:" + scene.name);
    }

    private void StartOrRestartScan(string reason)
    {
        try
        {
            if (_scanCoroutine != null) StopCoroutine(_scanCoroutine);
        }
        catch { }

        Log.LogInfo(string.Format("[MoreStartingTalent] Start runtime scan. reason={0}", reason));
        _scanCoroutine = StartCoroutine(ScanUntilApplied());
    }

    private void TryPatchSaveData()
    {
        try
        {
            var saveDataType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .FirstOrDefault(t => t != null && t.Name == "SaveData");

            if (saveDataType == null)
            {
                Log.LogWarning("[MoreStartingTalent] SaveData type not found. Will rely on runtime/static scan.");
                return;
            }

            var miGetInt = saveDataType.GetMethod("GetInt",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(string), typeof(int) }, null);

            var miSetInt = saveDataType.GetMethod("SetInt",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { typeof(string), typeof(int) }, null);

            if (miGetInt != null)
            {
                _harmony.Patch(miGetInt,
                    postfix: new HarmonyMethod(typeof(MoreStartingTalentPlugin).GetMethod(
                        nameof(SaveData_GetInt_Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.LogInfo("[MoreStartingTalent] Patched: SaveData.GetInt(string,int)");
            }
            else
            {
                Log.LogWarning("[MoreStartingTalent] SaveData.GetInt not found.");
            }

            if (miSetInt != null)
            {
                _harmony.Patch(miSetInt,
                    prefix: new HarmonyMethod(typeof(MoreStartingTalentPlugin).GetMethod(
                        nameof(SaveData_SetInt_Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.LogInfo("[MoreStartingTalent] Patched: SaveData.SetInt(string,int)");
            }
            else
            {
                Log.LogWarning("[MoreStartingTalent] SaveData.SetInt not found.");
            }
        }
        catch (Exception e)
        {
            Log.LogError(string.Format("[MoreStartingTalent] Patch exception: {0}", e));
        }
    }

    // -------------------------
    // SaveData.GetInt postfix
    // -------------------------
    private static void SaveData_GetInt_Postfix(string key, int fallback, ref int __result)
    {
        try
        {
            if (VerboseLog.Value)
                Log.LogInfo(string.Format("[MoreStartingTalent] GetInt key={0} result={1} fallback={2}", key, __result, fallback));

            if (!KeyLooksPointRelated(key)) return;

            int from = From.Value;
            int to = from + Add.Value;

            if (__result == from)
            {
                __result = to;
                Log.LogInfo(string.Format("[MoreStartingTalent] GetInt override: key={0} {1}->{2}", key, from, to));
            }
        }
        catch (Exception e)
        {
            Log.LogError(string.Format("[MoreStartingTalent] GetInt postfix exception: {0}", e));
        }
    }

    // -------------------------
    // SaveData.SetInt prefix
    // -------------------------
    private static void SaveData_SetInt_Prefix(string key, ref int value)
    {
        try
        {
            if (VerboseLog.Value)
                Log.LogInfo(string.Format("[MoreStartingTalent] SetInt key={0} value={1}", key, value));

            if (!KeyLooksPointRelated(key)) return;

            int from = From.Value;
            int to = from + Add.Value;

            if (value == from)
            {
                value = to;
                Log.LogInfo(string.Format("[MoreStartingTalent] SetInt override: key={0} {1}->{2}", key, from, to));
            }
        }
        catch (Exception e)
        {
            Log.LogError(string.Format("[MoreStartingTalent] SetInt prefix exception: {0}", e));
        }
    }

    private static bool KeyLooksPointRelated(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;

        // 你日志里出现过 PassivePoint_*
        if (key.StartsWith("PassivePoint_", StringComparison.OrdinalIgnoreCase)) return true;

        string k = key.ToLowerInvariant();
        if (k.Contains("talent")) return true;
        if (k.Contains("passivepoint")) return true;
        if (k.Contains("passive_point")) return true;
        if (k.Contains("talentpoint")) return true;
        if (k.Contains("talent_point")) return true;
        if (k.Contains("startingtalent")) return true;
        if (k.Contains("starting_talent")) return true;

        if (k.Contains("wrath")) return true;
        if (k.Contains("swiftness")) return true;
        if (k.Contains("survival")) return true;
        if (k.Contains("endure")) return true;
        if (k.Contains("wisdom")) return true;
        if (k.Contains("willpower")) return true;
        if (k.Contains("ingenuity")) return true;

        // 也把 hardmode 相关算进去（因为你的“40”很可能是 hardmode 通关计算出来的）
        if (k.Contains("hardmode")) return true;

        return false;
    }

    // -------------------------
    // 强化扫描：场景对象 + FindObjectsOfTypeAll + 静态字段
    // -------------------------
    private IEnumerator ScanUntilApplied()
    {
        int from = From.Value;
        int to = from + Add.Value;

        float endTime = Time.realtimeSinceStartup + Mathf.Max(5f, ScanSeconds.Value);
        float interval = Mathf.Max(0.1f, ScanIntervalSeconds.Value);

        while (!_appliedOnce && Time.realtimeSinceStartup < endTime)
        {
            int changed = 0;

            try
            {
                // A) 扫场景里活着的 MonoBehaviour
                changed += ScanUnityObjects(UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true), from, to);

                // B) 扫资源里所有 UnityEngine.Object（更强，但更慢）
                if (!_appliedOnce && ScanResourcesAll.Value)
                {
                    // 只扫我们关心的类型名（减少开销）
                    var all = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
                    var filtered = all.Where(o =>
                    {
                        if (o == null) return false;
                        var tn = o.GetType().Name;
                        if (string.IsNullOrEmpty(tn)) return false;
                        tn = tn.ToLowerInvariant();
                        return tn.Contains("talent") || tn.Contains("passive") || tn.Contains("point") || tn.Contains("hardmode")
                               || tn.Contains("skill") || tn.Contains("stat") || tn.Contains("perk");
                    }).ToArray();

                    changed += ScanUnityObjects(filtered, from, to);
                }

                // C) 扫静态字段/属性（很多“40”可能就是常量或静态配置）
                if (!_appliedOnce)
                {
                    changed += ScanStaticInts(from, to);
                }
            }
            catch (Exception e)
            {
                Log.LogError(string.Format("[MoreStartingTalent] Scan exception: {0}", e));
            }

            if (changed > 0)
            {
                _appliedOnce = true;
                Log.LogInfo(string.Format("[MoreStartingTalent] Applied! changed={0}, from={1}, to={2}.", changed, from, to));
                yield break;
            }

            yield return new WaitForSeconds(interval);
        }

        if (!_appliedOnce)
        {
            Log.LogWarning(string.Format(
                "[MoreStartingTalent] Runtime scan finished but changed=0 (value {0} may be computed as local variable or stored differently).",
                From.Value));
        }
    }

    private int ScanUnityObjects(UnityEngine.Object[] objs, int from, int to)
    {
        if (objs == null || objs.Length == 0) return 0;

        int changed = 0;

        for (int i = 0; i < objs.Length; i++)
        {
            var o = objs[i];
            if (o == null) continue;

            var t = o.GetType();
            string tn = t.Name;
            if (string.IsNullOrEmpty(tn)) continue;

            // 额外过滤一下类型名，避免扫太多
            string tnl = tn.ToLowerInvariant();
            if (!(tnl.Contains("talent") || tnl.Contains("passive") || tnl.Contains("point") || tnl.Contains("hardmode")
                  || tnl.Contains("skill") || tnl.Contains("stat") || tnl.Contains("perk")))
            {
                // 如果你担心漏掉，可以注释掉这段过滤
                // continue;
            }

            // 扫字段
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int fi = 0; fi < fields.Length; fi++)
            {
                var f = fields[fi];
                if (f.FieldType != typeof(int)) continue;
                if (!NameLooksPointRelated(f.Name)) continue;

                int v;
                try { v = (int)f.GetValue(o); } catch { continue; }

                if (v == from)
                {
                    try
                    {
                        f.SetValue(o, to);
                        changed++;
                        Log.LogInfo(string.Format("[MoreStartingTalent] Patched field: {0}.{1} {2}->{3}",
                            t.Name, f.Name, from, to));
                    }
                    catch { }
                }
            }

            // 扫属性
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int pi = 0; pi < props.Length; pi++)
            {
                var p = props[pi];
                if (p.PropertyType != typeof(int)) continue;
                if (!p.CanRead || !p.CanWrite) continue;
                if (!NameLooksPointRelated(p.Name)) continue;

                int v;
                try { v = (int)p.GetValue(o, null); } catch { continue; }

                if (v == from)
                {
                    try
                    {
                        p.SetValue(o, to, null);
                        changed++;
                        Log.LogInfo(string.Format("[MoreStartingTalent] Patched prop: {0}.{1} {2}->{3}",
                            t.Name, p.Name, from, to));
                    }
                    catch { }
                }
            }
        }

        return changed;
    }

    private int ScanStaticInts(int from, int to)
    {
        int changed = 0;

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int ai = 0; ai < assemblies.Length; ai++)
        {
            Type[] types;
            try { types = assemblies[ai].GetTypes(); }
            catch { continue; }

            for (int ti = 0; ti < types.Length; ti++)
            {
                var t = types[ti];
                if (t == null) continue;

                string tn = t.Name;
                if (string.IsNullOrEmpty(tn)) continue;

                string tnl = tn.ToLowerInvariant();
                if (!(tnl.Contains("talent") || tnl.Contains("passive") || tnl.Contains("point") || tnl.Contains("hardmode")
                      || tnl.Contains("skill") || tnl.Contains("stat") || tnl.Contains("perk")))
                {
                    continue;
                }

                // 静态字段
                var sfields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int fi = 0; fi < sfields.Length; fi++)
                {
                    var f = sfields[fi];
                    if (f.FieldType != typeof(int)) continue;
                    if (!NameLooksPointRelated(f.Name)) continue;

                    int v;
                    try { v = (int)f.GetValue(null); } catch { continue; }

                    if (v == from)
                    {
                        try
                        {
                            f.SetValue(null, to);
                            changed++;
                            Log.LogInfo(string.Format("[MoreStartingTalent] Patched STATIC field: {0}.{1} {2}->{3}",
                                t.FullName, f.Name, from, to));
                        }
                        catch { }
                    }
                }

                // 静态属性
                var sprops = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int pi = 0; pi < sprops.Length; pi++)
                {
                    var p = sprops[pi];
                    if (p.PropertyType != typeof(int)) continue;
                    if (!p.CanRead || !p.CanWrite) continue;
                    if (!NameLooksPointRelated(p.Name)) continue;

                    int v;
                    try { v = (int)p.GetValue(null, null); } catch { continue; }

                    if (v == from)
                    {
                        try
                        {
                            p.SetValue(null, to, null);
                            changed++;
                            Log.LogInfo(string.Format("[MoreStartingTalent] Patched STATIC prop: {0}.{1} {2}->{3}",
                                t.FullName, p.Name, from, to));
                        }
                        catch { }
                    }
                }
            }
        }

        return changed;
    }

    private static bool NameLooksPointRelated(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();

        if (n.Contains("talent")) return true;
        if (n.Contains("passive")) return true;
        if (n.Contains("point")) return true;
        if (n.Contains("skill")) return true;
        if (n.Contains("stat")) return true;
        if (n.Contains("perk")) return true;
        if (n.Contains("hardmode")) return true;

        if (n.Contains("wrath")) return true;
        if (n.Contains("swiftness")) return true;
        if (n.Contains("survival")) return true;
        if (n.Contains("endure")) return true;
        if (n.Contains("wisdom")) return true;
        if (n.Contains("willpower")) return true;
        if (n.Contains("ingenuity")) return true;

        return false;
    }
}
