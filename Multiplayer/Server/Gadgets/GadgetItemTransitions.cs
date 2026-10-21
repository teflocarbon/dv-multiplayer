using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Core.Items;
using Multiplayer.Server.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Gadgets;

/// <summary>
/// Commits what vanilla does to an installed gadget's item on the server: into
/// <see cref="ItemPlacementKind.Installed"/> when <see cref="StorageController.AddItemToInstalledGadgets"/>
/// takes it (a player installing it, or the save loading), and out again when
/// <see cref="StorageController.MoveFromInstalledGadgetsToWorld"/> gives it back. Nothing else moves an
/// item into or out of Installed (gadgets.md, "The installed gadget and its item").
/// </summary>
[HarmonyPatch]
internal static class GadgetItemTransitions
{
    private static readonly HashSet<GadgetBase> offTrainRemovals = new();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StorageController), nameof(StorageController.AddItemToInstalledGadgets))]
    private static void AddedToInstalledGadgets(ItemBase item)
    {
        if (NetworkRole.IsServer && TryGetNetworked(item, out NetworkedItem networked))
            CommitInstalled(networked);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StorageController), nameof(StorageController.MoveFromInstalledGadgetsToWorld))]
    private static void MovedFromInstalledGadgets(GameObject item)
    {
        if (NetworkRole.IsServer && item != null && TryGetNetworked(item.GetComponent<ItemBase>(), out NetworkedItem networked))
            CommitRemoved(networked);
    }

    /// <summary>Whether vanilla holds the item as an installed gadget.</summary>
    internal static bool IsInstalled(NetworkedItem item)
    {
        try
        {
            return item?.Item != null &&
                   StorageController.Instance?.StorageInstalledGadgets?.ContainsItem(item.Item) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The world index asks before indexing an item by its transform: an installed gadget's
    /// inactive item would otherwise go to nearby players as a loose item at a stale pose. Commits
    /// one the server hasn't recorded as installed yet and tells the index to leave it.
    /// </summary>
    internal static bool TryCommitIfInstalled(NetworkedItem item)
    {
        if (!IsInstalled(item))
            return false;
        CommitInstalled(item);
        return true;
    }

    private static void CommitInstalled(NetworkedItem item)
    {
        GadgetBase gadget = item.GetComponent<GadgetItem>()?.Gadget;
        if (gadget == null || gadget.Custom == null)
            return;
        if (gadget.Custom is not TrainCarCustomization { TrainCar: { } car })
        {
            RemoveOffTrain(gadget, item);
            return;
        }
        if (AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record existing) &&
            existing.Placement == ItemPlacementKind.Installed)
            return;

        ItemUpdateData snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState);
        if (snapshot == null)
            return;
        NetworkedTrainCar.TryGetNetId(car, out ushort carNetId);
        snapshot.ItemState = ItemState.Installed;
        snapshot.PlayerId = 0;
        snapshot.CarNetId = carNetId;
        snapshot.WorldParentKind = ItemWorldParentKind.World;
        snapshot.WorldParentNetId = 0;
        snapshot.ItemPosition = gadget.transform.position - WorldMover.currentMove;
        snapshot.ItemRotation = gadget.transform.rotation;
        if (!AuthoritativeItemRegistry.TryApplyGadgetTransition(item, snapshot, null,
                ItemTransitionReason.GadgetInstalled, out string rejection,
                ItemChangeLog.Move(ItemMoveReason.InstalledAsGadget, detail: "on " + car.ID)))
        {
            Multiplayer.LogWarning($"Gadget {item.name} (netId {item.NetId}) installed on {car.ID}, but its " +
                                   $"record refused it: {rejection}");
            return;
        }
        if (AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record))
        {
            record.AttachedCarPersistentId = car.CarGUID ?? string.Empty;
            AdoptSavedOwner(item, record);
        }
        SendToPlayersWhoKnow(item, snapshot);
    }

    private static void CommitRemoved(NetworkedItem item)
    {
        if (!AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record) ||
            record.Placement != ItemPlacementKind.Installed)
            return;
        ItemUpdateData snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState);
        if (snapshot == null)
            return;
        // At the gadget's pose, where vanilla has just put the item. Where it goes next (the car's
        // interior, the remover's hand) its own observation reports, as for any of the server's items.
        snapshot.ItemState = ItemState.Dropped;
        snapshot.PlayerId = 0;
        snapshot.CarNetId = 0;
        snapshot.WorldParentKind = ItemWorldParentKind.World;
        snapshot.WorldParentNetId = 0;
        snapshot.ItemPosition = item.transform.position - WorldMover.currentMove;
        snapshot.ItemRotation = item.transform.rotation;
        if (!AuthoritativeItemRegistry.TryApplyGadgetTransition(item, snapshot, null,
                ItemTransitionReason.GadgetRemoved, out string rejection,
                ItemChangeLog.Move(ItemMoveReason.RemovedAsGadget)))
        {
            Multiplayer.LogWarning($"Gadget {item.name} (netId {item.NetId}) came off its car, but its record " +
                                   $"refused it: {rejection}");
            return;
        }
        SendToPlayersWhoKnow(item, snapshot);
    }

    /// <summary>
    /// The owner of an installed gadget is saved with it in vanilla's installed storage
    /// (<see cref="PersistentWorldItemIdentityPatch.OwnerKey"/>), since the mod's own records don't
    /// keep installed items. A record made at load takes it back.
    /// </summary>
    private static void AdoptSavedOwner(NetworkedItem item, AuthoritativeItemRegistry.Record record)
    {
        if (record.PersistentOwnerIdentity != Guid.Empty)
            return;
        Guid saved = item.GetComponent<PersistentWorldItemIdentity>()?.Owner ?? Guid.Empty;
        if (saved == Guid.Empty)
            return;
        record.PersistentOwnerIdentity = saved;
        record.PersistentOwnerPlayerId = OwnedItemsServer.OwnerNumber(saved);
        ServerPlayer owner = NetworkServer.Instance?.ServerPlayers.FirstOrDefault(player => player.Guid == saved);
        owner?.AddOwnedItem(item.NetId);
    }

    private static void SendToPlayersWhoKnow(NetworkedItem item, ItemUpdateData snapshot)
    {
        if (NetworkServer.Instance == null)
            return;
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
            if (player.Peer != NetworkServer.Instance.SelfPeer && player.KnownItems.ContainsKey(item))
                NetworkServer.Instance.SendItemUpdatePacket(snapshot, player);
    }

    /// <summary>
    /// Gadgets go on vehicles only in multiplayer (gadgets.md, "Off-train installs"). One a save
    /// from before has on the world, the house, the shed or the paint station is taken off into the
    /// world at its pose once the save's items have loaded, an ordinary item with its owner.
    /// </summary>
    private static void RemoveOffTrain(GadgetBase gadget, NetworkedItem item)
    {
        if (offTrainRemovals.Add(gadget))
            SingletonBehaviour<CoroutineManager>.Instance.StartCoroutine(RemoveWhenLoaded(gadget, item));
    }

    private static IEnumerator RemoveWhenLoaded(GadgetBase gadget, NetworkedItem item)
    {
        yield return null;
        StartingItemsController loader = SingletonBehaviour<StartingItemsController>.Instance;
        while (loader != null && !loader.itemsLoaded)
            yield return null;
        offTrainRemovals.Remove(gadget);
        if (gadget == null || !gadget.IsLinked)
            yield break;
        Multiplayer.Log($"Gadget {item?.name} (netId {item?.NetId}) was installed on " +
                        $"{gadget.Custom?.GetIdentificationKey()}; gadgets go on vehicles only in multiplayer, " +
                        "so it's taken off into the world");
        gadget.ForceRemove(reparentToTrainCar: false);
    }

    private static bool TryGetNetworked(ItemBase item, out NetworkedItem networked) =>
        NetworkedItem.TryGetNetworkedItem(item, out networked) && networked != null && networked.NetId != 0;
}
