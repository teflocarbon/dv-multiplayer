using DV.ThingTypes;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using UnityEngine;

namespace Multiplayer.Client.Session;

// The host's player's station requests (pit stops, plugs, module registers): the same packets a
// client sends, handed to the server in-process (NetworkServer.RegisterLocalStations), which
// validates and relays them like anyone's. The host's world already has them. Nothing comes back to
// present: the server leaves the host's player out of a station's relays and answers its register
// requests in-process (NetworkedCashRegisterWithModules.AnswerLocalRequest).
public partial class LocalPlayerSession : IStationsClient
{
    // The server answers at its next poll, not after a network round trip.
    private const float StationRequestTimeout = 2f;

    public float RPC_Timeout => StationRequestTimeout;

    public void SendPitStopInteractionPacket(ushort netId, PitStopStationInteractionType interaction, ResourceType? resource, float state)
    {
        Submit(new CommonPitStopInteractionPacket
        {
            Tick = NetworkLifecycle.Instance.Tick,
            NetId = netId,
            InteractionType = interaction,
            ResourceType = resource == null ? 0 : (int)resource,
            Value = state
        });
    }

    public void SendPitStopPlugInteractionPacket(ushort netId, PlugInteractionType interaction, Vector3? position = null,
        Quaternion? rotation = null, ushort trainCarNetId = 0, sbyte socketIndex = -1)
    {
        Submit(new CommonPitStopPlugInteractionPacket
        {
            NetId = netId,
            InteractionType = interaction,
            TrainCarNetId = trainCarNetId,
            SocketIndex = socketIndex,
            Position = position,
            Rotation = rotation,
        });
    }

    public void SendCashRegisterAction(ushort netId, CashRegisterAction action, double amount = 0.0f)
    {
        Submit(new CommonCashRegisterWithModulesActionPacket
        {
            NetId = netId,
            Action = action,
            Amount = amount
        });
    }
}
