using Multiplayer.Client.Trains;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Train;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Multiplayer.Client.Session;

// The host's player's half of the trains (docs/design/trains.md, "The host's own client"). Its cars
// get the same local-player part a client's do (NetworkedTrainCarClient: cab and external controls
// hooked for leases, chain drags tracked), with its follower half off, as the host's world is the
// server's. Its requests are a client's packets, handed to the server in-process
// (NetworkServer.RegisterLocalTrains). The host's own control changes and couplings aren't requests:
// the car commits them through its server half, and the server observes its world's couplings.
public partial class LocalPlayerSession : ITrainClient
{
    partial void RegisterTrains()
    {
        // Another player took or gave back a lever: block or free it in the host's cab; or the host's
        // own grab was refused.
        Handle<ClientboundTrainControlAuthorityUpdatePacket>(OnTrainControlAuthorityUpdate);
    }

    private static void OnTrainControlAuthorityUpdate(ClientboundTrainControlAuthorityUpdatePacket packet)
    {
        if (NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar car))
            car.Client()?.ReceiveAuthorityUpdate(packet.PortNetId, packet.State);
    }

    public ITrainCarClientPart CreateCarPart(NetworkedTrainCar car) => new NetworkedTrainCarClient(car);

    // The host's world is the canonical topology: it follows nobody's.
    public bool TryFollowingHostUncoupleCause(Coupler coupler, Coupler other, out ChangeCause<UncoupleReason> cause)
    {
        cause = default;
        return false;
    }

    public void SendTrainControlAuthorityRequest(ushort netId, uint portNetId, bool requestAuthority) =>
        Submit(new ServerboundTrainControlAuthorityPacket
        {
            NetId = netId,
            PortNetId = portNetId,
            RequestAuthority = requestAuthority
        });

    public void SendTrainDeleteRequest(ushort netId) =>
        Submit(new ServerboundTrainDeleteRequestPacket { NetId = netId });

    public void SendTrainRerailRequest(ushort netId, ushort trackId, Vector3 position, Vector3 forward) =>
        Submit(new ServerboundTrainRerailRequestPacket
        {
            NetId = netId,
            TrackId = trackId,
            Position = position,
            Forward = forward
        });

    // The answer comes back through the host's peer and resolves the radio's ticket (OnRpcResponse).
    public void SendWorkTrainRequest(uint ticketId, string liveryId, ushort trackNetId, int position, bool withTrackDirection) =>
        Submit(new ServerboundWorkTrainRequestPacket
        {
            TicketId = ticketId,
            LiveryId = liveryId,
            TrackNetId = trackNetId,
            Index = position,
            WithTrackDirection = withTrackDirection
        });
}
