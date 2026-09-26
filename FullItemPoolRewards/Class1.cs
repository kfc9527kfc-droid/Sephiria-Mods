// FullItemPoolRewardsPlugin.cs
// 编译输出 DLL -> 放到  S:\SteamLibrary\steamapps\common\Sephiria\BepInEx\plugins\

using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

[BepInPlugin("hazel.sephiria.fullitempoolrewards", "Full Item Pool Rewards (Pick3)", "1.0.3")]
public class FullItemPoolRewardsPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    private Harmony _harmony;

    // 每次奖励允许选择次数
    private const int PicksPerReward = 3;

    // ======== 方案A：客户端->服务器 请求选择奖励 ========
    public struct PickRewardMsg : NetworkMessage
    {
        public uint avatarNetId;
        public uint sephNetId;
        public int itemId;
        public bool finishNow; // true = 最后一次选择 -> 结束本次奖励
    }

    private static bool _serverHandlerRegistered;
    private static bool _clientActiveLogged;
    private static bool _serializationRegistered;

    private void Awake()
    {
        Log = Logger;
        _harmony = new Harmony("hazel.sephiria.fullitempoolrewards.pick3");

        try
        {
            EnsurePickRewardMsgSerialization();

            _harmony.PatchAll(typeof(FullItemPoolRewardsPlugin));
            TryPatchJournalIconClicks(_harmony);

            StartCoroutine(EnsureNetworkHandlers());

            Log.LogInfo("Full Item Pool Rewards (Pick3) loaded.");
        }
        catch (Exception ex)
        {
            Log.LogError("PatchAll failed: " + ex);
        }
    }

    private static IEnumerator EnsureNetworkHandlers()
    {
        WaitForSeconds wait = new WaitForSeconds(0.2f);

        while (true)
        {
            try
            {
                EnsurePickRewardMsgSerialization();

                if (!_serverHandlerRegistered && NetworkServer.active)
                {
                    RegisterServerHandler();
                    _serverHandlerRegistered = true;
                    Log.LogInfo("[Net] Server handler registered.");
                }

                if (!_clientActiveLogged && NetworkClient.active)
                {
                    _clientActiveLogged = true;
                    Log.LogInfo("[Net] Client active.");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Net] EnsureNetworkHandlers error: " + ex.Message);
            }

            yield return wait;
        }
    }

    private static void RegisterServerHandler()
    {
        try
        {
            Type serverType = typeof(NetworkServer);
            MethodInfo[] methods = serverType.GetMethods(BindingFlags.Public | BindingFlags.Static);

            MethodInfo target = null;
            foreach (MethodInfo m in methods)
            {
                if (m.Name != "RegisterHandler") continue;
                if (!m.IsGenericMethodDefinition) continue;

                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length < 1) continue;

                if (ps[0].ParameterType.Name.StartsWith("Action"))
                {
                    target = m;
                    break;
                }
            }

            if (target == null)
            {
                Log.LogWarning("[Net] NetworkServer.RegisterHandler<T> not found. Client picks may not work.");
                return;
            }

            MethodInfo g = target.MakeGenericMethod(typeof(PickRewardMsg));
            Action<NetworkConnectionToClient, PickRewardMsg> del = OnPickRewardMsg;

            ParameterInfo[] pinfo = g.GetParameters();
            object[] args;

            if (pinfo.Length == 2 && pinfo[1].ParameterType == typeof(bool))
            {
                args = new object[] { del, false };
            }
            else
            {
                args = new object[] { del };
            }

            g.Invoke(null, args);
        }
        catch (Exception ex)
        {
            Log.LogError("[Net] RegisterServerHandler failed: " + ex);
        }
    }

    private static void OnPickRewardMsg(NetworkConnectionToClient conn, PickRewardMsg msg)
    {
        try
        {
            if (!NetworkServer.active) return;

            if (!NetworkServer.spawned.TryGetValue(msg.avatarNetId, out var avatarId) || avatarId == null)
            {
                Log.LogWarning(string.Format("[Net] Avatar netId not found: {0}", msg.avatarNetId));
                return;
            }

            if (!NetworkServer.spawned.TryGetValue(msg.sephNetId, out var sephId) || sephId == null)
            {
                Log.LogWarning(string.Format("[Net] Sephirite netId not found: {0}", msg.sephNetId));
                return;
            }

            PlayerAvatar avatar = avatarId.GetComponent<PlayerAvatar>();
            Sephirite seph = sephId.GetComponent<Sephirite>();
            if (avatar == null || seph == null)
            {
                Log.LogWarning("[Net] avatar/seph component missing on netId objects.");
                return;
            }

            // 可选：简单校验，避免代点
            try
            {
                if (conn != null && conn.identity != null && conn.identity != avatarId)
                {
                    Log.LogWarning("[Net] Reject pick: connection identity mismatch.");
                    return;
                }
            }
            catch { }

            DoServerPick(avatar, seph, msg.itemId, msg.finishNow, null);
        }
        catch (Exception ex)
        {
            Log.LogError("[Net] OnPickRewardMsg failed: " + ex);
        }
    }

    // =========================
    // 1) 奖励池：强制混池（神器+石板）
    // =========================
    [HarmonyPatch(typeof(Sephirite), "GenerateItems")]
    [HarmonyPostfix]
    private static void Sephirite_GenerateItems_Postfix(Sephirite __instance, GameObject actor)
    {
        try
        {
            if (!NetworkServer.active) return;
            if (__instance == null) return;

            var rewards = GetRewardsList(__instance);
            if (rewards == null)
            {
                Log.LogWarning("[Patch] Cannot find rewards list. Fallback to original.");
                return;
            }

            int count = rewards.Count;
            if (count <= 0) return;

            List<int> pool = BuildMixedPoolIds();
            if (pool == null || pool.Count == 0) return;

            int seed = 0;
            try { seed = __instance.CurrentSeed; } catch { }
            System.Random rand = new System.Random(seed);

            HashSet<int> used = new HashSet<int>();
            rewards.Clear();

            for (int i = 0; i < count; i++)
            {
                int pickId = PickUnique(pool, rand, used);
                if (pickId <= 0) break;

                int instanceId = ItemDatabase.GenerateInstanceID(rand);
                rewards.Add(new SephiriteRewardMetadata(instanceId, pickId));
            }

            string typeStr = "";
            try { typeStr = __instance.type.ToString(); } catch { typeStr = "UNKNOWN"; }

            Log.LogInfo(string.Format("[Patch] Generated full mixed-pool rewards: {0}/{0}, type={1}, seed={2}", count, typeStr, seed));
        }
        catch (Exception ex)
        {
            Log.LogError("[Patch] Sephirite.GenerateItems postfix failed: " + ex);
        }
    }

    private static List<int> BuildMixedPoolIds()
    {
        List<int> ids = new List<int>();
        try
        {
            foreach (int id in ItemDatabase.GetAllItemID())
            {
                ItemEntity ent = ItemDatabase.FindItemById(id);
                if (!ent) continue;

                if (ent.rarity == EItemRarity.Eternal) continue;
                if (ent.type != EItemType.Charm && ent.type != EItemType.StoneTablet) continue;
                if (ent.activeType == EItemActiveType.Hidden || ent.activeType == EItemActiveType.Disabled) continue;

                if (ent.activeType == EItemActiveType.TestOnly)
                {
                    if (!(ScreenFader.Instance && ScreenFader.Instance.IsTestMode)) continue;
                }

                ids.Add(id);
            }
        }
        catch (Exception ex)
        {
            Log.LogError("BuildMixedPoolIds failed: " + ex);
        }
        return ids;
    }

    private static int PickUnique(List<int> pool, System.Random rand, HashSet<int> used)
    {
        for (int i = 0; i < 200; i++)
        {
            int id = pool[rand.Next(pool.Count)];
            if (used.Add(id)) return id;
        }
        for (int i = 0; i < pool.Count; i++)
        {
            int id = pool[i];
            if (used.Add(id)) return id;
        }
        return -1;
    }

    private static IList<SephiriteRewardMetadata> GetRewardsList(Sephirite seph)
    {
        try
        {
            FieldInfo fi = AccessTools.Field(seph.GetType(), "rewards");
            if (fi != null)
            {
                object obj = fi.GetValue(seph);
                return obj as IList<SephiriteRewardMetadata>;
            }
            return null;
        }
        catch { return null; }
    }

    // =========================
    // 2) 点击 Skip/Convert -> 打开图鉴；图鉴左键点物品 -> 领取（Pick3：可选3次）
    // =========================

    [HarmonyPatch(typeof(UI_SephiriteRewardPanel), "SkipSelect")]
    [HarmonyPrefix]
    private static bool UI_SephiriteRewardPanel_SkipSelect_Prefix(UI_SephiriteRewardPanel __instance)
    {
        return InterceptSkipLikeButton(__instance, false);
    }

    [HarmonyPatch(typeof(UI_SephiriteRewardPanel), "ConvertRerollDice")]
    [HarmonyPrefix]
    private static bool UI_SephiriteRewardPanel_ConvertRerollDice_Prefix(UI_SephiriteRewardPanel __instance)
    {
        return InterceptSkipLikeButton(__instance, true);
    }

    private static bool InterceptSkipLikeButton(UI_SephiriteRewardPanel panel, bool isConvertButton)
    {
        try
        {
            if (panel == null) return true;

            PlayerAvatar avatar = GetPrivateField<PlayerAvatar>(panel, "openedAvatar");
            Sephirite seph = GetPrivateField<Sephirite>(panel, "sephirite");

            if (avatar == null || seph == null) return true;

            EnsurePickRewardMsgSerialization();
            if (NetworkServer.active && !_serverHandlerRegistered)
            {
                RegisterServerHandler();
                _serverHandlerRegistered = true;
                Log.LogInfo("[Net] Server handler registered (on open picker).");
            }

            string msg = "Open full item picker?";
            try
            {
                msg = isConvertButton ? panel.convertRerollDicePopupString.ToString()
                                      : panel.skipSelectPopupString.ToString();
            }
            catch { }

            UIManager.Instance.GetElement<UI_MessageBoxHolder>().OpenYesNo(
                msg,
                delegate { PickerState.Begin(avatar, seph, panel, PicksPerReward); },
                null,
                false
            );

            return false;
        }
        catch (Exception ex)
        {
            Log.LogError("InterceptSkipLikeButton failed: " + ex);
            return true;
        }
    }

    private static T GetPrivateField<T>(object obj, string fieldName) where T : class
    {
        try
        {
            FieldInfo fi = AccessTools.Field(obj.GetType(), fieldName);
            if (fi == null) return null;
            return fi.GetValue(obj) as T;
        }
        catch { return null; }
    }

    private static class PickerState
    {
        public static bool Active;
        public static PlayerAvatar Avatar;
        public static Sephirite Seph;
        public static UI_SephiriteRewardPanel RewardPanel;
        public static bool PickingNow;
        public static int PicksLeft;

        public static void Begin(PlayerAvatar avatar, Sephirite seph, UI_SephiriteRewardPanel rewardPanel, int picks)
        {
            try
            {
                Avatar = avatar;
                Seph = seph;
                RewardPanel = rewardPanel;
                Active = true;
                PickingNow = false;
                PicksLeft = picks;

                Log.LogInfo(string.Format("[Picker] Begin. PicksLeft={0}. Closing reward panel then open journal...", PicksLeft));

                // 为了避免某些情况下 reward panel 抢输入焦点：先关奖励面板
                try { if (RewardPanel != null) RewardPanel.Close(); } catch { }

                UI_JournalPanel journal = UIManager.Instance.GetElement<UI_JournalPanel>();
                if (journal != null)
                {
                    Log.LogInfo("[Picker] Opening journal.");
                    journal.Open();
                    try { journal.SelectTab(0); } catch { }
                }
                else
                {
                    Log.LogWarning("[Picker] UI_JournalPanel not found (UIManager).");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("[Picker] Begin failed: " + ex);
                Cancel();
            }
        }

        public static void Cancel()
        {
            Active = false;
            PickingNow = false;
            PicksLeft = 0;
            Avatar = null;
            Seph = null;
            RewardPanel = null;
            Log.LogInfo("[Picker] Cancel.");
        }

        public static void Pick(int itemId)
        {
            if (!Active) return;
            if (PickingNow) return;
            if (PicksLeft <= 0) { Cancel(); return; }

            PickingNow = true;

            try
            {
                bool finishNow = (PicksLeft <= 1);
                Log.LogInfo(string.Format("[Picker] Pick itemId={0}, finishNow={1}, left(before)={2}.", itemId, finishNow, PicksLeft));

                if (Avatar == null || Seph == null)
                {
                    Log.LogWarning("[Picker] Avatar/Seph is null.");
                    Cancel();
                    return;
                }

                // ======= 房主/服务器：直接执行 =======
                if (NetworkServer.active || Avatar.isServer)
                {
                    DoServerPick(Avatar, Seph, itemId, finishNow, RewardPanel);

                    PicksLeft--;

                    if (finishNow)
                    {
                        try
                        {
                            UI_JournalPanel journal = UIManager.Instance.GetElement<UI_JournalPanel>();
                            if (journal != null) journal.Close();
                        }
                        catch { }

                        Cancel();
                    }
                    return;
                }

                // ======= 非房主客户端：发消息给服务器 =======
                if (NetworkClient.active && NetworkClient.ready)
                {
                    EnsurePickRewardMsgSerialization();

                    uint aId = GetNetIdSafe(Avatar);
                    uint sId = GetNetIdSafe(Seph);
                    if (aId == 0 || sId == 0)
                    {
                        Log.LogWarning(string.Format("[Picker] Cannot send pick msg, netId invalid. avatar={0}, seph={1}", aId, sId));
                        Cancel();
                        return;
                    }

                    PickRewardMsg msg = new PickRewardMsg
                    {
                        avatarNetId = aId,
                        sephNetId = sId,
                        itemId = itemId,
                        finishNow = finishNow
                    };

                    NetworkClient.Send(msg);
                    Log.LogInfo(string.Format("[Net] Sent PickRewardMsg itemId={0} finishNow={1} avatarNetId={2} sephNetId={3}", itemId, finishNow, aId, sId));

                    PicksLeft--;

                    if (finishNow)
                    {
                        try
                        {
                            UI_JournalPanel journal = UIManager.Instance.GetElement<UI_JournalPanel>();
                            if (journal != null) journal.Close();
                        }
                        catch { }

                        Cancel();
                    }
                    return;
                }

                Log.LogWarning("[Picker] Client not ready / not active, cannot pick in multiplayer.");
                Cancel();
            }
            catch (Exception ex)
            {
                Log.LogError("[Picker] Pick failed: " + ex);
                Cancel();
            }
            finally
            {
                PickingNow = false;
            }
        }
    }

    private static void DoServerPick(PlayerAvatar avatar, Sephirite seph, int itemId, bool finishNow, UI_SephiriteRewardPanel rewardPanelMaybeLocal)
    {
        try
        {
            if (!NetworkServer.active && !(avatar != null && avatar.isServer))
            {
                Log.LogWarning("[Picker] DoServerPick called but not server.");
                return;
            }

            if (!finishNow)
            {
                bool ok = GrantItemDirectToInventory(avatar, seph, itemId);
                Log.LogInfo(string.Format("[Picker] Server extra grant itemId={0}, ok={1}.", itemId, ok));
                return;
            }

            IList<SephiriteRewardMetadata> rewards = GetRewardsList(seph);
            if (rewards == null)
            {
                Log.LogError("[Picker] Cannot access seph.rewards list (null).");
                return;
            }

            int seed = 0;
            try { seed = seph.CurrentSeed; } catch { }
            System.Random rand = new System.Random(unchecked(Environment.TickCount ^ itemId ^ seed));
            int instanceId = ItemDatabase.GenerateInstanceID(rand);

            rewards.Clear();
            rewards.Add(new SephiriteRewardMetadata(instanceId, itemId));

            try
            {
                if (rewardPanelMaybeLocal != null)
                {
                    CallPrivateVoid(rewardPanelMaybeLocal, "ClearSephiriteCallback");
                    CallPrivateVoid(rewardPanelMaybeLocal, "ClearRewardIcon");
                }
            }
            catch { }

            avatar.SelectSephiriteReward(seph, 0, new ItemPosition(-1, -1));
            Log.LogInfo(string.Format("[Picker] Server final granted itemId={0} (instanceId={1}).", itemId, instanceId));
        }
        catch (Exception ex)
        {
            Log.LogError("[Picker] DoServerPick failed: " + ex);
        }
    }

    private static bool GrantItemDirectToInventory(PlayerAvatar avatar, Sephirite seph, int itemId)
    {
        try
        {
            if (avatar == null) return false;

            int seed = 0;
            try { if (seph != null) seed = seph.CurrentSeed; } catch { }
            System.Random rand = new System.Random(unchecked(Environment.TickCount ^ itemId ^ seed));
            int instanceId = ItemDatabase.GenerateInstanceID(rand);

            ItemMetadata meta = new ItemMetadata(instanceId, itemId, 1);

            object invObj = null;
            try { invObj = avatar.Inventory; } catch { invObj = null; }

            if (invObj == null)
            {
                Log.LogWarning("[Picker] avatar.Inventory is null, cannot grant direct.");
                return false;
            }

            bool invoked = TryInvokeAddItem(invObj, meta);
            if (!invoked)
            {
                Log.LogWarning("[Picker] No compatible Inventory.AddItem overload found (direct grant may fail).");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.LogError("[Picker] GrantItemDirectToInventory failed: " + ex);
            return false;
        }
    }

    private static bool TryInvokeAddItem(object invObj, ItemMetadata meta)
    {
        try
        {
            MethodInfo[] ms = invObj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "AddItem")
                .ToArray();

            foreach (MethodInfo m in ms)
            {
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length < 1) continue;
                if (ps[0].ParameterType != typeof(ItemMetadata)) continue;

                object[] args = new object[ps.Length];
                args[0] = meta;

                bool ok = true;

                for (int i = 1; i < ps.Length; i++)
                {
                    Type t = ps[i].ParameterType;

                    if (t == typeof(int)) args[i] = 0;
                    else if (t == typeof(ItemPosition)) args[i] = new ItemPosition(-1, -1);
                    else if (t == typeof(bool)) args[i] = true;
                    else if (t.IsEnum) args[i] = Activator.CreateInstance(t);
                    else if (!t.IsValueType) args[i] = null;
                    else { ok = false; break; }
                }

                if (!ok) continue;

                m.Invoke(invObj, args);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning("[Picker] TryInvokeAddItem error: " + ex.Message);
        }

        return false;
    }

    private static void CallPrivateVoid(object obj, string methodName)
    {
        MethodInfo mi = AccessTools.Method(obj.GetType(), methodName);
        if (mi != null) mi.Invoke(obj, null);
    }

    private static uint GetNetIdSafe(object obj)
    {
        try
        {
            NetworkBehaviour nb = obj as NetworkBehaviour;
            if (nb != null) return nb.netId;

            Component c = obj as Component;
            if (c != null)
            {
                NetworkIdentity id = c.GetComponent<NetworkIdentity>();
                if (id != null) return id.netId;
            }
        }
        catch { }
        return 0;
    }

    [HarmonyPatch(typeof(UI_JournalPanel), "OnClosed")]
    [HarmonyPostfix]
    private static void UI_JournalPanel_OnClosed_Postfix()
    {
        if (PickerState.Active && !PickerState.PickingNow)
        {
            Log.LogInfo("[Picker] Journal closed, cancel picker.");
            PickerState.Cancel();
        }
    }

    // =========================
    // 3) 给“图鉴物品图标左键”挂钩（兼容：UI_ItemIcon / NewInventoryItemIcon）
    // =========================
    private static void TryPatchJournalIconClicks(Harmony harmony)
    {
        string[] typeNames = new string[] { "UI_ItemIcon", "NewInventoryItemIcon" };

        foreach (string tn in typeNames)
        {
            try
            {
                Type t = AccessTools.TypeByName(tn);
                if (t == null) continue;

                MethodInfo[] methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                        .Where(m => m.Name == "OnItemClicked")
                                        .ToArray();

                if (methods.Length == 0)
                {
                    methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               .Where(m => m.Name == "OnPointerClick")
                               .ToArray();
                }

                foreach (MethodInfo m in methods)
                {
                    HarmonyMethod prefix = new HarmonyMethod(typeof(FullItemPoolRewardsPlugin).GetMethod(
                        "JournalIconClick_Prefix",
                        BindingFlags.Static | BindingFlags.NonPublic));

                    harmony.Patch(m, prefix: prefix);

                    string sig = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name).ToArray());
                    Log.LogInfo(string.Format("[Picker] Patched {0}.{1}({2})", tn, m.Name, sig));
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("TryPatchJournalIconClicks failed for " + tn + ": " + ex.Message);
            }
        }
    }

    private static bool JournalIconClick_Prefix(object __instance, object __0)
    {
        try
        {
            if (!PickerState.Active) return true;

            if (!IsLeftClick(__0)) return true;

            if (!TryGetItemId(__instance, out int itemId)) return true;

            PickerState.Pick(itemId);
            return false;
        }
        catch (Exception ex)
        {
            Log.LogError("[Picker] JournalIconClick_Prefix error: " + ex);
            return true;
        }
    }

    private static bool IsLeftClick(object arg)
    {
        if (arg == null) return true;

        PointerEventData ped = arg as PointerEventData;
        if (ped != null) return ped.button == PointerEventData.InputButton.Left;

        if (arg.GetType().IsEnum && arg.GetType().Name.Contains("InputButton"))
            return arg.ToString() == "Left";

        return true;
    }

    private static bool TryGetItemId(object iconInstance, out int itemId)
    {
        itemId = -1;
        if (iconInstance == null) return false;

        try
        {
            object itemObj = null;

            PropertyInfo pi = AccessTools.Property(iconInstance.GetType(), "Item");
            if (pi != null) itemObj = pi.GetValue(iconInstance, null);

            if (itemObj == null)
            {
                FieldInfo fi = AccessTools.Field(iconInstance.GetType(), "Item");
                if (fi != null) itemObj = fi.GetValue(iconInstance);
            }

            if (itemObj == null)
            {
                FieldInfo fi2 = AccessTools.Field(iconInstance.GetType(), "item");
                if (fi2 != null) itemObj = fi2.GetValue(iconInstance);
            }

            if (itemObj == null) return false;

            FieldInfo entField = AccessTools.Field(itemObj.GetType(), "entityID");
            if (entField != null)
            {
                itemId = (int)entField.GetValue(itemObj);
                return itemId > 0;
            }

            PropertyInfo entProp = AccessTools.Property(itemObj.GetType(), "entityID");
            if (entProp != null)
            {
                itemId = (int)entProp.GetValue(itemObj, null);
                return itemId > 0;
            }

            FieldInfo idField = AccessTools.Field(itemObj.GetType(), "id");
            if (idField != null)
            {
                itemId = (int)idField.GetValue(itemObj);
                return itemId > 0;
            }

            return false;
        }
        catch { return false; }
    }

    // =========================
    // 4) Mirror 自定义消息序列化注册（修复：兼容 Extensions）
    // =========================
    private static void EnsurePickRewardMsgSerialization()
    {
        if (_serializationRegistered) return;

        try
        {
            Type writerDef = AccessTools.TypeByName("Mirror.Writer`1");
            Type readerDef = AccessTools.TypeByName("Mirror.Reader`1");

            if (writerDef == null || readerDef == null)
            {
                Log.LogWarning("[Net] Mirror.Writer`1 / Reader`1 not found. Serialization may rely on game weaver.");
                _serializationRegistered = true;
                return;
            }

            Type writerT = writerDef.MakeGenericType(typeof(PickRewardMsg));
            Type readerT = readerDef.MakeGenericType(typeof(PickRewardMsg));

            FieldInfo writeField = AccessTools.Field(writerT, "write");
            FieldInfo readField = AccessTools.Field(readerT, "read");

            if (writeField == null || readField == null)
            {
                Log.LogWarning("[Net] Writer/Reader fields not found. Serialization may rely on game weaver.");
                _serializationRegistered = true;
                return;
            }

            Action<NetworkWriter, PickRewardMsg> w = WritePickRewardMsg;
            Func<NetworkReader, PickRewardMsg> r = ReadPickRewardMsg;

            writeField.SetValue(null, w);
            readField.SetValue(null, r);

            _serializationRegistered = true;
            Log.LogInfo("[Net] Serialization registered for PickRewardMsg.");
        }
        catch (Exception ex)
        {
            Log.LogWarning("[Net] EnsurePickRewardMsgSerialization failed: " + ex.Message);
            _serializationRegistered = true;
        }
    }

    // —— 修复关键：同时支持 instance 方法 和 Extensions(static) 扩展方法 ——
    private static MethodInfo _miWriteUInt, _miWriteInt, _miWriteBool;
    private static MethodInfo _miReadUInt, _miReadInt, _miReadBool;

    private static Type _writerExtType;
    private static Type _readerExtType;

    private static void WritePickRewardMsg(NetworkWriter writer, PickRewardMsg msg)
    {
        EnsureRWMethods();

        InvokeWrite(_miWriteUInt, writer, msg.avatarNetId);
        InvokeWrite(_miWriteUInt, writer, msg.sephNetId);
        InvokeWrite(_miWriteInt, writer, msg.itemId);
        InvokeWrite(_miWriteBool, writer, msg.finishNow);
    }

    private static PickRewardMsg ReadPickRewardMsg(NetworkReader reader)
    {
        EnsureRWMethods();

        PickRewardMsg msg = new PickRewardMsg();
        msg.avatarNetId = (uint)InvokeRead(_miReadUInt, reader);
        msg.sephNetId = (uint)InvokeRead(_miReadUInt, reader);
        msg.itemId = (int)InvokeRead(_miReadInt, reader);
        msg.finishNow = (bool)InvokeRead(_miReadBool, reader);
        return msg;
    }

    private static void EnsureRWMethods()
    {
        if (_writerExtType == null) _writerExtType = AccessTools.TypeByName("Mirror.NetworkWriterExtensions");
        if (_readerExtType == null) _readerExtType = AccessTools.TypeByName("Mirror.NetworkReaderExtensions");

        if (_miWriteUInt == null)
            _miWriteUInt = FindWriteMethod(typeof(uint), "WriteUInt", "WriteUInt32", "WritePackedUInt32");

        if (_miWriteInt == null)
            _miWriteInt = FindWriteMethod(typeof(int), "WriteInt", "WriteInt32", "WritePackedInt32");

        if (_miWriteBool == null)
            _miWriteBool = FindWriteMethod(typeof(bool), "WriteBool", "WriteBoolean");

        if (_miReadUInt == null)
            _miReadUInt = FindReadMethod(typeof(uint), "ReadUInt", "ReadUInt32", "ReadPackedUInt32");

        if (_miReadInt == null)
            _miReadInt = FindReadMethod(typeof(int), "ReadInt", "ReadInt32", "ReadPackedInt32");

        if (_miReadBool == null)
            _miReadBool = FindReadMethod(typeof(bool), "ReadBool", "ReadBoolean");
    }

    private static MethodInfo FindWriteMethod(Type valueType, params string[] names)
    {
        // 1) 先找 NetworkWriter 实例方法：void WriteX(T)
        foreach (string n in names)
        {
            MethodInfo mi = typeof(NetworkWriter).GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new Type[] { valueType }, null);
            if (mi != null) return mi;
        }

        // 2) 再找 Extensions：static void WriteX(this NetworkWriter, T)
        if (_writerExtType != null)
        {
            foreach (string n in names)
            {
                MethodInfo mi = _writerExtType.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new Type[] { typeof(NetworkWriter), valueType }, null);
                if (mi != null) return mi;
            }

            // 兜底：找任意 static void *(NetworkWriter, valueType)
            foreach (MethodInfo m in _writerExtType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;
                if (ps[0].ParameterType != typeof(NetworkWriter)) continue;
                if (ps[1].ParameterType != valueType) continue;
                if (m.ReturnType != typeof(void)) continue;
                return m;
            }
        }

        return null;
    }

    private static MethodInfo FindReadMethod(Type retType, params string[] names)
    {
        // 1) NetworkReader 实例方法：T ReadX()
        foreach (string n in names)
        {
            MethodInfo mi = typeof(NetworkReader).GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (mi != null && mi.ReturnType == retType) return mi;
        }

        // 2) Extensions：static T ReadX(this NetworkReader)
        if (_readerExtType != null)
        {
            foreach (string n in names)
            {
                MethodInfo mi = _readerExtType.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new Type[] { typeof(NetworkReader) }, null);
                if (mi != null && mi.ReturnType == retType) return mi;
            }

            // 兜底：找任意 static retType *(NetworkReader)
            foreach (MethodInfo m in _readerExtType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (ps[0].ParameterType != typeof(NetworkReader)) continue;
                if (m.ReturnType != retType) continue;
                return m;
            }
        }

        return null;
    }

    private static void InvokeWrite(MethodInfo mi, NetworkWriter writer, object value)
    {
        if (mi == null) throw new MissingMethodException("Mirror NetworkWriter write method not found.");

        // instance: writer.WriteX(value)
        if (!mi.IsStatic)
        {
            mi.Invoke(writer, new object[] { value });
            return;
        }

        // extension(static): NetworkWriterExtensions.WriteX(writer, value)
        mi.Invoke(null, new object[] { writer, value });
    }

    private static object InvokeRead(MethodInfo mi, NetworkReader reader)
    {
        if (mi == null) throw new MissingMethodException("Mirror NetworkReader read method not found.");

        // instance: reader.ReadX()
        if (!mi.IsStatic)
            return mi.Invoke(reader, null);

        // extension(static): NetworkReaderExtensions.ReadX(reader)
        return mi.Invoke(null, new object[] { reader });
    }
}
