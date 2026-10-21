using DV.Customization;
using DV.Customization.Gadgets;
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
/// Installed gadgets' settings on the server (gadgets.md, "Gadget state"). The server's own game is
/// the truth: a few times a second it reads every installed gadget's settings and sends what
/// changed to every player, whoever changed them (the host's player at a knob, server logic, a
/// request it applied). A player's request is checked, applied through vanilla's setters and always
/// answered, so its sender can stop waiting: by the broadcast if it changed anything, or by an echo
/// to the sender alone, or, refused, by the values as they are.
/// </summary>
internal static class GadgetStateServer
{
    private const uint WatchIntervalTicks = NetworkLifecycle.TICK_RATE / 10 > 0 ? NetworkLifecycle.TICK_RATE / 10 : 1;
    // Vanilla reaches 3 m; the rest is lag.
    private const float ReachMetres = 5f;

    private sealed class Watched
    {
        public Dictionary<string, float> Sent;
        public uint Revision;
    }

    private static readonly Dictionary<ushort, Watched> watched = new();
    private static uint lastWatchTick;

    internal static void Tick(uint tick, NetworkServer server)
    {
        if (unchecked(tick - lastWatchTick) < WatchIntervalTicks)
            return;
        lastWatchTick = tick;
        HashSet<ushort> seen = new();
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll()
                     .Where(r => r.Placement == ItemPlacementKind.Installed).ToList())
        {
            GadgetBase gadget = LinkedGadget(record.NetId);
            if (gadget == null || !GadgetSettings.Has(gadget))
                continue;
            seen.Add(record.NetId);
            Dictionary<string, float> settings = GadgetSettings.Capture(gadget);
            // First sight: a joining player gets these in its snapshot, and the rest already have them.
            if (!watched.TryGetValue(record.NetId, out Watched entry))
            {
                watched[record.NetId] = new Watched { Sent = settings };
                continue;
            }
            if (!GadgetSettings.Differ(settings, entry.Sent))
                continue;
            entry.Sent = settings;
            entry.Revision++;
            server.SendGadgetState(Packet(record.NetId, entry, settings, 0, 0, false));
        }
        foreach (ushort gone in watched.Keys.Where(id => !seen.Contains(id)).ToList())
            watched.Remove(gone);
    }

    internal static void HandleRequest(ServerboundGadgetSettingPacket packet, ServerPlayer player, NetworkServer server)
    {
        GadgetBase gadget = LinkedGadget(packet.ItemNetId);
        string refusal = Refusal(packet, player, gadget);
        if (refusal != null)
        {
            Multiplayer.LogWarning($"Gadget setting from {player.Username} on item {packet.ItemNetId} refused: {refusal}");
            if (gadget != null && GadgetSettings.Has(gadget))
                server.SendGadgetState(Packet(packet.ItemNetId, Entry(packet.ItemNetId, gadget),
                    GadgetSettings.Capture(gadget), player.PlayerId, packet.OperationId, true), player);
            return;
        }

        GadgetSettings.Apply(gadget, packet.Keys, packet.Values);
        Watched entry = Entry(packet.ItemNetId, gadget);
        Dictionary<string, float> settings = GadgetSettings.Capture(gadget);
        bool changed = GadgetSettings.Differ(settings, entry.Sent);
        entry.Sent = settings;
        if (changed)
            entry.Revision++;
        ClientboundGadgetStatePacket answer = Packet(packet.ItemNetId, entry, settings, player.PlayerId, packet.OperationId, false);
        server.SendGadgetState(answer, changed ? null : player);
    }

    private static string Refusal(ServerboundGadgetSettingPacket packet, ServerPlayer player, GadgetBase gadget)
    {
        if (player.LoadingState < PlayerLoadingState.ReadyForCustomizers)
            return "still loading";
        if (gadget == null)
            return "not an installed gadget";
        if (!GadgetSettings.Has(gadget))
            return $"{gadget.name} has no settings";
        if (packet.Keys == null || packet.Values == null || packet.Keys.Length != packet.Values.Length)
            return "malformed";
        // Riding the gadget's car puts it in reach wherever it is on the car.
        bool onItsCar = gadget.Custom is TrainCarCustomization { TrainCar: { } car } &&
                        NetworkedTrainCar.TryGetNetId(car, out ushort carNetId) && player.CarId == carNetId;
        float distance = Vector3.Distance(player.WorldPosition, gadget.transform.position);
        if (!onItsCar && distance > ReachMetres)
            return $"out of reach ({distance:0.0} m)";
        // Permissions plug in here (gadgets.md, "Decisions for the user"): today anyone may.
        return null;
    }

    private static GadgetBase LinkedGadget(ushort itemNetId) =>
        AuthoritativeItemRegistry.TryGet(itemNetId, out AuthoritativeItemRegistry.Record record) &&
        record.Placement == ItemPlacementKind.Installed &&
        NetworkedItem.TryGet(itemNetId, out NetworkedItem item) && item != null &&
        item.GetComponent<GadgetItem>()?.Gadget is { IsLinked: true } gadget
            ? gadget
            : null;

    private static Watched Entry(ushort itemNetId, GadgetBase gadget)
    {
        if (!watched.TryGetValue(itemNetId, out Watched entry))
            watched[itemNetId] = entry = new Watched { Sent = GadgetSettings.Capture(gadget) };
        return entry;
    }

    private static ClientboundGadgetStatePacket Packet(ushort itemNetId, Watched entry, Dictionary<string, float> settings,
        byte playerId, uint operationId, bool refused) => new()
    {
        ItemNetId = itemNetId,
        Revision = entry.Revision,
        PlayerId = playerId,
        OperationId = operationId,
        Refused = refused,
        Keys = settings.Keys.ToArray(),
        Values = settings.Values.ToArray()
    };
}
