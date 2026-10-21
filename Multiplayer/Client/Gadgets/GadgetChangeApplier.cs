using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Utils;
using Multiplayer.Client.Session;
using Multiplayer.Core.Gadgets;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// A remote client applying the server's gadget change log (gadgets.md, "Applying it on a client"): in
/// each car's revision order, in the apply scope. An <c>Installed</c> is vanilla's load of the item's
/// save data (as at join), a <c>Removed</c> the native removal, which reproduces its holes, unwiring and
/// unmounting. An entry waits while its car or item hasn't arrived; a gap in the revisions, an entry
/// that fails, or one that waited 10 s asks the server for the car's snapshot, which rebuilds it.
/// </summary>
internal static class GadgetChangeApplier
{
    private const float QueueTimeoutSeconds = 10f;

    private sealed class Anchor
    {
        // The car object the revision counts for: a NetId reused by a later car starts again.
        public TrainCar Car;
        public uint Applied;
        public readonly List<GadgetChangeData> Queue = new();
        public float WaitingSince = float.PositiveInfinity;
        public bool ResyncOutstanding;
    }

    private static readonly Dictionary<ushort, Anchor> anchors = new();
    private static bool subscribed;

    internal static void Reset() => anchors.Clear();

    /// <summary>The join snapshot has been placed: each car's changes count from its revision.</summary>
    internal static void Joined(GadgetAnchorSnapshotData[] snapshot)
    {
        foreach (GadgetAnchorSnapshotData data in snapshot ?? [])
        {
            Anchor anchor = AnchorOf(data.CarNetId);
            anchor.Applied = Math.Max(anchor.Applied, data.Revision);
        }
        Subscribe();
        foreach (ushort carNetId in anchors.Keys.ToList())
            Process(carNetId);
    }

    internal static void Receive(ClientboundGadgetChangesPacket packet)
    {
        Subscribe();
        Anchor anchor = AnchorOf(packet.CarNetId);
        foreach (GadgetChangeData entry in packet.Entries ?? [])
            anchor.Queue.Add(entry);
        if (anchor.Queue.Count > 0 && float.IsPositiveInfinity(anchor.WaitingSince))
            anchor.WaitingSince = Time.realtimeSinceStartup;
        Process(packet.CarNetId);
    }

    /// <summary>The server's answer to a resync: rebuild the car as the snapshot has it.</summary>
    internal static void Receive(ClientboundGadgetAnchorSnapshotPacket packet)
    {
        GadgetAnchorSnapshotData data = packet.Anchor;
        if (data == null || !NetworkedTrainCar.TryGet(data.CarNetId, out TrainCar car) || car == null)
            return;
        Anchor anchor = AnchorOf(data.CarNetId);
        Rebuild(car, data);
        anchor.Applied = data.Revision;
        anchor.ResyncOutstanding = false;
        anchor.Queue.RemoveAll(entry => entry.Revision <= data.Revision);
        anchor.WaitingSince = anchor.Queue.Count > 0 ? Time.realtimeSinceStartup : float.PositiveInfinity;
        Process(data.CarNetId);
    }

    /// <summary>
    /// The item code is about to move this item out of <c>Installed</c>, and its gadget is still on a car
    /// here: take it off natively first, so it doesn't matter whether the item update or the change log's
    /// <c>Removed</c> comes first.
    /// </summary>
    internal static void TakeOffBeforeItemUpdate(NetworkedItem item)
    {
        if (!NetworkRole.IsRemoteClient || item == null || item.GetComponent<GadgetItem>()?.Gadget is not { IsLinked: true } gadget)
            return;
        Multiplayer.LogDebug(() => $"Gadget {item.name} ({item.NetId}) leaves Installed before its removal arrived: taking it off");
        TakeOff(gadget, forced: false);
    }

