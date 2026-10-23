using DV.Customization.Gadgets;
using Multiplayer.Client.Session;
using Multiplayer.Core.Gadgets;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// A remote client's side of gadget buttons and running state (gadgets.md, "Gadget state"), for the
/// gadgets the server sent. A button this player presses has run vanilla's effect here already
/// (GadgetCommandPatches) and goes to the server as a command; the server's running state replaces
/// what this game shows unless this player's own newer press is still on its way (GadgetSettingEcho,
/// the same rule as for settings).
/// </summary>
internal static class GadgetRunningClient
{
    private const float PendingTimeoutSeconds = 5f;

    private sealed class Pending
    {
        public uint Operation;
        public float Since;
    }

    private static readonly Dictionary<GadgetBase, Pending> pending = new();
    private static uint nextOperation;

    // What came for gadgets still being placed (the join snapshot's apply waits for their items).
    private static readonly Dictionary<ushort, ClientboundGadgetRunningPacket> early = new();

    internal static void Reset()
    {
        pending.Clear();
        early.Clear();
    }

    /// <summary>After the join snapshot's gadgets are placed: the running state that came first.</summary>
    internal static void ApplyEarly()
    {
        List<ClientboundGadgetRunningPacket> packets = new(early.Values);
        early.Clear();
        foreach (ClientboundGadgetRunningPacket packet in packets)
            Receive(packet);
    }

    /// <summary>This game's player pressed <paramref name="command"/> on <paramref name="gadget"/>.</summary>
    internal static void Pressed(GadgetBase gadget, GadgetCommand command)
    {
        if (!NetworkRole.IsRemoteClient || GadgetRunningState.Applying || !GadgetJoinApplier.IsServerSent(gadget))
            return;
        ushort itemNetId = gadget.GadgetItem?.GetComponent<NetworkedItem>()?.NetId ?? 0;
        if (itemNetId == 0)
            return;
        uint operation = ++nextOperation;
        pending[gadget] = new Pending { Operation = operation, Since = Time.realtimeSinceStartup };
        NetworkClient.Instance?.SendGadgetCommand(new ServerboundGadgetCommandPacket
        {
            ItemNetId = itemNetId,
            OperationId = operation,
            Command = (byte)command
        });
    }

    internal static void Receive(ClientboundGadgetRunningPacket packet)
    {
        if (!NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item) || item == null ||
            item.GetComponent<GadgetItem>()?.Gadget is not { IsLinked: true } gadget ||
            !GadgetJoinApplier.IsServerSent(gadget))
        {
            if (GadgetJoinApplier.Applying)
                early[packet.ItemNetId] = packet;
            return;
        }
        uint mine = pending.TryGetValue(gadget, out Pending press) &&
                    Time.realtimeSinceStartup - press.Since < PendingTimeoutSeconds
            ? press.Operation
            : 0;
        GadgetSettingEchoDecision decision = GadgetSettingEcho.Decide(NetworkClient.Instance?.PlayerId ?? 0, mine,
            packet.PlayerId, packet.OperationId, packet.Refused);
        if (decision.ClearsPending || mine == 0)
            pending.Remove(gadget);
        if (decision.Action == GadgetSettingEchoAction.Apply)
            GadgetRunningState.Apply(gadget, packet.Keys, packet.Values);
    }
}
