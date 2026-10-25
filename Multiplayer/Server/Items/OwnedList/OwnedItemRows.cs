using System;
using System.Collections.Generic;
using System.Linq;
using DV;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.OwnedList;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Trains;

namespace Multiplayer.Server.Items.OwnedList;

/// <summary>One row of a player's owned-items list, as the server builds it.</summary>
public sealed class OwnedItemRow
{
    public uint Handle;
    public Guid Id;
    public string Prefab = string.Empty, DisplayName = string.Empty, Where = string.Empty;
    public bool Retrievable;
}

/// <summary>
/// A player's owned-items list (owned-list.md, "The rows"): a row per live
/// ledger entry they own, ended ones left out, with where it is worded by the server
/// (<see cref="PlaceText"/>). A row is named by the item's handle (<see cref="ItemHandles"/>),
/// so a retrieval (PR 6b) can name it while the list is open.
/// </summary>
public static class OwnedItemRows
{
    public static List<OwnedItemRow> For(ServerPlayer player)
    {
        List<OwnedItemRow> rows = new();
        if (player == null || player.Guid == Guid.Empty || !ServerLedger.Loaded)
            return rows;

        Dictionary<Guid, LiveItems.LiveItem> records = new();
        foreach (LiveItems.LiveItem record in LiveItems.GetAll())
            if (record.PersistentItemId != Guid.Empty && !records.ContainsKey(record.PersistentItemId))
                records.Add(record.PersistentItemId, record);
        ContainerGraphSnapshot cold = ContainerServer.Graph.ExportSnapshot();
        Dictionary<Guid, StoredItemRecord> stored = cold.Items
            .GroupBy(item => item.PersistentItemId).ToDictionary(group => group.Key, group => group.First());
        Dictionary<Guid, ContainerRecord> containers = cold.Containers
            .GroupBy(container => container.PersistentContainerId).ToDictionary(group => group.Key, group => group.First());

        foreach (LedgerEntry entry in ServerLedger.Ledger.LiveOwnedBy(OwnerRef.Player(player.Guid)))
        {
            records.TryGetValue(entry.Id, out LiveItems.LiveItem record);
            stored.TryGetValue(entry.Id, out StoredItemRecord inContainer);
            string containerName = null;
            if (entry.Home.Kind is HomeKind.Container or HomeKind.Magazine && Guid.TryParse(entry.Home.Anchor, out Guid containerId) &&
                containers.TryGetValue(containerId, out ContainerRecord container))
                containerName = NameOf(container.PrefabName);
            PlaceFacts facts = new(player.Guid,
                holderName: NetworkServer.Instance?.ServerPlayers.FirstOrDefault(p => p.Guid == entry.Home.Holder)?.Username,
                carId: CarOf(record),
                containerName: containerName);
            rows.Add(new OwnedItemRow
            {
                Handle = ItemHandles.Of(entry.Id),
                Id = entry.Id,
                Prefab = entry.Prefab ?? string.Empty,
                DisplayName = !string.IsNullOrEmpty(inContainer?.DisplayName) ? inContainer.DisplayName : NameOf(entry.Prefab),
                Where = PlaceText.Describe(entry.Home, facts),
                Retrievable = Retrieval.CanRetrieve(player, entry, out _)
            });
        }
        return rows;
    }

    // The car an item is in, on or attached to, by its id (L-042).
    private static string CarOf(LiveItems.LiveItem record)
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