    private static Anchor AnchorOf(ushort carNetId)
    {
        NetworkedTrainCar.TryGet(carNetId, out TrainCar car);
        if (!anchors.TryGetValue(carNetId, out Anchor anchor))
            anchors[carNetId] = anchor = new Anchor { Car = car };
        else if (car != null && anchor.Car != car)
        {
            // A NetId reused by a later car: its changes start again.
            anchor.Car = car;
            anchor.Applied = 0;
        }
        return anchor;
    }

    private static void Subscribe()
    {
        if (subscribed || NetworkLifecycle.Instance == null)
            return;
        NetworkLifecycle.Instance.OnTick += Tick;
        subscribed = true;
    }

    private static void Tick(uint _)
    {
        if (!NetworkRole.IsRemoteClient || anchors.Count == 0)
            return;
        foreach (KeyValuePair<ushort, Anchor> pair in anchors.ToList())
        {
            if (pair.Value.Queue.Count == 0)
                continue;
            Process(pair.Key);
            if (pair.Value.Queue.Count > 0 && Time.realtimeSinceStartup - pair.Value.WaitingSince > QueueTimeoutSeconds)
                RequestResync(pair.Key, pair.Value, "its changes waited 10 s");
        }
    }

    private static void Process(ushort carNetId)
    {
        // What arrives while the join snapshot is still being placed waits for it.
        if (GadgetJoinApplier.Applying || !anchors.TryGetValue(carNetId, out Anchor anchor) || anchor.ResyncOutstanding)
            return;
        anchor.Queue.Sort((a, b) => a.Revision.CompareTo(b.Revision));
        while (anchor.Queue.Count > 0)
        {
            GadgetChangeData entry = anchor.Queue[0];
            switch (GadgetRevisionOrder.Decide(anchor.Applied, entry.Revision))
            {
                case GadgetRevisionStep.Skip:
                    anchor.Queue.RemoveAt(0);
                    continue;
                case GadgetRevisionStep.Gap:
                    RequestResync(carNetId, anchor, $"revision {entry.Revision} after {anchor.Applied}");
                    return;
            }
            if (!NetworkedTrainCar.TryGet(carNetId, out TrainCar car) || car == null)
                return;
            ApplyResult result = Apply(car, entry);
            if (result == ApplyResult.Wait)
                return;
            anchor.Queue.RemoveAt(0);
            anchor.Applied = entry.Revision;
            anchor.WaitingSince = anchor.Queue.Count > 0 ? Time.realtimeSinceStartup : float.PositiveInfinity;
            if (result == ApplyResult.Failed)
            {
                RequestResync(carNetId, anchor, $"revision {entry.Revision} didn't apply");
                return;
            }
        }
    }

    private enum ApplyResult
    {
        Applied,
        Wait,
        Failed
    }

