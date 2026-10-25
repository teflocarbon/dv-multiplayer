using System;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Server.Items.Inventory;

// Lost and Found entries kept as data on the server (OI-COLD-08): rows as a cold inventory's, which
// Lost and Found keeps, under the record and NetId the entry had.
internal static partial class PlayerItems
{
    /// <summary>
    /// A Lost and Found entry's object goes and its state is a row, for Lost and Found to keep
    /// (OI-COLD-08); its record and NetId stay, and its ammo goes to data with it. Null when it stays
    /// an object for now: no id, its state still being loaded, ammo that can't go with it, or no row
    /// that could make it again.
    /// </summary>
    internal static SavedItemRow ToLostData(LiveItems.LiveItem record, NetworkedItem item)
    {
        if (InstalledItems.RefuseDestroy(item, "kept as data in Lost and Found"))
            return null;
        if (record == null || item == null || record.PersistentItemId == Guid.Empty ||
            materializing.Contains(record.NetId) || ItemSaveLoad.IsRestorePending(item) || !AmmoCanGoData(item) ||
            !CanBeLostData(record, item))
            return null;
        MakeOrdinary(item);
        SavedItemRow row = ItemSaveLoad.RowOf(record, item);
        row.OwnerIdentity = record.Owner;
        row.PlacementPlayerIdentity = Guid.Empty;
        row.Slot = -1;
        // A paper is made again from its job (OI-COLD-07).
        if (JobPapers.IsPaper(record.PrefabName))
            row.State = JobPapers.Complete(item.gameObject, row.State);
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
        {
            player.KnownItems.Remove(item);
            player.NearbyItems.Remove(item);
            player.AcknowledgedWorldItems.Remove(item.NetId);
        }
        AmmoGoesData(item, "Lost and Found", Array.Empty<byte>());
        ServerItemManager.Instance?.UnregisterHostWorldItem(item);
        item.HandOverToData();
        StorageIntegration.MoveTo(item.Item, StorageMembership.None);
        Object.Destroy(item.gameObject);
        Multiplayer.LogDebug(() => $"Lost and Found: {record.PrefabName} (netId {record.NetId}) is data only on the server");
        return row;
    }

    // As ToData: an item no row can make again stays an object in Lost and Found, or every retrieval
    // would be refused (lost-item-not-made) and the entry stay for good (J3): one with no prefab in
    // the game's item list (a mod's), an expired or missing-licence report, a paper with no job. Nor
    // does one the host's own inventory still holds as an object (a VR belt slot keeps objects,
    // OI-COLD-14): destroying it would leave the belt holding nothing (J8).
    private static bool CanBeLostData(LiveItems.LiveItem record, NetworkedItem item) =>
        !InventoryIntegration.ContainsActive(item.gameObject) &&
        InventoryStandIn.CanStandIn(item.Item?.InventorySpecs?.ItemPrefabName ?? record.PrefabName) &&
        (JobPapers.PaperOf(item.gameObject) == null || JobPapers.CanBeRow(item.gameObject));

    /// <summary>
    /// The object of a Lost and Found entry kept as data, made again from its row under its NetId,
    /// with its ammo, for a retrieval or a recall. Null if it couldn't be.
    /// </summary>
    internal static NetworkedItem FromLostData(LiveItems.LiveItem record, SavedItemRow row)
    {
        if (record == null || row == null || materializing.Contains(record.NetId))
            return null;
        GameObject instance = InstantiateFromRow(record.NetId, row, out NetworkedItem item);
        if (instance == null)
            return null;
        try
        {
            return TakeUpObject(record, row, item, null);
        }
        catch (Exception exception)
        {
            item.AbortColdMaterialization();
            Object.Destroy(instance);
            Multiplayer.LogWarning($"Lost and Found: couldn't make {row.PrefabName} (netId {record.NetId}) from its data: {exception.Message}");
            return null;
        }
        finally
        {
            materializing.Remove(record.NetId);
        }
    }

    /// <summary>A Lost and Found entry kept as data is gone (its job ended, it was replaced): its record and NetId go.</summary>
    internal static void LostDataGone(LiveItems.LiveItem record)
    {
        if (record == null)
            return;
        AmmoGoesCold(record, null);
        bool paper = JobPapers.IsPaper(record.PrefabName);
        if (paper)
            Jobs.JobPaperRows.Forget(record.NetId);
        // A game that had the item when it was collected keeps an inert copy under this NetId; it
        // goes before the NetId can be given to another item (J7).
        ItemUpdate destroy = new()
        {
            UpdateType = ItemUpdate.ItemUpdateType.Destroy,
            ItemNetId = record.NetId,
            PrefabName = record.PrefabName
        };
        LiveItems.WriteToSnapshot(record, destroy, record.LastReason);
        destroy.RemovalCause = ItemChangeLog.Removal(paper ? ItemRemovalReason.JobDocumentDestroyed : ItemRemovalReason.DestroyedByGame,
            null, "a Lost and Found entry kept as data, gone");
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
            if (player.Peer != null && player != NetworkServer.Instance.LocalPlayer)
                NetworkServer.Instance.SendItemUpdate(destroy, player);
        LiveItems.Remove(record.NetId);
        NetworkedItem.FreeDataNetId(record.NetId);
    }
}
