using DV.Customization;
using DV.Customization.Gadgets;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
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
/// installed gadget's item (placement <c>Installed</c>, on its car or the world) and then one snapshot of
/// every anchor's gadgets: each item's UID and its vanilla save data. The world's anchor is car 0. The player's game replays vanilla's load of
/// that data, so whatever vanilla saves (soldering, wires, mount points, settings, modded gadgets)
/// arrives as the server has it. The items count as known to the player, so the interest that keeps
/// installed items relevant to every player (they ride cars, which every player has) sends no
/// second Create.
/// </summary>
internal sealed class GadgetJoinSnapshot
{
    public readonly List<ItemUpdate> ItemCreates = new();
    public ClientboundGadgetJoinSnapshotPacket Packet { get; private set; }

    public static GadgetJoinSnapshot Build(ServerPlayer player)
    {
        GadgetJoinSnapshot result = new();
        Dictionary<ushort, List<InstalledGadgetData>> byCar = new();
        uint tick = NetworkLifecycle.Instance?.Tick ?? 0;
        foreach (LiveItems.LiveItem record in LiveItems.GetAll()
                     .Where(r => r.Placement == ItemPlacementKind.Installed).ToList())
        {
            if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null)
                continue;
            GadgetBase gadget = item.GetComponent<GadgetItem>()?.Gadget;
            ushort carNetId = 0;
            if (gadget?.Custom is TrainCarCustomization { TrainCar: { } car })
            {
                if (!NetworkedTrainCar.TryGetNetId(car, out carNetId))
                    continue;
            }
            else if (gadget?.Custom is not WorldCustomization)
                continue;
            JObject state = item.GetComponent<ItemSaveData>()?.SaveItemData();
            ItemUpdate create = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Create);
            if (state == null || create == null)
                continue;

            LiveItems.WriteToSnapshot(record, create, record.LastReason);
            create.ItemState = ItemState.Installed;
            create.CarNetId = carNetId;
            result.ItemCreates.Add(create);
            player.KnownItems[item] = tick;
            player.NearbyItems[item] = Time.time;

            // The mod's own keys (persistent id, owner) are the server's records, not the player's.
            foreach (JProperty own in state.Properties().Where(p => p.Name.StartsWith("dvmp", StringComparison.Ordinal)).ToList())
                own.Remove();
            if (!byCar.TryGetValue(carNetId, out List<InstalledGadgetData> gadgets))
                byCar[carNetId] = gadgets = new List<InstalledGadgetData>();
            gadgets.Add(new InstalledGadgetData
            {
                ItemNetId = item.NetId,
                Uid = gadget.UID,
                State = state.ToString(Formatting.None)
            });
        }
        result.Packet = new ClientboundGadgetJoinSnapshotPacket
        {
            Anchors = byCar.Select(car => new GadgetAnchorSnapshotData { CarNetId = car.Key, Gadgets = car.Value.ToArray() })
                .ToArray()
        };
        return result;
    }
}
