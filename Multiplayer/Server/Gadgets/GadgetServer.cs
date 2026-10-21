using DV.Customization;
using DV.CabControls;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.Utils;
using Multiplayer.Core.Items;
using Multiplayer.Server.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Gadgets;

/// <summary>
/// A remote player's gadget installs and removals (gadgets.md, "Requests", "What the server checks").
/// The server checks a request with its own rules and vanilla's (the placement check, run with the
/// car's placement surfaces switched on, which vanilla only does near the local player), applies it
/// natively in the apply scope, and the change log's entry, carrying the request's operation, is the
/// answer; a refused one is answered with its reason. The host's own player installs and removes in
/// the server's game directly, as vanilla does, and the change log sees it the same way.
/// </summary>
internal static class GadgetServer
{
    // Vanilla reaches 3 m; the rest is lag.
    private const float ReachMetres = 5f;

    internal static void HandleInstall(ServerboundGadgetInstallPacket packet, ServerPlayer player, NetworkServer server)
    {
        string refusal = InstallRefusal(packet, player, out GadgetItem gadgetItem, out TrainCar car, out Mount mount, out Collider collider);
        if (refusal != null)
        {
            Reject(server, player, packet.OperationId, packet.ItemNetId, "install", refusal);
            return;
        }
        Transform frame = car.Customization.GetParentingTransform();
        GadgetBase gadget;
        GadgetChangeLogServer.BeginOperation(player, packet.OperationId);
        try
        {
            using (GadgetApplyScope.Enter())
            {
                gadget = GadgetItem.Place(car.Customization, packet.LocalPosition, packet.LocalRotation, gadgetItem, collider);
                if (gadget != null && mount != null)
                    mount.MountGadget(gadget);
            }
        }
        finally
        {
            GadgetChangeLogServer.EndOperation();
        }
        if (gadget == null || !gadget.IsLinked)
        {
            Reject(server, player, packet.OperationId, packet.ItemNetId, "install", "placing it failed");
            return;
        }
        // The installer's game plays it when the entry arrives; the host's player hears it here.
        if (NetworkRole.HasLocalPlayer)
            (gadget.SoundOnPlaced != null ? gadget.SoundOnPlaced : SingletonBehaviour<GadgetSystemUtility>.Instance.SoundOnGadgetPlaced)
                ?.Play(frame.TransformPoint(packet.LocalPosition));
    }

    internal static void HandleRemove(ServerboundGadgetRemovePacket packet, ServerPlayer player, NetworkServer server)
    {
        GadgetBase gadget = GadgetStateServer.LinkedGadget(packet.ItemNetId);
        var method = (GadgetBase.GadgetRemovalMethod)packet.Method;
        string refusal = player.LoadingState < PlayerLoadingState.ReadyForCustomizers ? "still loading"
            : gadget == null ? "not an installed gadget"
            : method != GadgetBase.GadgetRemovalMethod.Remover && method != GadgetBase.GadgetRemovalMethod.EmptyHand ? $"no removal method {packet.Method}"
            : !gadget.CanBeRemovedUsingMethod(method) ? $"{gadget.name} can't be taken off by {method}"
            : GadgetStateServer.ReachRefusal(player, gadget);
        if (refusal != null)
        {
            Reject(server, player, packet.OperationId, packet.ItemNetId, "removal", refusal);
            return;
        }
        GadgetChangeLogServer.BeginOperation(player, packet.OperationId);
        try
        {
            using (GadgetApplyScope.Enter())
            {
                if (NetworkRole.HasLocalPlayer)
                    gadget.PlayRemoveSound();
                // Off onto the car's floor, where vanilla leaves it; a hand removal's player picks it up
                // from there in their own game, as an ordinary grab.
                gadget.Remove(reparentToTrainCar: true);
            }
        }
        finally
        {
            GadgetChangeLogServer.EndOperation();
        }
    }

