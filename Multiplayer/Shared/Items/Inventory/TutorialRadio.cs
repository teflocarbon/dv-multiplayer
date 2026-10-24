using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV;
using DV.Tutorial.QT;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// The comms radio as the tutorials reach it in the inventory (docs/design/items/inventory.md,
/// "Stashed items that act"; D14, OI-COLD-11). Vanilla's quick tutorials (a host playing alone)
/// and the tutorial's Bolt units look the radio up among the inventory's objects; a stashed one is
/// a stand-in with no radio on it:
/// <list type="bullet">
/// <item>a quick tutorial's radio service (<c>ACommsRadioService</c>: rerail, delete, crew) that
/// starts while the radio is stashed binds to it once it's taken out, as it would have found it;</item>
/// <item>the tutorial's mode control (<c>CommsModeControlUnit</c>) takes a stashed radio out
/// first, and the modes it turns off are the radio's own state, kept as it's put away and given
/// back to its object when it's next made (vanilla keeps them on the stashed object).</item>
/// </list>
/// </summary>
[HarmonyPatch]
internal static class TutorialRadio
{
    private const string RadioPrefab = "CommsRadio";

    private sealed class Waiting
    {
        public ATutorialService Service;
        public QuickTutorialHost Host;
        public QuickTutorialPhase Phase;
    }

    private static readonly Dictionary<ushort, int[]> disabledModes = new();
    private static readonly List<Waiting> waiting = new();

    private static InventoryStandIn StashedRadio()
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        GameObject radio = inventory != null ? inventory.GetItemByName(RadioPrefab, false, false) : null;
        return radio != null && radio.TryGetComponent(out InventoryStandIn standIn) ? standIn : null;
    }

    /// <summary>A radio's modes the tutorial turned off, kept with its row as it's put away.</summary>
    public static void Keep(NetworkedItem item)
    {
        if (item == null || item.NetId == 0 || !item.TryGetComponent(out CommsRadioController radio))
            return;
        if (radio.disabledModeIndices.Count == 0)
            disabledModes.Remove(item.NetId);
        else
            disabledModes[item.NetId] = radio.disabledModeIndices.ToArray();
    }

    [HarmonyPatch(typeof(CommsRadioController), "Start")]
    [HarmonyPostfix]
    private static void AfterRadioStarts(CommsRadioController __instance)
    {
        if (__instance.TryGetComponent(out NetworkedItem item) && disabledModes.TryGetValue(item.NetId, out int[] kept))
        {
            disabledModes.Remove(item.NetId);
            foreach (int index in kept)
                __instance.disabledModeIndices.Add(index);
            if (__instance.disabledModeIndices.Contains(__instance.activeModeIndex) &&
                __instance.disabledModeIndices.Count < __instance.allModes.Count)
                __instance.SetNextMode();
        }
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        if (waiting.Count == 0 || inventory == null || !inventory.Contains(__instance.gameObject, false))
            return;
        Waiting[] services = waiting.ToArray();
        waiting.Clear();
        foreach (Waiting entry in services)
            entry.Service.StartService(entry.Host, entry.Phase);
    }

    // The quick tutorials' radio services: every concrete ACommsRadioService<T>.
    private static IEnumerable<Type> RadioServices() =>
        typeof(ATutorialService).Assembly.GetTypes().Where(type => !type.IsAbstract && IsRadioService(type));

    private static bool IsRadioService(Type type)
    {
        for (Type at = type.BaseType; at != null; at = at.BaseType)
            if (at.IsGenericType && at.GetGenericTypeDefinition() == typeof(ACommsRadioService<>))
                return true;
        return false;
    }

    private static CommsRadioController RadioOf(ATutorialService service) =>
        Traverse.Create(service).Property("CommsRadio").GetValue<CommsRadioController>();

    [HarmonyPatch]
    private static class ServiceStarts
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            RadioServices().Select(type => type.GetMethod(nameof(ATutorialService.StartService),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)).Where(method => method != null);

        private static void Postfix(ATutorialService __instance, QuickTutorialHost host, QuickTutorialPhase phase)
        {
            if (RadioOf(__instance) != null || StashedRadio() == null || waiting.Any(entry => entry.Service == __instance))
                return;
            waiting.Add(new Waiting { Service = __instance, Host = host, Phase = phase });
        }
    }

    [HarmonyPatch]
    private static class ServiceStops
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            RadioServices().Select(type => type.GetMethod(nameof(ATutorialService.StopService),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)).Where(method => method != null);

        private static void Prefix(ATutorialService __instance) =>
            waiting.RemoveAll(entry => entry.Service == __instance);
    }

    [HarmonyPatch]
    private static class ModeControl
    {
        // The unit's input is a lambda in Definition (taking the Bolt flow): the method the
        // compiler made of it. The unit is found by name, as the mod doesn't reference Bolt.
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(AccessTools.TypeByName("CommsModeControlUnit")).First(method =>
                method.Name.StartsWith("<Definition>") && method.GetParameters() is { Length: 1 } parameters &&
                parameters[0].ParameterType.Name == "Flow");

        private static void Prefix()
        {
            if (StashedRadio() is { } standIn && InventoryRowHolder.Current is { } holder)
                holder.TakeOut(standIn, "tutorial-comms-modes");
        }
    }
}