    private static ApplyResult Apply(TrainCar car, GadgetChangeData entry)
    {
        var kind = (GadgetChangeKind)entry.Kind;
        if (kind == GadgetChangeKind.Installed && string.IsNullOrEmpty(entry.State))
            return ApplyResult.Applied;   // installed and taken off again within one server tick
        if (kind is GadgetChangeKind.HoleAdded or GadgetChangeKind.HoleRemoved)
            return ApplyHole(car, entry);
        if (!NetworkedItem.TryGet(entry.ItemNetId, out NetworkedItem item) || item == null)
            return kind == GadgetChangeKind.Removed ? ApplyResult.Applied : ApplyResult.Wait;
        GadgetBase gadget = item.GetComponent<GadgetItem>()?.Gadget;
        try
        {
            if (kind == GadgetChangeKind.Installed)
            {
                if (gadget != null && gadget.IsLinked && gadget.UID == entry.Uid && gadget.TrainCar == car)
                    return ApplyResult.Applied;
                GadgetIntentPatches.Confirmed(entry);
                if (!Place(car, new InstalledGadgetData { ItemNetId = entry.ItemNetId, Uid = entry.Uid, State = entry.State }, out gadget))
                    return ApplyResult.Failed;
                GadgetBase placed = gadget;
                (placed.SoundOnPlaced != null ? placed.SoundOnPlaced : SingletonBehaviour<GadgetSystemUtility>.Instance.SoundOnGadgetPlaced)
                    ?.Play(placed.transform.position);
                return ApplyResult.Applied;
            }
            if (kind is GadgetChangeKind.Wired or GadgetChangeKind.Unwired)
            {
                if (gadget == null || !gadget.IsLinked || !car.Customization.TryGetCustomizerByUID(entry.OtherUid, out var other) ||
                    other is not GadgetBase otherGadget || entry.Index < 0 || entry.Index >= gadget.wiring.wireLinkPorts.Count ||
                    entry.OtherIndex < 0 || entry.OtherIndex >= otherGadget.wiring.wireLinkPorts.Count)
                    return ApplyResult.Failed;
                var a = gadget.wiring.wireLinkPorts[entry.Index];
                var b = otherGadget.wiring.wireLinkPorts[entry.OtherIndex];
                using (GadgetApplyScope.Enter())
                {
                    if (kind == GadgetChangeKind.Wired)
                        GadgetWiringModule.WireLinkPort.Wire(a, b);
                    else
                        GadgetWiringModule.WireLinkPort.Unwire(a, b);
                }
                GadgetIntentPatches.Confirmed(entry);
                return GadgetWiringModule.WireLinkPort.AreWired(a, b) == (kind == GadgetChangeKind.Wired) ? ApplyResult.Applied : ApplyResult.Failed;
            }
            if (kind == GadgetChangeKind.Soldered)
            {
                if (gadget is not TrainCarCustomization.TrainCarCustomizerBase solderable || !gadget.IsLinked)
                    return ApplyResult.Failed;
                // While this player solders it, its own units lead the server's.
                if (!(GadgetSolderingClient.Holding(gadget) && GadgetSolderingClient.Units(solderable) >= entry.Index))
                    using (GadgetApplyScope.Enter())
                        solderable.SetSolderingUnits(entry.Index);
                return ApplyResult.Applied;
            }
            if (kind == GadgetChangeKind.MountPoint)
            {
                if (gadget == null || !gadget.IsLinked || !gadget.TryGetComponent(out Drillable drillable) ||
                    entry.Index < 0 || entry.Index >= drillable.MountPointCount)
                    return ApplyResult.Failed;
                using (GadgetApplyScope.Enter())
                    drillable.SetMountPointState(entry.Index, (MountPoint.States)entry.Value);
                GadgetIntentPatches.Confirmed(entry);
                return drillable.GetMountPointState(entry.Index) == (MountPoint.States)entry.Value ? ApplyResult.Applied : ApplyResult.Failed;
            }
            if (gadget != null && gadget.IsLinked)
            {
                gadget.PlayRemoveSound();
                TakeOff(gadget, entry.Forced);
            }
            GadgetIntentPatches.Confirmed(entry);
            return ApplyResult.Applied;
        }
        catch (Exception exception)
        {
            Multiplayer.LogException($"Gadget change {kind} of item {entry.ItemNetId} on {car.ID} (revision {entry.Revision}) failed", exception);
            return ApplyResult.Failed;
        }
    }

    // A hole made or filled on its own (not as a removal's consequence, which the removal reproduces).
    private static ApplyResult ApplyHole(TrainCar car, GadgetChangeData entry)
    {
        Customization custom = car.Customization;
        using (GadgetApplyScope.Enter())
        {
            if (entry.Kind == (byte)GadgetChangeKind.HoleAdded)
            {
                if (!custom.FindHole(entry.Position, 0.02f, out _))
                    custom.AddHole(entry.Position, entry.Direction);
            }
            else if (custom.FindHole(entry.Position, 0.02f, out Collider hole))
            {
                custom.RemoveHole(hole);
            }
        }
        GadgetIntentPatches.Confirmed(entry);
        return ApplyResult.Applied;
    }

