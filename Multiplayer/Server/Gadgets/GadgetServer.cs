using DV.Customization;
using DV.CabControls;
using DV.Customization.Gadgets;
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

    /// <summary>
    /// The gadget's owner retrieves it from their owned-items list (owned-items.md, "Retrieval", PR 8): it
    /// comes off as removing it by hand does, recorded in the car's change log under the owner, and stays
    /// in the world for the caller to send to their Lost and Found. False, with a reason, when it can't.
    /// </summary>
    internal static bool RemoveForRetrieval(ServerPlayer owner, NetworkedItem item, out string reason)
    {
        reason = string.Empty;
        GadgetBase gadget = item != null ? GadgetStateServer.LinkedGadget(item.NetId) : null;
        if (gadget == null)
        {
            reason = "not-an-installed-gadget";
            return false;
        }
        if (!gadget.CanBeRemovedUsingMethod(GadgetBase.GadgetRemovalMethod.Any))
        {
            reason = "gadget-cant-come-off";
            return false;
        }
        GadgetChangeLogServer.BeginOperation(owner, 0);
        try
        {
            using (GadgetApplyScope.Enter())
                gadget.Remove(reparentToTrainCar: false);
        }
        finally
        {
            GadgetChangeLogServer.EndOperation();
        }
        if (NetworkServer.Instance != null)
            GadgetChangeLogServer.Flush(NetworkServer.Instance);
        return true;
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
    /// Vanilla's placement check at the pose, with the car's placement surfaces switched on for it:
    /// vanilla keeps them on only within 20 m of the local player, which a dedicated host never is.
    /// </summary>
    private static bool CanPlaceWithSurfaces(TrainCar car, Vector3 position, Quaternion rotation, GadgetBase prefab)
    {
        GadgetColliderHolder[] holders = car.GetComponentsInChildren<GadgetColliderHolder>(true);
        bool[] wasActive = holders.Select(h => h.holder != null && h.holder.activeSelf).ToArray();
        try
        {
            foreach (GadgetColliderHolder holder in holders)
                if (holder.holder != null)
                    holder.holder.SetActive(true);
            Physics.SyncTransforms();
            return GadgetItem.CanPlace(position, rotation, prefab);
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
