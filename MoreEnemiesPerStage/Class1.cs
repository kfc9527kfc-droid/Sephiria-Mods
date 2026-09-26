using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[BepInPlugin("hazel.sephiria.more_enemies_per_stage", "More Enemies Per Stage", "1.0.0")]
public class MoreEnemiesPerStagePlugin : BaseUnityPlugin
{
    internal static MoreEnemiesPerStagePlugin Log;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> CountMultiplier;
    internal static ConfigEntry<int> AdditiveCount;
    internal static ConfigEntry<int> MaxCountPerSpawnData;

    // 防止同一个 spawner 被重复乘（进入下一 phase/重复触发 ServerBeginSpawn 时也不叠乘）
    private static readonly HashSet<int> PatchedSpawnerInstanceIds = new HashSet<int>();

    private void Awake()
    {
        Log = this;

        Enabled = Config.Bind("General", "Enabled", true, "Enable this mod.");
        CountMultiplier = Config.Bind("General", "CountMultiplier", 2.0f, "Multiply MonsterSpawnData.count by this value (server/host only).");
        AdditiveCount = Config.Bind("General", "AdditiveCount", 0, "After multiplying, add this number to each MonsterSpawnData.count.");
        MaxCountPerSpawnData = Config.Bind("General", "MaxCountPerSpawnData", 999, "Clamp each MonsterSpawnData.count to this max.");

        var h = new Harmony("hazel.sephiria.more_enemies_per_stage");
        h.PatchAll();

        Logger.LogInfo("[MoreEnemiesPerStage] Loaded.");
    }

    // =========================
    // Core: Apply multiplier once
    // =========================
    internal static void TryApplyToSpawner(Component spawner, string phaseListFieldName)
    {
        if (!Enabled.Value) return;
        if (spawner == null) return;

        // 只让服务器/房主改（单机也是 host/server）
        if (!NetworkServer.active) return;

        int id = spawner.GetInstanceID();
        if (PatchedSpawnerInstanceIds.Contains(id)) return;

        bool ok = MultiplyCountsInSpawner(spawner, phaseListFieldName);
        if (ok)
        {
            PatchedSpawnerInstanceIds.Add(id);
        }
    }

    private static bool MultiplyCountsInSpawner(Component spawner, string phaseListFieldName)
    {
        try
        {
            float mul = Mathf.Max(0.01f, CountMultiplier.Value);
            int add = AdditiveCount.Value;
            int clampMax = Mathf.Max(1, MaxCountPerSpawnData.Value);

            // EnemySpawner/CommonEnemySpawner: field = spawnDatasByPhase (List<MonsterSpawnPhase>)
            object phaseListObj = GetMemberValue(spawner, phaseListFieldName);
            IList phaseList = phaseListObj as IList;
            if (phaseList == null)
            {
                Log.Logger.LogWarning(string.Format("[MoreEnemiesPerStage] {0}.{1} not found or not IList.",
                    spawner.GetType().Name, phaseListFieldName));
                return false;
            }

            int changed = 0;
            int total = 0;

            for (int p = 0; p < phaseList.Count; p++)
            {
                object phase = phaseList[p];
                if (phase == null) continue;

                object spawnDatasObj = GetMemberValue(phase, "spawnDatas");
                IList spawnDatas = spawnDatasObj as IList;
                if (spawnDatas == null) continue;

                for (int i = 0; i < spawnDatas.Count; i++)
                {
                    object spawnData = spawnDatas[i];
                    if (spawnData == null) continue;

                    int oldCount;
                    if (!TryGetIntMember(spawnData, "count", out oldCount)) continue;

                    total++;

                    int newCount = Mathf.CeilToInt(oldCount * mul) + add;
                    if (newCount < 1) newCount = 1;
                    if (newCount > clampMax) newCount = clampMax;

                    if (newCount != oldCount)
                    {
                        if (TrySetIntMember(spawnData, "count", newCount))
                            changed++;
                    }
                }
            }

            if (changed > 0)
            {
                Log.Logger.LogInfo(string.Format(
                    "[MoreEnemiesPerStage] Applied to {0}#{1}: changed {2}/{3} spawnDatas. mul={4}, add={5}, max={6}",
                    spawner.GetType().Name, spawner.GetInstanceID(), changed, total, mul, add, clampMax));
            }
            else
            {
                Log.Logger.LogInfo(string.Format(
                    "[MoreEnemiesPerStage] No changes for {0}#{1} (total spawnDatas={2}).",
                    spawner.GetType().Name, spawner.GetInstanceID(), total));
            }

            return true;
        }
        catch (Exception e)
        {
            Log.Logger.LogError(string.Format("[MoreEnemiesPerStage] MultiplyCountsInSpawner failed on {0}: {1}",
                spawner != null ? spawner.GetType().Name : "null", e));
            return false;
        }
    }

