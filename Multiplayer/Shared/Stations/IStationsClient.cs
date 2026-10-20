using DV.ThingTypes;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Shared.Stations;

/// <summary>
///     The station requests a player at this keyboard sends (pit stops, plugs, module cash
///     registers), as Shared code makes them without referencing the client
///     (docs/design/code-layout.md). <c>NetworkClient</c> implements it, in
///     <c>Client/Session/NetworkClient.Stations.cs</c>, and on the host its player's session
///     (<c>LocalPlayerSession.Stations.cs</c>), which hands them to the server in-process
///     (docs/design/host-local-session.md).
/// </summary>
public interface IStationsClient
{
    void SendPitStopInteractionPacket(ushort netId, PitStopStationInteractionType interaction, ResourceType? resource, float state);

    void SendPitStopPlugInteractionPacket(ushort netId, PlugInteractionType interaction, Vector3? position = null,
        Quaternion? rotation = null, ushort trainCarNetId = 0, sbyte socketIndex = -1);

    void SendCashRegisterAction(ushort netId, CashRegisterAction action, double amount = 0.0f);

    /// <summary>How long to wait for the server's answer to a request before giving up on it.</summary>
    float RPC_Timeout { get; }
}

public static class StationsClient
{
    /// <summary>This game's player session, or null when no client is running.</summary>
    public static IStationsClient Current => NetworkLifecycle.Instance?.LocalSession as IStationsClient;
}