    internal static void HandleResync(ServerboundGadgetResyncRequestPacket packet, ServerPlayer player, NetworkServer server)
    {
        if (player.LoadingState < PlayerLoadingState.ReadyForCustomizers || !NetworkedTrainCar.TryGet(packet.CarNetId, out TrainCar _))
            return;
        Multiplayer.LogDebug(() => $"{player.Username} resyncs the gadgets on car {packet.CarNetId} (applied revision {packet.AppliedRevision})");
        List<ItemUpdateData> creates = new();
        var anchor = GadgetJoinSnapshot.ForCar(player, packet.CarNetId, creates);
        if (creates.Count > 0)
            server.SendItemsBulkUpdatePacket(creates, player);
        server.SendGadgetAnchorSnapshot(new ClientboundGadgetAnchorSnapshotPacket { Anchor = anchor }, player);
    }

    /// <summary>
    /// A car is about to be deleted: its gadgets come off at once (forced, recorded) and each goes to its
    /// owner's Lost and Found, online or not; an unowned one stays where it was (gadgets.md, "The
    /// installed gadget and its item"). Gadgets riding another come off first. The removals go out before
    /// the car does.
    /// </summary>
    internal static void CollectFromDeletedCar(TrainCar car)
    {
        if (car == null || !NetworkRole.IsServer || car.Customization == null)
            return;
        NetworkedTrainCar.TryGetNetId(car, out ushort carNetId);
        List<NetworkedItem> removed = new();
        for (bool progress = true; progress;)
        {
            progress = false;
            foreach (GadgetBase gadget in car.Customization.Customizers.OfType<GadgetBase>().ToList())
            {
                if (!gadget.IsLinked || !gadget.CanBeRemovedUsingMethod(GadgetBase.GadgetRemovalMethod.Any))
                    continue;
                NetworkedItem item = gadget.GadgetItem?.GetComponent<NetworkedItem>();
                gadget.ForceRemove(reparentToTrainCar: false);
                if (item != null)
                    removed.Add(item);
                progress = true;
            }
        }
        foreach (NetworkedItem item in removed)
            if (item != null && !NetworkedLostAndFoundManager.Collect(item, LostItemReason.GadgetDestinationMissing, $"car {car.ID} deleted"))
                Multiplayer.LogDebug(() => $"Gadget {item.name} ({item.NetId}) from deleted car {car.ID} has no owner: left in the world");
        if (NetworkServer.Instance != null)
            GadgetChangeLogServer.Flush(NetworkServer.Instance);
        if (carNetId != 0)
            GadgetChangeLogServer.Forget(carNetId);
    }

    private static string InstallRefusal(ServerboundGadgetInstallPacket packet, ServerPlayer player, out GadgetItem gadgetItem,
        out TrainCar car, out Mount mount, out Collider collider)
    {
        gadgetItem = null;
        car = null;
        mount = null;
        collider = null;
        if (player.LoadingState < PlayerLoadingState.ReadyForCustomizers)
            return "still loading";
        if (!NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item) || item == null ||
            (gadgetItem = item.GetComponent<GadgetItem>()) == null || gadgetItem.Gadget == null)
            return "not a gadget item";
        if (gadgetItem.Gadget.IsLinked)
            return "already installed";
        if (!AuthoritativeItemRegistry.TryGet(packet.ItemNetId, out AuthoritativeItemRegistry.Record record) ||
            record.Placement != ItemPlacementKind.PlayerHand || record.PlacementPlayerId != player.PlayerId)
            return "not in their hand";
        if (!NetworkedTrainCar.TryGet(packet.CarNetId, out car) || car == null || car.Customization == null)
            return "no such car";
        if (!IsFinite(packet.LocalPosition) || !IsFinite(packet.LocalRotation))
            return "malformed pose";
        // Vanilla sets this in Start, which an item the server only ever held inactive may not have run.
        gadgetItem.Item ??= item.GetComponent<ItemBase>();

        Transform frame = car.Customization.GetParentingTransform();
        Vector3 position = frame.TransformPoint(packet.LocalPosition);
        Quaternion rotation = frame.rotation * packet.LocalRotation;
        bool riding = player.CarId == packet.CarNetId;
        float distance = Vector3.Distance(player.WorldPosition, position);
        if (!riding && distance > ReachMetres)
            return $"out of reach ({distance:0.0} m)";