    // =========================
    // RandomEnemyPhaseSpawner support
    // =========================
    internal static void TryApplyToRandomEnemyPhaseSpawner(Component randomSpawner)
    {
        if (!Enabled.Value) return;
        if (randomSpawner == null) return;
        if (!NetworkServer.active) return;

        int id = randomSpawner.GetInstanceID();
        if (PatchedSpawnerInstanceIds.Contains(id)) return;

        try
        {
            // RandomEnemyPhaseSpawner: this.spawnPhases.phases[...].spawnDatas[...].count
            object spawnPhasesObj = GetMemberValue(randomSpawner, "spawnPhases");
            if (spawnPhasesObj == null)
            {
                Log.Logger.LogWarning("[MoreEnemiesPerStage] RandomEnemyPhaseSpawner.spawnPhases is null.");
                return;
            }

            object phasesObj = GetMemberValue(spawnPhasesObj, "phases");
            IList phases = phasesObj as IList;
            if (phases == null)
            {
                Log.Logger.LogWarning("[MoreEnemiesPerStage] RandomEnemyPhaseSpawner.spawnPhases.phases not found or not IList.");
                return;
            }

            float mul = Mathf.Max(0.01f, CountMultiplier.Value);
            int add = AdditiveCount.Value;
            int clampMax = Mathf.Max(1, MaxCountPerSpawnData.Value);

            int changed = 0;
            int total = 0;

            for (int p = 0; p < phases.Count; p++)
            {
                object phase = phases[p];
                if (phase == null) continue;

                object spawnDatasObj = GetMemberValue(phase, "spawnDatas");
                IList spawnDatas = spawnDatasObj as IList;
                if (spawnDatas == null) continue;

                for (int i = 0; i < spawnDatas.Count; i++)
                {
                    object spawnData = spawnDatas[i];
                    if (spawnData == null) continue;

                    int oldCount;
                    if (!TryGetIntMember(spawnData, "count", out oldCount)) continue;

                    total++;

                    int newCount = Mathf.CeilToInt(oldCount * mul) + add;
                    if (newCount < 1) newCount = 1;
                    if (newCount > clampMax) newCount = clampMax;

                    if (newCount != oldCount)
                    {
                        if (TrySetIntMember(spawnData, "count", newCount))
                            changed++;
                    }
                }
            }

            PatchedSpawnerInstanceIds.Add(id);
            Log.Logger.LogInfo(string.Format(
                "[MoreEnemiesPerStage] Applied to RandomEnemyPhaseSpawner#{0}: changed {1}/{2} spawnDatas. mul={3}, add={4}, max={5}",
                id, changed, total, mul, add, clampMax));
        }
        catch (Exception e)
        {
            Log.Logger.LogError(string.Format("[MoreEnemiesPerStage] TryApplyToRandomEnemyPhaseSpawner failed: {0}", e));
        }
    }

    // =========================
    // Reflection helpers (field/property)
    // =========================
    private static object GetMemberValue(object obj, string name)
    {
        if (obj == null) return null;
        var t = obj.GetType();

        var f = AccessTools.Field(t, name);
        if (f != null) return f.GetValue(obj);

        var p = AccessTools.Property(t, name);
        if (p != null) return p.GetValue(obj, null);

        return null;
    }

    private static bool TryGetIntMember(object obj, string name, out int value)
    {
        value = 0;
        if (obj == null) return false;
        var t = obj.GetType();

        var f = AccessTools.Field(t, name);
        if (f != null && f.FieldType == typeof(int))
        {
            value = (int)f.GetValue(obj);
            return true;
        }

        var p = AccessTools.Property(t, name);
        if (p != null && p.PropertyType == typeof(int) && p.CanRead)
        {
            value = (int)p.GetValue(obj, null);
            return true;
        }

        return false;
    }

    private static bool TrySetIntMember(object obj, string name, int value)
    {
        if (obj == null) return false;
        var t = obj.GetType();

        var f = AccessTools.Field(t, name);
        if (f != null && f.FieldType == typeof(int))
        {
            f.SetValue(obj, value);
            return true;
        }

        var p = AccessTools.Property(t, name);
        if (p != null && p.PropertyType == typeof(int) && p.CanWrite)
        {
            p.SetValue(obj, value, null);
            return true;
        }

        return false;
    }

    // =========================
    // Harmony patches
    // =========================

    [HarmonyPatch(typeof(EnemySpawner), "ServerBeginSpawn")]
    private static class Patch_EnemySpawner_ServerBeginSpawn
    {
        private static void Postfix(EnemySpawner __instance)
        {
            TryApplyToSpawner(__instance, "spawnDatasByPhase");
        }
    }

    [HarmonyPatch(typeof(CommonEnemySpawner), "ServerBeginSpawn")]
    private static class Patch_CommonEnemySpawner_ServerBeginSpawn
    {
        private static void Postfix(CommonEnemySpawner __instance)
        {
            TryApplyToSpawner(__instance, "spawnDatasByPhase");
        }
    }

    [HarmonyPatch(typeof(RandomEnemyPhaseSpawner), "StartSpawn")]
    private static class Patch_RandomEnemyPhaseSpawner_StartSpawn
    {
        private static void Prefix(RandomEnemyPhaseSpawner __instance)
        {
            TryApplyToRandomEnemyPhaseSpawner(__instance);
        }
    }
}
