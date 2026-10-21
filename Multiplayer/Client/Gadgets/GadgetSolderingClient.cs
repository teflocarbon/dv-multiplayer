using DV.Customization;
using DV.Customization.Gadgets;
using HarmonyLib;
using Multiplayer.Client.Session;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// Soldering on a remote client is predicted (gadgets.md, "Soldering"): the iron solders the gadget in
/// this game at once, as vanilla's does, so it powers up when it reaches full; the units it reaches go to
/// the server at 5 Hz and once more when the player stops, absolute. The server's <c>Soldered</c> entries
/// are the truth, except that while this player holds the iron on a gadget it shows the larger of its
/// own and the server's. Spending the spool (and swapping a spent one for an empty one, a new item) is
/// the server's and isn't done yet: the iron's count here runs down as vanilla's does.
/// </summary>
[HarmonyPatch]
internal static class GadgetSolderingClient
{
    private const float SendInterval = 0.2f;
    private const float StoppedAfter = 1f;

    private sealed class Soldering
    {
        public float LastTouched;
        public float LastSent = float.NegativeInfinity;
        public int SentUnits = -1;
    }

    private static readonly Dictionary<GadgetBase, Soldering> soldering = new();
    private static bool subscribed;
    private static uint nextOperation;

    internal static void Reset() => soldering.Clear();

    /// <summary>Whether this player is soldering the gadget (touched it within the last second).</summary>
    internal static bool Holding(GadgetBase gadget) =>
        soldering.TryGetValue(gadget, out Soldering entry) && Time.realtimeSinceStartup - entry.LastTouched < StoppedAfter;

    internal static int Units(TrainCarCustomization.TrainCarCustomizerBase gadget) => 65536 - gadget.SolderingUnitsRequired;

    // Vanilla's ProcessSoldering without the spool swap, which makes items.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetSolderingTool), "ProcessSoldering")]
    private static bool ProcessSoldering(GadgetSolderingTool __instance, GadgetBase target)
    {
        if (!NetworkRole.IsRemoteClient || GadgetApplyScope.Active || target == null || !GadgetJoinApplier.IsServerSent(target))
            return true;
        int units = (int)(65536f * __instance.solderingSpeed * Time.deltaTime);
        if (units > __instance.remainingUnits)
            units = __instance.remainingUnits;
        if (__instance.remainingUnits < 65536)
            target.MakeSoldered(units);
        else
            units = target.MakeSoldered(units);
        __instance.remainingUnits -= units;
        if (__instance.remainingUnits == 0)
            __instance.remainingUnits = -1;
        __instance.OnUnitsChanged();
        __instance.shouldParticlesBeOn = true;
        Touched(target);
        return false;
    }

    private static void Touched(GadgetBase gadget)
    {
        if (!soldering.TryGetValue(gadget, out Soldering entry))
            soldering[gadget] = entry = new Soldering();
        entry.LastTouched = Time.realtimeSinceStartup;
        if (!subscribed && NetworkLifecycle.Instance != null)
        {
            NetworkLifecycle.Instance.OnTick += Tick;
            subscribed = true;
        }
    }

    private static void Tick(uint _)
    {
        if (soldering.Count == 0)
            return;
        float now = Time.realtimeSinceStartup;
        foreach (KeyValuePair<GadgetBase, Soldering> pair in soldering.ToList())
        {
            GadgetBase gadget = pair.Key;
            Soldering entry = pair.Value;
            if (gadget == null || !gadget.IsLinked)
            {
                soldering.Remove(gadget);
                continue;
            }
            int units = Units(gadget);
            if (units != entry.SentUnits && now - entry.LastSent >= SendInterval)
            {
                ushort itemNetId = gadget.GadgetItem?.GetComponent<NetworkedItem>()?.NetId ?? 0;
                if (itemNetId != 0)
                    NetworkClient.Instance?.SendGadgetSolder(new ServerboundGadgetSolderPacket
                    {
                        OperationId = ++nextOperation,
                        ItemNetId = itemNetId,
                        Units = units
                    });
                entry.SentUnits = units;
                entry.LastSent = now;
            }
            // Stopped, and the last units have gone.
            if (now - entry.LastTouched > StoppedAfter && entry.SentUnits == units)
                soldering.Remove(gadget);
        }
    }
}
