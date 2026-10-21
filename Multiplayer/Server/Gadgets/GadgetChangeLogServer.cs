using DV.Customization;
using DV.Customization.Gadgets;
using HarmonyLib;
using LiteNetLib;
using Multiplayer.Core.Gadgets;
using Multiplayer.Server.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Gadgets;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Server.Gadgets;

/// <summary>
/// The server's gadget change log (gadgets.md, "The change log"). It observes the native primitives
/// rather than announcing requests: a gadget linked to a car is <c>Installed</c>, one unlinked is
/// <c>Removed</c>, whatever caused it (a player's request, the host's player, a deleted car, broken
/// glass, a collision). Only top-level changes are recorded (GadgetChangeRecorder); a removal's
/// consequences are reproduced in every game by the same native call. At the end of each server tick
/// every car's new entries go to every player past <c>ReadyForCustomizers</c>, after the Creates of
/// items they don't know; an entry's payload is read then, so an <c>Installed</c> carries what
/// <c>Place</c> set after linking (placement data, mounting).
/// </summary>
[HarmonyPatch]
internal static class GadgetChangeLogServer
{
    private static readonly GadgetChangeRecorder<GadgetBase> recorder = new();
    private static bool forcing;

    internal static uint Revision(ushort carNetId) => recorder.Revision(carNetId);

    internal static IEnumerable<ushort> CarsWithRevisions() =>
        NetworkedTrainCar.GetAllRegistered().Select(car => car.NetId).Where(netId => recorder.Revision(netId) > 0).ToList();

    /// <summary>The changes a player's request makes answer it: they carry its operation.</summary>
    internal static void BeginOperation(ServerPlayer player, uint operationId) =>
        recorder.BeginOperation(player.PlayerId, operationId);

    internal static void EndOperation() => recorder.EndOperation();

    internal static void Reset() => recorder.Clear();

    private static bool Recording => NetworkRole.IsServer && NetworkServer.Instance is { WorldLoaded: true };

    private static bool OnCar(Customization custom, out ushort carNetId)
    {
        carNetId = 0;
        return custom is TrainCarCustomization { TrainCar: { } car } && NetworkedTrainCar.TryGetNetId(car, out carNetId);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Customization.CustomizerBase), nameof(Customization.CustomizerBase.Link))]
    private static void LinkStart(Customization.CustomizerBase __instance, out bool __state) =>
        __state = __instance is GadgetBase && Recording && recorder.Enter();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Customization.CustomizerBase), nameof(Customization.CustomizerBase.Link))]
    private static void Linked(Customization.CustomizerBase __instance, bool __state)
    {
        if (__state && __instance is GadgetBase gadget && gadget.IsLinked && OnCar(gadget.Custom, out ushort carNetId))
            recorder.Record(carNetId, GadgetChangeKind.Installed, gadget);
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Customization.CustomizerBase), nameof(Customization.CustomizerBase.Link))]
    private static void LinkEnd(Customization.CustomizerBase __instance)
    {
        if (__instance is GadgetBase && Recording)
            recorder.Exit();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Customization.CustomizerBase), nameof(Customization.CustomizerBase.Unlink))]
    private static void Unlinking(Customization.CustomizerBase __instance)
    {
        if (__instance is not GadgetBase gadget || !Recording)
            return;
        bool top = recorder.Enter();
        if (top && gadget.IsLinked && OnCar(gadget.Custom, out ushort carNetId))
            recorder.Record(carNetId, GadgetChangeKind.Removed, gadget, forcing);
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Customization.CustomizerBase), nameof(Customization.CustomizerBase.Unlink))]
    private static void UnlinkEnd(Customization.CustomizerBase __instance)
    {
        if (__instance is GadgetBase && Recording)
            recorder.Exit();
    }

    // Code took it off (a deleted car, broken glass, a collision, a telegrab), not a player's tool.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetBase), nameof(GadgetBase.ForceRemove))]
    private static void ForceStart(out bool __state)
    {
        __state = forcing;
        forcing = true;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GadgetBase), nameof(GadgetBase.ForceRemove))]
    private static void ForceEnd(bool __state) => forcing = __state;

    /// <summary>Sends what was recorded since the last flush: every server tick, and at once when a car
    /// is about to go, so its removals arrive before it does.</summary>
    internal static void Flush(NetworkServer server)
    {
        List<GadgetChange<GadgetBase>> changes = recorder.Flush();
        if (changes.Count == 0)
            return;
        List<ServerPlayer> players = server.ServerPlayers
            .Where(player => player.Peer != server.SelfPeer && player.LoadingState >= PlayerLoadingState.ReadyForCustomizers)
            .ToList();
        foreach (IGrouping<ushort, GadgetChange<GadgetBase>> car in changes.GroupBy(change => change.Anchor))
        {
            List<GadgetChangeData> entries = car.Select(Describe).ToList();
            foreach (ServerPlayer player in players)
            {
                List<ItemUpdateData> creates = new();
                foreach (GadgetChange<GadgetBase> change in car.Where(c => c.Kind == GadgetChangeKind.Installed))
                    if (ItemOf(change.Gadget) is { } item && AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record) &&
                        GadgetJoinSnapshot.CreateFor(player, record, item, car.Key) is { } create)
                        creates.Add(create);
                if (creates.Count > 0)
                    server.SendItemsBulkUpdatePacket(creates, player);
                server.SendGadgetChanges(new ClientboundGadgetChangesPacket { CarNetId = car.Key, Entries = entries.ToArray() }, player);
            }
        }
    }

    /// <summary>A car that's gone: forget its revision (its entries were flushed first).</summary>
    internal static void Forget(ushort carNetId) => recorder.Forget(carNetId);

    private static GadgetChangeData Describe(GadgetChange<GadgetBase> change)
    {
        NetworkedItem item = ItemOf(change.Gadget);
        GadgetChangeData data = new()
        {
            Revision = change.Revision,
            Kind = (byte)change.Kind,
            ItemNetId = item?.NetId ?? 0,
            Uid = change.Gadget != null ? change.Gadget.UID : 0,
            Forced = change.Forced,
            PlayerId = change.PlayerId,
            OperationId = change.OperationId
        };
        // Installed and taken off again in the same tick: the entry keeps its revision and does nothing.
        if (change.Kind == GadgetChangeKind.Installed && item != null && change.Gadget != null && change.Gadget.IsLinked &&
            GadgetJoinSnapshot.Describe(item, change.Gadget) is { } installed)
            data.State = installed.State;
        return data;
    }

    private static NetworkedItem ItemOf(GadgetBase gadget) =>
        gadget != null && gadget.GadgetItem != null ? gadget.GadgetItem.GetComponent<NetworkedItem>() : null;
}
