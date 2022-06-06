using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace ModFrame
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class NewMod : BaseUnityPlugin
    {
        private const string ModName = "New Mod";
        private const string ModVersion = "1.0";
        private const string ModGUID = "some.new.guid";
        private static Harmony harmony = null!;
        public static long _logIntevervalSeconds { get; set; }
        private static ConfigEntry<string> _portalPrefabName;
        private static ConfigEntry<string> _onewayPortalTagPrefix;
        private static ConfigEntry<float> _connectPortalCoroutineWait;
        
        ConfigSync configSync = new(ModGUID) 
            { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion};
        internal static ConfigEntry<bool> ServerConfigLocked = null!;
        ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
        {
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

            SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }
        ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);
        public void Awake()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            harmony = new(ModGUID);
            harmony.PatchAll(assembly);
            ServerConfigLocked = config("1 - General", "Lock Configuration", true, "If on, the configuration is locked and can be changed by server admins only.");
            _portalPrefabName = config("Portals", "portalPrefabName", "Stone_Portal", new ConfigDescription("Alternative portal prefab name to search for."));
            _onewayPortalTagPrefix = config("Portals", "onewayPortalTagPrefix", ">>>", new ConfigDescription("Prefix for specifying a one-way portal."));
            _connectPortalCoroutineWait = config("Portals", "connectPortalCoroutineWait", 4f, new ConfigDescription("Wait time (seconds) when ConnectPortal coroutine yields."));
            configSync.AddLockingConfigEntry(ServerConfigLocked);
        }


        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Awake))]
        public static class TeleportWorldPrefix
        {
            public static void Prefix(TeleportWorld __instance)
            {
                __instance.m_nview = __instance.GetComponent<ZNetView>();

                if (__instance.m_nview.GetZDO() == null)
                {
                    __instance.enabled = false;
                    return;
                }

                __instance.m_hadTarget = __instance.HaveTarget();

                if (!__instance.m_proximityRoot)
                {
                    __instance.m_proximityRoot = __instance.transform;
                }

                if (__instance.m_target_found == null)
                {
                    GameObject targetFoundObject = __instance.gameObject.transform.Find("_target_found").gameObject;

                    targetFoundObject.SetActive(false);
                    __instance.m_target_found = targetFoundObject.AddComponent<EffectFade>();
                    targetFoundObject.SetActive(true);
                }

                __instance.m_nview.Register<string>("SetTag", new Action<long, string>(__instance.RPC_SetTag));
                __instance.InvokeRepeating("UpdatePortal", 0.5f, 0.5f);
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.ConnectPortals))]
        public static class ConnectPortalPrefix
        {
            public static void Prefix(Game __instance)
            {
                ZDOMan zdoMan = ZDOMan.instance;
                long logTimestamp;
                long lastLogTimestamp = 0;

                IEnumerator ConnectPorts()
                {
                    while (true)
                    {
                        logTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
                        bool shouldlog = (logTimestamp - lastLogTimestamp) > _logIntevervalSeconds;

                        __instance.m_tempPortalList.Clear();

                        int index = 0;
                        bool getPrefabsComplete = false;

                        HashSet<int> prefabHashCodes = new HashSet<int>
                        {
                            __instance.m_portalPrefab.name.GetStableHashCode(), _portalPrefabName.Value.GetStableHashCode()
                        };

                        do
                        {
                            getPrefabsComplete = GetAllZdosMatchingPrefabHashcodes(zdoMan, prefabHashCodes, __instance.m_tempPortalList, ref index);
                            yield return null;
                        } while (!getPrefabsComplete);

                        foreach (ZDO zdo in __instance.m_tempPortalList)
                        {
                            ZDOID targetZdoid = zdo.GetZDOID("target");
                            if (targetZdoid.IsNone())
                            {
                                continue;
                            }
                            string @tag = zdo.GetString("tag", string.Empty);
                            ZDO targetZdo = zdoMan.GetZDO(targetZdoid);
                            if (tag == String.Empty || targetZdo == null || (targetZdo.GetString("tag", String.Empty) != tag && !tag.StartsWith(_onewayPortalTagPrefix.Value)))
                            {
                                zdo.SetOwner(zdoMan.GetMyID());
                                zdo.Set("target", ZDOID.None);
                                zdoMan.ForceSendZDO(zdo.m_uid);
                            }
                        }

                        foreach (ZDO zdo in __instance.m_tempPortalList)
                        {
                            string @tag = zdo.GetString("tag", string.Empty);

                            if (tag == string.Empty || !zdo.GetZDOID("target").IsNone())
                            {
                                continue;
                            }

                            // If tag starts with oneway-prefix, look for matching portal that has tag without the prefix. 
                            bool isOneWayPortal = tag.StartsWith(_onewayPortalTagPrefix.Value);
                            ZDO targetZdo = __instance.FindRandomUnconnectedPortal(
                                __instance.m_tempPortalList, zdo, isOneWayPortal ? tag.Remove(0, _onewayPortalTagPrefix.Value.Length) : tag);

                            if (targetZdo != null)
                            {
                                zdo.SetOwner(zdoMan.GetMyID());
                                zdo.Set("target", targetZdo.m_uid);

                                // Only connect target if we are not a one-way portal.
                                targetZdo.SetOwner(zdoMan.GetMyID());
                                targetZdo.Set("target", isOneWayPortal ? ZDOID.None : zdo.m_uid);

                                zdoMan.ForceSendZDO(zdo.m_uid);
                                zdoMan.ForceSendZDO(targetZdo.m_uid);
                            }
                        }

                        yield return new WaitForSeconds(_connectPortalCoroutineWait.Value);

                    }

                }

                __instance.StartCoroutine(nameof(ConnectPorts));
            }
        }
        
        [HarmonyPatch(typeof(TeleportWorld), "Interact")]
        private class Interact_Patch
        { 
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions)
            {
                List<CodeInstruction> source = new List<CodeInstruction>(instructions);
                
                for (int index = 0; index < source.Count; ++index)
                {
                    if (source[index].opcode == OpCodes.Ldc_I4_S)
                        source[index].operand = (object)(int)sbyte.MaxValue;
                }
                
                return source.AsEnumerable<CodeInstruction>();
            }
        }

        private static bool GetAllZdosMatchingPrefabHashcodes(
            ZDOMan zdoMan, HashSet<int> prefabHashcodes, List<ZDO> matchingZdos, ref int index)
        {
            if (index >= zdoMan.m_objectsBySector.Length)
            {
                foreach (var outsideZdos in zdoMan.m_objectsByOutsideSector.Values)
                {
                    matchingZdos.AddRange(outsideZdos.Where(zdo => zdo.IsValid() && prefabHashcodes.Contains(zdo.GetPrefab())));
                }

                return true;
            }

            int counted = 0;

            while (index < zdoMan.m_objectsBySector.Length)
            {
                var sectorZdos = zdoMan.m_objectsBySector[index];

                if (sectorZdos != null)
                {
                    var zdos = sectorZdos.Where(zdo => prefabHashcodes.Contains(zdo.GetPrefab()));
                    matchingZdos.AddRange(zdos);
                    counted += zdos.Count();
                }

                index++;

                if (counted > 500)
                {
                    break;
                }
            }

            return false;
        }
    }
}
