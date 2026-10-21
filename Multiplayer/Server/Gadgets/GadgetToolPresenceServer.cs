using Multiplayer.Server.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Server.Gadgets;

/// <summary>
/// Players using gadget tools, relayed to everyone who could hear them (gadgets.md, "Tool use other
/// players see", PR 11). Only a tool in the player's own hand counts. The host's own player's tool comes
/// here directly (GadgetToolPresence), anyone else's as a request.
/// </summary>
internal static class GadgetToolPresenceServer
{
    // Vanilla's tool sounds fade out well inside this.
    private const float HearingMetres = 60f;

    private static readonly Dictionary<ushort, (byte player, bool working, float at)> tools = new();

    /// <summary>Whether the server last heard that tool working (scenarios, and a dedicated host's view).</summary>
    internal static bool IsWorking(ushort toolNetId) =>
        tools.TryGetValue(toolNetId, out var state) && state.working && Time.realtimeSinceStartup - state.at < 2.5f;

    internal static void Relay(ServerPlayer player, ushort toolNetId, bool working, NetworkServer server)
    {
        if (player == null || server == null || !AuthoritativeItemRegistry.TryGet(toolNetId, out AuthoritativeItemRegistry.Record record) ||
            record.Placement != ItemPlacementKind.PlayerHand || record.PlacementPlayerId != player.PlayerId)
            return;
        tools[toolNetId] = (player.PlayerId, working, Time.realtimeSinceStartup);
        ClientboundGadgetToolUsePacket packet = new() { PlayerId = player.PlayerId, ToolItemNetId = toolNetId, Working = working };
        foreach (ServerPlayer other in server.ServerPlayers)
        {
            if (other == player || other.LoadingState < PlayerLoadingState.ReadyForCustomizers ||
                Vector3.Distance(other.WorldPosition, player.WorldPosition) > HearingMetres)
                continue;
            server.SendGadgetToolUse(packet, other);
        }
    }
}
