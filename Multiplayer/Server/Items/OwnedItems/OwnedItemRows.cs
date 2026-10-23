using System;
using System.Collections.Generic;
using System.Linq;
using DV;
using Multiplayer.Core.Containers;
using Multiplayer.Core.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Trains;

namespace Multiplayer.Server.Items.OwnedItems;

/// <summary>One row of a player's owned-items list, as the server builds it.</summary>
public sealed class OwnedItemRow
{
    public uint Handle;
    public Guid Id;
    public string Prefab = string.Empty, DisplayName = string.Empty, Where = string.Empty;
    public bool Retrievable;
}

/// <summary>
/// A player's owned-items list (owned-items.md, "The owned-items list", PR 6a): a row per live
/// ledger entry they own, ended ones left out, with where it is worded by the server
/// (<see cref="OwnedItemPlaceText"/>). An item keeps its handle for the session, so a retrieval
/// (PR 6b) can name it while the list is open.
/// </summary>
public static class OwnedItemRows
{
    private static readonly Dictionary<Guid, uint> handles = new();
    private static readonly Dictionary<uint, Guid> byHandle = new();
    private static uint lastHandle;

    public static List<OwnedItemRow> For(ServerPlayer player)
    {
        List<OwnedItemRow> rows = new();
        if (player == null || player.Guid == Guid.Empty || !OwnedItemsServer.Loaded)
            return rows;

        Dictionary<Guid, AuthoritativeItemRegistry.Record> records = new();
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll())
            if (record.PersistentItemId != Guid.Empty && !records.ContainsKey(record.PersistentItemId))
                records.Add(record.PersistentItemId, record);
        ColdContainerGraphSnapshot cold = NetworkedColdContainerManager.Graph.ExportSnapshot();
        Dictionary<Guid, ColdStoredItemRecord> stored = cold.Items
            .GroupBy(item => item.PersistentItemId).ToDictionary(group => group.Key, group => group.First());
        Dictionary<Guid, ColdContainerRecord> containers = cold.Containers
            .GroupBy(container => container.PersistentContainerId).ToDictionary(group => group.Key, group => group.First());

        foreach (OwnedItemEntry entry in OwnedItemsServer.Ledger.LiveOwnedBy(player.Guid))
        {
            records.TryGetValue(entry.Id, out AuthoritativeItemRegistry.Record record);
            stored.TryGetValue(entry.Id, out ColdStoredItemRecord inContainer);
            string containerName = null;
            if (entry.Where.Place == OwnedItemPlace.Container && Guid.TryParse(entry.Where.Anchor, out Guid containerId) &&
                containers.TryGetValue(containerId, out ColdContainerRecord container))
                containerName = NameOf(container.PrefabName);
            OwnedItemPlaceFacts facts = new(player.Guid,
                holderName: NetworkServer.Instance?.ServerPlayers.FirstOrDefault(p => p.Guid == entry.Where.Holder)?.Username,
                carId: CarOf(record),
                containerName: containerName);
            rows.Add(new OwnedItemRow
            {
                Handle = HandleOf(entry.Id),
                Id = entry.Id,
                Prefab = entry.Prefab ?? string.Empty,
                DisplayName = !string.IsNullOrEmpty(inContainer?.DisplayName) ? inContainer.DisplayName : NameOf(entry.Prefab),
                Where = OwnedItemPlaceText.Describe(entry.Where, facts),
                Retrievable = OwnedItemRetrieval.CanRetrieve(player, entry, out _)
            });
        }
        return rows;
    }

    /// <summary>The item a handle from this session's lists names.</summary>
    public static bool TryResolve(uint handle, out Guid id) => byHandle.TryGetValue(handle, out id);

    private static uint HandleOf(Guid id)
    {
        if (!handles.TryGetValue(id, out uint handle))
        {
            handles[id] = handle = ++lastHandle;
            byHandle[handle] = id;
        }
        return handle;
    }

    // The car an item is in, on or attached to, by its id (L-042).
    private static string CarOf(AuthoritativeItemRegistry.Record record)
    {
        if (record == null)
            return null;
        ushort car = record.AttachedCarNetId != 0 ? record.AttachedCarNetId : record.WorldParentNetId;
        return car != 0 && NetworkedTrainCar.TryGet(car, out TrainCar trainCar) && trainCar != null ? trainCar.ID : null;
    }

    private static string NameOf(string prefab)
    {
        if (string.IsNullOrEmpty(prefab))
            return "ITEM";
        string name = Globals.G?.Items?.items?.FirstOrDefault(spec => spec != null &&
            string.Equals(spec.ItemPrefabName, prefab, StringComparison.Ordinal))?.LocalizedName;
        return string.IsNullOrEmpty(name) ? prefab : name;
    }
}
