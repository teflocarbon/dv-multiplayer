using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Gadgets;

/// <summary>
/// Installed gadgets' running state on the server (gadgets.md, "Gadget state"), watched with the
/// settings (GadgetStateServer.Tick): a step or a reset goes to every player at once, a count that
/// runs its way once a second. A player's button press is checked like a setting, run as vanilla's
/// button runs it, and always answered with the state it left. The defect detector's snooze follows
/// vanilla's rules here, from the server's own trains, because its LOD works them out only where
/// it's loaded (never on a dedicated host).
/// </summary>
internal static class GadgetRunningServer
{
    private const float RunningIntervalSeconds = 1f;

    private sealed class Watched
    {
        public Dictionary<string, float> Sent;
        public float SentAt;
        // The defect detector's cars that are derailed or overheating: a new one is a new alert.
        public HashSet<TrainCar> Alerts = new();
    }

    private static readonly Dictionary<ushort, Watched> watched = new();

    internal static void Watch(ushort itemNetId, GadgetBase gadget, NetworkServer server)
    {
        if (!GadgetRunningState.Has(gadget))
            return;
        if (!watched.TryGetValue(itemNetId, out Watched entry))
            watched[itemNetId] = entry = new Watched();
        if (GadgetRunningState.DefectDetector(gadget) != null)
            DefectRules(gadget, entry);

        Dictionary<string, float> now = GadgetRunningState.Capture(gadget);
        // First sight: a joining player gets these in its snapshot (the gadget's save data).
        if (entry.Sent == null)
        {
            entry.Sent = now;
            entry.SentAt = Time.realtimeSinceStartup;
            return;
        }
        GadgetRunningState.Change change = GadgetRunningState.Compare(gadget, now, entry.Sent);
        if (change == GadgetRunningState.Change.None ||
            change == GadgetRunningState.Change.Running && Time.realtimeSinceStartup - entry.SentAt < RunningIntervalSeconds)
            return;
        Send(itemNetId, entry, now, 0, 0, false, null, server);
    }

    /// <summary>A joining player: every installed gadget's running state, after its snapshot (the save
    /// data has only some of it).</summary>
    internal static void SendAll(ServerPlayer player, NetworkServer server)
    {
        foreach (ushort itemNetId in AuthoritativeItemRegistry.GetAll()
                     .Where(r => r.Placement == ItemPlacementKind.Installed)
                     .Select(r => r.NetId).ToList())
        {
            GadgetBase gadget = GadgetStateServer.LinkedGadget(itemNetId);
            if (gadget != null && GadgetRunningState.Has(gadget))
                Send(itemNetId, Entry(itemNetId), GadgetRunningState.Capture(gadget), 0, 0, false, player, server);
        }
    }

    internal static void Forget(HashSet<ushort> installed)
    {
        foreach (ushort gone in watched.Keys.Where(id => !installed.Contains(id)).ToList())
            watched.Remove(gone);
    }

    internal static void HandleCommand(ServerboundGadgetCommandPacket packet, ServerPlayer player, NetworkServer server)
    {
        GadgetBase gadget = GadgetStateServer.LinkedGadget(packet.ItemNetId);
        GadgetCommand command = (GadgetCommand)packet.Command;
        string refusal = player.LoadingState < PlayerLoadingState.ReadyForCustomizers ? "still loading"
            : gadget == null ? "not an installed gadget"
            : !GadgetCommands.Fits(gadget, command) ? $"{gadget.name} has no {command}"
            : GadgetStateServer.TouchRefusal(player, gadget, packet.ItemNetId);
        if (refusal != null)
        {
            Multiplayer.LogWarning($"Gadget command {command} from {player.Username} on item {packet.ItemNetId} refused: {refusal}");
            if (gadget != null && GadgetRunningState.Has(gadget))
                Send(packet.ItemNetId, Entry(packet.ItemNetId), GadgetRunningState.Capture(gadget),
                    player.PlayerId, packet.OperationId, true, player, server);
            return;
        }

        Watched entry = Entry(packet.ItemNetId);
        GadgetCommands.Run(gadget, command, HasDefect(gadget));
        Dictionary<string, float> now = GadgetRunningState.Capture(gadget);
        bool changed = GadgetRunningState.Compare(gadget, now, entry.Sent) != GadgetRunningState.Change.None;
        Send(packet.ItemNetId, entry, now, player.PlayerId, packet.OperationId, false, changed ? null : player, server);
    }

    /// <summary>
    /// Vanilla's GadgetDefectDetectorLOD: there's a defect while the detector is powered and a car of
    /// its train is derailed or its brakes overheat; a car derailing or starting to overheat is a new
    /// alert, which wakes a snoozed detector; no defect, no snooze.
    /// </summary>
    private static void DefectRules(GadgetBase gadget, Watched entry)
    {
        HashSet<TrainCar> alerts = new(Train(gadget).Where(car => car.derailed || car.brakeSystem.heatController.overheatPercentage > 0f));
        bool newAlert = alerts.Any(car => !entry.Alerts.Contains(car));
        entry.Alerts = alerts;
        GadgetDefectDetectorLOD lod = GadgetRunningState.DefectDetector(gadget);
        if (lod.isSnoozed && (newAlert || !HasDefect(gadget)))
            GadgetRunningState.Apply(gadget, new[] { "snoozed" }, new[] { 0f });
    }

    private static bool HasDefect(GadgetBase gadget) =>
        gadget.PowerState && Train(gadget).Any(car => car.derailed || car.brakeSystem.heatController.overheatPercentage > 0f);

    private static IEnumerable<TrainCar> Train(GadgetBase gadget) =>
        gadget.IsOnTrainCar && gadget.TrainCar.trainset != null
            ? gadget.TrainCar.trainset.cars.Where(car => car != null && car.brakeSystem != null)
            : Array.Empty<TrainCar>();

    private static Watched Entry(ushort itemNetId)
    {
        if (!watched.TryGetValue(itemNetId, out Watched entry))
            watched[itemNetId] = entry = new Watched();
        return entry;
    }

    private static void Send(ushort itemNetId, Watched entry, Dictionary<string, float> state, byte playerId,
        uint operationId, bool refused, ServerPlayer to, NetworkServer server)
    {
        if (to == null)
        {
            entry.Sent = state;
            entry.SentAt = Time.realtimeSinceStartup;
        }
        server.SendGadgetRunning(new ClientboundGadgetRunningPacket
        {
            ItemNetId = itemNetId,
            PlayerId = playerId,
            OperationId = operationId,
            Refused = refused,
            Keys = state.Keys.ToArray(),
            Values = state.Values.ToArray()
        }, to);
    }
}
