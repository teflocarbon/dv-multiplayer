using DV.Customization;
using DV.Customization.Gadgets;
using Multiplayer.Server.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Gadgets;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Gadgets;

/// <summary>
/// What a player at <c>ReadyForCustomizers</c> gets (gadgets.md, "Late joiners"): a Create for every
/// installed gadget's item (placement <c>Installed</c>, on its car) and then one snapshot of every car's
/// gadgets: each item's UID and its vanilla save data, and the car's change log revision. The player's
/// game replays vanilla's load of that data, so whatever vanilla saves (soldering, wires, mount points,
/// settings, modded gadgets) arrives as the server has it. The items count as known to the player, so
/// the interest that keeps installed items relevant to every player (they ride cars, which every player
/// has) sends no second Create. A resync gets the same for one car.
/// </summary>
internal sealed class GadgetJoinSnapshot
{
    public readonly List<ItemUpdateData> ItemCreates = new();
    public ClientboundGadgetJoinSnapshotPacket Packet { get; private set; }

    public static GadgetJoinSnapshot Build(ServerPlayer player)
    {
        GadgetJoinSnapshot result = new();
        Dictionary<ushort, List<InstalledGadgetData>> byCar = new();
        foreach (AuthoritativeItemRegistry.Record record in InstalledRecords())
        {
            if (!TryDescribe(record, out NetworkedItem item, out ushort carNetId, out InstalledGadgetData data))
                continue;
            if (CreateFor(player, record, item, carNetId) is { } create)
                result.ItemCreates.Add(create);
            if (!byCar.TryGetValue(carNetId, out List<InstalledGadgetData> gadgets))
                byCar[carNetId] = gadgets = new List<InstalledGadgetData>();
            gadgets.Add(data);
        }
        // A car whose gadgets all came off still has a revision a joining player has to start from.
        foreach (ushort carNetId in GadgetChangeLogServer.CarsWithRevisions())
            if (!byCar.ContainsKey(carNetId))
                byCar[carNetId] = new List<InstalledGadgetData>();
        result.Packet = new ClientboundGadgetJoinSnapshotPacket
        {
            Anchors = byCar.Select(car => new GadgetAnchorSnapshotData
            {
                CarNetId = car.Key,
                Revision = GadgetChangeLogServer.Revision(car.Key),
                Gadgets = car.Value.ToArray()
            }).ToArray()
        };
        return result;
    }

    /// <summary>One car's gadgets for <paramref name="player"/>, with the Creates of the items they don't know.</summary>
    public static GadgetAnchorSnapshotData ForCar(ServerPlayer player, ushort carNetId, List<ItemUpdateData> creates)
    {
        List<InstalledGadgetData> gadgets = new();
        foreach (AuthoritativeItemRegistry.Record record in InstalledRecords())
        {
            if (!TryDescribe(record, out NetworkedItem item, out ushort onCar, out InstalledGadgetData data) || onCar != carNetId)
                continue;
            if (CreateFor(player, record, item, carNetId) is { } create)
                creates.Add(create);
            gadgets.Add(data);
        }
        return new GadgetAnchorSnapshotData
        {
            CarNetId = carNetId,
            Revision = GadgetChangeLogServer.Revision(carNetId),
            Gadgets = gadgets.ToArray(),
            Holes = HolesOf(carNetId)
        };
    }

    /// <summary>
    /// The item's Create for a player who doesn't know it yet (placement <c>Installed</c>, on its car),
    /// counting it as known from now; null if they know it.
    /// </summary>
    public static ItemUpdateData CreateFor(ServerPlayer player, AuthoritativeItemRegistry.Record record, NetworkedItem item, ushort carNetId)
    {
        if (player.KnownItems.ContainsKey(item))
            return null;
        ItemUpdateData create = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create);
        if (create == null)
            return null;
        AuthoritativeItemRegistry.WriteToSnapshot(record, create, record.LastReason);
        create.ItemState = ItemState.Installed;
        create.CarNetId = carNetId;
        player.KnownItems[item] = NetworkLifecycle.Instance?.Tick ?? 0;
        player.NearbyItems[item] = Time.time;
        return create;
    }

    /// <summary>An installed gadget as another game places it: its item, UID and vanilla save data.</summary>
    public static InstalledGadgetData Describe(NetworkedItem item, GadgetBase gadget)
    {
        JObject state = item.GetComponent<ItemSaveData>()?.SaveItemData();
        if (state == null)
            return null;
        // The mod's own keys (persistent id, owner) are the server's records, not the player's.
        foreach (JProperty own in state.Properties().Where(p => p.Name.StartsWith("dvmp", StringComparison.Ordinal)).ToList())
            own.Remove();
        return new InstalledGadgetData { ItemNetId = item.NetId, Uid = gadget.UID, State = state.ToString(Formatting.None) };
    }

    /// <summary>The car's holes, position then direction in its parenting frame for each.</summary>
    private static Vector3[] HolesOf(ushort carNetId)
    {
        if (!NetworkedTrainCar.TryGet(carNetId, out TrainCar car) || car == null || car.Customization == null)
            return null;
        List<Vector3> holes = new();
        foreach (Collider hole in car.Customization.Holes)
        {
            if (hole == null)
                continue;
            holes.Add(hole.transform.localPosition);
            holes.Add(hole.transform.localRotation * Vector3.forward);
        }
        return holes.ToArray();
    }

    private static List<AuthoritativeItemRegistry.Record> InstalledRecords() =>
        AuthoritativeItemRegistry.GetAll().Where(r => r.Placement == ItemPlacementKind.Installed).ToList();

    private static bool TryDescribe(AuthoritativeItemRegistry.Record record, out NetworkedItem item, out ushort carNetId,
        out InstalledGadgetData data)
    {
        carNetId = 0;
        data = null;
        if (!NetworkedItem.TryGet(record.NetId, out item) || item == null)
            return false;
        GadgetBase gadget = item.GetComponent<GadgetItem>()?.Gadget;
        if (gadget?.Custom is not TrainCarCustomization { TrainCar: { } car } ||
            !NetworkedTrainCar.TryGetNetId(car, out carNetId))
            return false;
        data = Describe(item, gadget);
        return data != null;
    }
}