    /// <summary>Places a gadget the server described, as vanilla's load would, and tracks its state.</summary>
    private static bool Place(TrainCar car, InstalledGadgetData data, out GadgetBase gadget)
    {
        gadget = null;
        using (GadgetApplyScope.Enter())
        {
            if (!GadgetJoinApplier.TryPlace(car, data, out ItemSaveData saveData))
                return NetworkedItem.TryGet(data.ItemNetId, out NetworkedItem had) && had.GetComponent<GadgetItem>()?.Gadget is { IsLinked: true };
            saveData.PostLoadItemData();
            gadget = saveData.GetComponent<GadgetItem>()?.Gadget;
        }
        if (gadget == null || !gadget.IsLinked)
            return false;
        GadgetStateClient.Track(gadget);
        return true;
    }

    private static void TakeOff(GadgetBase gadget, bool forced)
    {
        using (GadgetApplyScope.Enter())
        {
            bool onCar = gadget.TrainCar != null;
            if (forced)
                gadget.ForceRemove(reparentToTrainCar: onCar);
            else
                gadget.Remove(reparentToTrainCar: onCar);
        }
    }

    private static void RequestResync(ushort carNetId, Anchor anchor, string why)
    {
        if (anchor.ResyncOutstanding)
            return;
        anchor.ResyncOutstanding = true;
        Multiplayer.LogWarning($"Gadgets on car {carNetId}: {why}; asking the server for them");
        NetworkClient.Instance?.SendGadgetResync(new ServerboundGadgetResyncRequestPacket { CarNetId = carNetId, AppliedRevision = anchor.Applied });
    }

    /// <summary>
    /// Makes the car's gadgets what the snapshot says (gadgets.md, "Desync and rollback"): a gadget the
    /// snapshot doesn't have, or has with another UID, comes off with its mount points freed first (so it
    /// leaves no holes); a missing one is placed as at join.
    /// </summary>
    private static void Rebuild(TrainCar car, GadgetAnchorSnapshotData data)
    {
        Dictionary<ushort, InstalledGadgetData> wanted = (data.Gadgets ?? []).ToDictionary(g => g.ItemNetId);
        for (bool progress = true; progress;)
        {
            progress = false;
            foreach (GadgetBase gadget in car.Customization.Customizers.OfType<GadgetBase>().ToList())
            {
                ushort itemNetId = gadget.GadgetItem?.GetComponent<NetworkedItem>()?.NetId ?? 0;
                if (wanted.TryGetValue(itemNetId, out InstalledGadgetData keep) && keep.Uid == gadget.UID)
                    continue;
                if (!gadget.CanBeRemovedUsingMethod(GadgetBase.GadgetRemovalMethod.Any))
                    continue;
                using (GadgetApplyScope.Enter())
                {
                    if (gadget.TryGetComponent(out Drillable drillable))
                        for (int i = 0; i < drillable.MountPointCount; i++)
                            drillable.SetMountPointState(i, MountPoint.States.None);
                }
                TakeOff(gadget, forced: true);
                progress = true;
            }
        }
        if (data.Holes != null)
        {
            using (GadgetApplyScope.Enter())
            {
                car.Customization.ClearHoles();
                for (int i = 0; i + 1 < data.Holes.Length; i += 2)
                    car.Customization.AddHole(data.Holes[i], data.Holes[i + 1]);
            }
        }
        int placed = 0;
        foreach (InstalledGadgetData gadget in wanted.Values)
        {
            if (NetworkedItem.TryGet(gadget.ItemNetId, out NetworkedItem item) && item.GetComponent<GadgetItem>()?.Gadget is { IsLinked: true })
                continue;
            if (Place(car, gadget, out _))
                placed++;
        }
        Multiplayer.Log($"Gadgets on {car.ID} resynced at revision {data.Revision}: {wanted.Count} on it, {placed} placed");
    }
}
