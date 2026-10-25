using System;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Jobs;
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
    /// an object for now: no id, its state still being loaded, or ammo that can't go with it.
    /// </summary>
    internal static SavedItemRow ToLostData(LiveItems.LiveItem record, NetworkedItem item)
    {
        if (record == null || item == null || record.PersistentItemId == Guid.Empty ||
            materializing.Contains(record.NetId) || ItemSaveLoad.IsRestorePending(item) || !AmmoCanGoData(item))
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
        if (JobPapers.IsPaper(record.PrefabName))
            Jobs.JobPaperRows.Forget(record.NetId);
        LiveItems.Remove(record.NetId);
        NetworkedItem.FreeDataNetId(record.NetId);
    }
}