        GadgetBase prefab = gadgetItem.gadgetPrefab;
        if (packet.MountHostUid != 0)
        {
            if (!car.Customization.TryGetCustomizerByUID(packet.MountHostUid, out var host) || !host.TryGetComponent(out mount))
                return $"no mount {packet.MountHostUid} on {car.ID}";
            if (!mount.Accepts(gadgetItem.Gadget))
                return "the mount doesn't take it";
            if (mount.IsInUse)
                return "the mount is in use";
            if (host.TryGetComponent(out Drillable drillable) && drillable.AttachedPointCount < prefab.RequiredMountPoints)
                return "the mount isn't fixed down";
        }
        else if (prefab.RequiredMountPoints > 0)
        {
            return "it needs a mount";
        }

        collider = GadgetColliderPath.Resolve(car, packet.ColliderPath, packet.ColliderIndex);
        if (collider != null && car.Customization.IsHole(collider))
            return "on a hole";
        if (!CanPlaceWithSurfaces(car, position, rotation, prefab))
            return "obstructed (vanilla's placement check)";
        // Permissions plug in here (gadgets.md, "Decisions for the user"): today anyone may.
        return null;
    }

    /// <summary>
    /// A player bolts, tapes or untapes a mount point (PR 4). Bolting needs a drill in their hand,
    /// taping a tape with uses left (the server counts them), untaping a taped point; the new state must
    /// be one vanilla allows there (<c>Drillable.CheckIfCanChangeToState</c>, with the car's surfaces on).
    /// </summary>
    internal static void HandleMountPoint(ServerboundGadgetMountPointPacket packet, ServerPlayer player, NetworkServer server)
    {
        GadgetBase gadget = GadgetStateServer.LinkedGadget(packet.ItemNetId);
        var state = (MountPoint.States)packet.State;
        Drillable drillable = gadget != null ? gadget.GetComponent<Drillable>() : null;
        DuctTape tape = null;
        string refusal = player.LoadingState < PlayerLoadingState.ReadyForCustomizers ? "still loading"
            : gadget == null ? "not an installed gadget"
            : drillable == null || packet.Index < 0 || packet.Index >= drillable.MountPointCount ? $"no mount point {packet.Index}"
            : state == MountPoint.States.Mounted && Held<DrillTool>(player) == null ? "no drill in hand"
            : state == MountPoint.States.Taped && ((tape = Held<DuctTape>(player)) == null || tape.usesLeft <= 0) ? "no tape left in hand"
            : state == MountPoint.States.None && drillable.GetMountPointState(packet.Index) != MountPoint.States.Taped ? "only tape comes off by hand"
            : state != MountPoint.States.None && state != MountPoint.States.Mounted && state != MountPoint.States.Taped ? $"no state {packet.State}"
            : GadgetStateServer.ReachRefusal(player, gadget);
        if (refusal == null && gadget.TrainCar is { } car &&
            !WithSurfaces(car, () => drillable.CheckIfCanChangeToState(packet.Index, state, out _)))
            refusal = "vanilla's check refuses that state there";
        if (refusal != null)
        {
            Reject(server, player, packet.OperationId, packet.ItemNetId, "mount point", refusal);
            return;
        }
        GadgetChangeLogServer.BeginOperation(player, packet.OperationId);
        try
        {
            using (GadgetApplyScope.Enter())
                drillable.SetMountPointState(packet.Index, state);
        }
        finally
        {
            GadgetChangeLogServer.EndOperation();
        }
        if (tape != null)
        {
            // The server's count; the empty roll's swap (a new item in the player's hand) isn't done yet.
            tape.usesLeft--;
            tape.tapeModelUpdater?.UpdateActiveStates(tape.PercentageUsesLeft);
        }
    }

    /// <summary>A player fills a hole in a car with the hole filler in their hand (PR 4).</summary>
    internal static void HandleFillHole(ServerboundGadgetFillHolePacket packet, ServerPlayer player, NetworkServer server)
    {
        Collider hole = null;
        TrainCar car = null;
        string refusal = player.LoadingState < PlayerLoadingState.ReadyForCustomizers ? "still loading"
            : !NetworkedTrainCar.TryGet(packet.CarNetId, out car) || car == null || car.Customization == null ? "no such car"
            : Held<HoleFiller>(player) == null ? "no hole filler in hand"
            : !car.Customization.FindHole(packet.LocalPosition, 0.02f, out hole) ? "no hole there"
            : null;
        if (refusal == null)
        {
            float distance = Vector3.Distance(player.WorldPosition, hole.transform.position);
            if (player.CarId != packet.CarNetId && distance > ReachMetres)
                refusal = $"out of reach ({distance:0.0} m)";
        }
        if (refusal != null)
        {
            Reject(server, player, packet.OperationId, 0, "hole fill", refusal);
            return;
        }
        GadgetChangeLogServer.BeginOperation(player, packet.OperationId);
        try
        {
            using (GadgetApplyScope.Enter())
                car.Customization.RemoveHole(hole);
        }
        finally
        {
            GadgetChangeLogServer.EndOperation();
        }
    }

    /// <summary>
    /// A player wires or unwires two gadgets on a car with the crimping tool in their hand (PR 5):
    /// vanilla's own rules decide (<c>ReadyToWire</c> for a wire, <c>AreWired</c> for an unwire).
    /// </summary>
    internal static void HandleWire(ServerboundGadgetWirePacket packet, ServerPlayer player, NetworkServer server)
    {
        GadgetWiringModule.WireLinkPort source = null, target = null;
        string refusal = player.LoadingState < PlayerLoadingState.ReadyForCustomizers ? "still loading"
            : !NetworkedTrainCar.TryGet(packet.CarNetId, out TrainCar car) || car == null || car.Customization == null ? "no such car"
            : !TryPort(car, packet.SourceUid, packet.SourcePort, out source) || !TryPort(car, packet.TargetUid, packet.TargetPort, out target) ? "no such ports"
            : Held<GadgetWiringTool>(player) == null ? "no crimping tool in hand"
            : packet.Wire && !GadgetWiringModule.WireLinkPort.ReadyToWire(source, target) ? "those ports can't be wired"
            : !packet.Wire && !GadgetWiringModule.WireLinkPort.AreWired(source, target) ? "those ports aren't wired"
            : GadgetStateServer.ReachRefusal(player, source.owner);
        if (refusal != null)
        {
            Reject(server, player, packet.OperationId, 0, packet.Wire ? "wire" : "unwire", refusal);
            return;
        }
        GadgetChangeLogServer.BeginOperation(player, packet.OperationId);
        try
        {
            using (GadgetApplyScope.Enter())
            {
                if (packet.Wire)
                    GadgetWiringModule.WireLinkPort.Wire(source, target);
                else
                    GadgetWiringModule.WireLinkPort.Unwire(source, target);
            }
        }
        finally
        {
            GadgetChangeLogServer.EndOperation();
        }
    }

    private static bool TryPort(TrainCar car, int uid, int index, out GadgetWiringModule.WireLinkPort port)
    {
        port = null;
        if (!car.Customization.TryGetCustomizerByUID(uid, out var customizer) || customizer is not GadgetBase gadget ||
            index < 0 || index >= gadget.wiring.wireLinkPorts.Count)
            return false;
        port = gadget.wiring.wireLinkPorts[index];
        return true;
    }

    // Twice vanilla's iron (32768 units a second), for lag.
    private const float SolderUnitsPerSecond = 65536f;
    private static readonly Dictionary<(byte player, ushort item), float> lastSolder = new();

    /// <summary>
    /// A player soldering a gadget sends the units their game reached (PR 5). The server takes them up to
    /// what the time since their last allows, with the soldering iron in their hand; the watch's
    /// <c>Soldered</c> entry carries the result. Soldering never goes backwards here.
    /// </summary>
    internal static void HandleSolder(ServerboundGadgetSolderPacket packet, ServerPlayer player, NetworkServer server)
    {
        GadgetBase gadget = GadgetStateServer.LinkedGadget(packet.ItemNetId);
        var solderable = gadget as TrainCarCustomization.TrainCarCustomizerBase;
        string refusal = player.LoadingState < PlayerLoadingState.ReadyForCustomizers ? "still loading"
            : solderable == null || !solderable.IsSolderable ? "not a gadget that's soldered"
            : !gadget.ArePlacementRequirementsMet ? "its placement isn't complete"
            : Held<GadgetSolderingTool>(player) == null ? "no soldering iron in hand"
            : GadgetStateServer.ReachRefusal(player, gadget);
        if (refusal != null)
        {
            Reject(server, player, packet.OperationId, packet.ItemNetId, "soldering", refusal);
            return;
        }
        float now = Time.realtimeSinceStartup;
        var key = (player.PlayerId, packet.ItemNetId);
        float since = lastSolder.TryGetValue(key, out float last) ? Mathf.Min(now - last, 1f) : 0.25f;
        lastSolder[key] = now;
        int current = 65536 - solderable.SolderingUnitsRequired;
        int allowed = current + Mathf.CeilToInt(since * SolderUnitsPerSecond);
        int units = Mathf.Clamp(packet.Units, current, Mathf.Min(allowed, 65536));
        if (units == current)
            return;
        using (GadgetApplyScope.Enter())
            solderable.SetSolderingUnits(units);
    }

    /// <summary>The tool of type <typeparamref name="T"/> in the player's hand, as the server has it.</summary>
    private static T Held<T>(ServerPlayer player) where T : Component
    {
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll())
            if (record.Placement == ItemPlacementKind.PlayerHand && record.PlacementPlayerId == player.PlayerId &&
                NetworkedItem.TryGet(record.NetId, out NetworkedItem item) && item != null &&
                item.GetComponentInChildren<T>(true) is { } tool)
                return tool;
        return null;
    }

    /// <summary>Vanilla's placement check at the pose, with the car's placement surfaces on (WithSurfaces).</summary>
    private static bool CanPlaceWithSurfaces(TrainCar car, Vector3 position, Quaternion rotation, GadgetBase prefab) =>
        WithSurfaces(car, () => GadgetItem.CanPlace(position, rotation, prefab));

    /// <summary>
    /// Runs one of vanilla's surface checks with the car's placement surfaces switched on for it: vanilla
    /// keeps them on only within 20 m of the local player, which a dedicated host never is.
    /// </summary>
    private static bool WithSurfaces(TrainCar car, System.Func<bool> check)
    {
        GadgetColliderHolder[] holders = car.GetComponentsInChildren<GadgetColliderHolder>(true);
        bool[] wasActive = holders.Select(h => h.holder != null && h.holder.activeSelf).ToArray();
        try
        {
            foreach (GadgetColliderHolder holder in holders)
                if (holder.holder != null)
                    holder.holder.SetActive(true);
            Physics.SyncTransforms();
            return check();
        }
        finally
        {
            for (int i = 0; i < holders.Length; i++)
                if (holders[i].holder != null)
                    holders[i].holder.SetActive(wasActive[i]);
        }
    }

    private static void Reject(NetworkServer server, ServerPlayer player, uint operationId, ushort itemNetId, string what, string reason)
    {
        Multiplayer.LogWarning($"Gadget {what} from {player.Username} (item {itemNetId}, operation {operationId}) refused: {reason}");
        server.SendGadgetReject(new ClientboundGadgetRejectPacket { OperationId = operationId, ItemNetId = itemNetId, Reason = reason }, player);
    }

    private static bool IsFinite(Vector3 v) => !float.IsNaN(v.x + v.y + v.z) && !float.IsInfinity(v.x + v.y + v.z);

    private static bool IsFinite(Quaternion q) =>
        !float.IsNaN(q.x + q.y + q.z + q.w) && !float.IsInfinity(q.x + q.y + q.z + q.w) && q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f;
}
