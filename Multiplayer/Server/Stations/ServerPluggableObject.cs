using DV.Interaction;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using UnityEngine;

namespace Multiplayer.Server.Stations;

/// <summary>
/// The server's part of a service station's plug (beside its <see cref="NetworkedPluggableObject"/>):
/// validates players' interactions with it, keeps who holds it, and tells the players at the station.
/// Added by the station's <see cref="ServerPitStopStation"/>.
/// </summary>
public class ServerPluggableObject : MonoBehaviour
{
    private const float DISTANCE_TOLERANCE = 1.1f; //allow 10% tolerance for interactions coming from clients
    private const float GRAB_SQR_DISTANCE = GrabberRaycaster.SPHERE_CAST_MAX_DIST * GrabberRaycaster.SPHERE_CAST_MAX_DIST * DISTANCE_TOLERANCE;
    private const float DOCK_SQR_DISTANCE = 3f * 3f * DISTANCE_TOLERANCE; // 2f is hardcoded into `PluggableObject.ScanForHit()`, however this is the distance from the plug to the socket, not the distance from the player to the plug. 3f is a more reasonable distance for docking plugs.

    public NetworkedPluggableObject Plug { get; private set; }
    private ServerPitStopStation station;

    private static NetworkServer Server => NetworkServer.Instance;

    // Called by hand, once: the plug can be inactive, and Unity only runs Awake once it's active.
    public void Init(ServerPitStopStation station)
    {
        if (Plug != null)
            return;
        this.station = station;
        Plug = GetComponent<NetworkedPluggableObject>();
        Server.PlayerDisconnected += OnPlayerDisconnected;
    }

    protected void OnDestroy()
    {
        if (Server != null)
            Server.PlayerDisconnected -= OnPlayerDisconnected;
    }

    public void ProcessInteraction(CommonPitStopPlugInteractionPacket packet, ServerPlayer senderPlayer)
    {
        ushort netId = Plug.NetId;
        Multiplayer.LogDebug(() => $"ServerPluggableObject.ProcessInteraction() NetId: {netId}, InteractionType: {packet.InteractionType}, from player: {senderPlayer.Username}");

        if (!ValidateInteraction(packet, senderPlayer))
        {
            //Failed to validate, player needs to rollback interaction (the host's own client ignores it)
            if (senderPlayer != Server.LocalPlayer)
                Server.SendPitStopPlugInteractionPacket(senderPlayer, new CommonPitStopPlugInteractionPacket
                {
                    NetId = netId,
                    InteractionType = PlugInteractionType.Rejected,
                });
            return;
        }

        //passed validation, set server states
        Multiplayer.LogDebug(() => $"ServerPluggableObject.ProcessInteraction() VALIDATION PASSED for NetId: {netId}, processing {packet.InteractionType}");

        switch (packet.InteractionType)
        {
            case PlugInteractionType.PickedUp:
                Plug.HeldByPlayerId = senderPlayer.PlayerId;
                Plug.TrainCarNetId = NetworkedPluggableObject.INVALID_NETID;
                Plug.SocketIndex = NetworkedPluggableObject.INVALID_SOCKET;
                break;

            case PlugInteractionType.Dropped:
            case PlugInteractionType.Yanked: //we should never reach this as Yanked is only sent by the server and should be rejected by validation
            case PlugInteractionType.DockHome:
                Plug.HeldByPlayerId = 0;
                Plug.TrainCarNetId = NetworkedPluggableObject.INVALID_NETID;
                Plug.SocketIndex = NetworkedPluggableObject.INVALID_SOCKET;
                break;

            case PlugInteractionType.DockSocket:
                Plug.HeldByPlayerId = 0;
                Plug.TrainCarNetId = packet.TrainCarNetId;
                Plug.SocketIndex = packet.SocketIndex;
                break;
        }

        packet.PlayerId = senderPlayer.PlayerId;

        //Apply it here unless it's the host player's own: that has happened in this world already
        if (senderPlayer != Server.LocalPlayer)
        {
            // Cleared here, not left to the plug's LateUpdate: that only clears it once there's an
            // interaction to report, and after a client's dock there isn't, so the flag outlived this
            // and swallowed the host player's own next grab (never sent, so the next dock was refused).
            Plug.ProcessingAsHost = true;
            try
            {
                Plug.ProcessPacket(packet);
            }
            finally
            {
                Plug.ProcessingAsHost = false;
            }
        }

        //send to all players in active area, except originator
        SendToStation(packet, senderPlayer);
    }

    public bool ValidateInteraction(CommonPitStopPlugInteractionPacket packet, ServerPlayer player)
    {
        PlugInteractionType interactionType = packet.InteractionType;
        PluggableObject pluggable = Plug.PluggableObject;
        ushort netId = Plug.NetId;

        if (interactionType == PlugInteractionType.Rejected || interactionType == PlugInteractionType.Yanked)
        {
            Server.LogWarning($"{player.Username} sent an invalid interaction type ({interactionType})!");
            return false;
        }

        //validate ownership of object
        if (Plug.HeldByPlayerId == 0 && interactionType != PlugInteractionType.PickedUp)
        {
            Server.LogWarning($"{player.Username} attempted to interact with a plug that they are not holding!");
            return false;
        }

        //ensure the player is holding the object or no one is holding the object
        if (Plug.HeldByPlayerId != 0 && Plug.HeldByPlayerId != player.PlayerId)
        {
            string holder = Server.TryGetServerPlayer(Plug.HeldByPlayerId, out ServerPlayer holding) ? holding.Username : $"P{Plug.HeldByPlayerId}";
            Server.LogWarning($"{player.Username} attempted to interact with a plug that is held by {holder}");
            return false;
        }

        if (interactionType == PlugInteractionType.DockSocket)
        {
            //verify TrainCar
            if (packet.TrainCarNetId == 0 || !NetworkedTrainCar.TryGet(packet.TrainCarNetId, out NetworkedTrainCar networkedTrainCar) || networkedTrainCar == null)
            {
                Multiplayer.LogDebug(() => $"ServerPluggableObject.ValidateInteraction() NetId: {netId}, trainCarNetId: {packet.TrainCarNetId}, NetworkedTrainCar not found!");
                return false;
            }

            //verify TrainCar is in station
            if (!(Plug.Station?.Station?.pitstop?.carList?.Contains(networkedTrainCar.TrainCar) ?? false))
            {
                Multiplayer.LogDebug(() => $"ServerPluggableObject.ValidateInteraction() NetId: {netId}, trainCarNetId: {packet.TrainCarNetId}, Not in Pitstop car list!");
                return false;
            }

            //verify socket exists (only locos have sockets)
            var socket = Plug.GetTrainCarSocket(networkedTrainCar, packet.SocketIndex);
            if (socket == null)
            {
                Server.LogWarning($"{player.Username} attempted to insert plug that into a socket that doesn't exist!");
                return false;
            }

            //verify socket is compatible
            if (!socket.CanAccept(pluggable) && socket.Plug != pluggable)
            {
                Server.LogWarning($"{player.Username} attempted to dock a {pluggable.connectionTag} plug into a {socket.connectionTag} socket, but socket is not compatible!");
                return false;
            }

            //verify distance to socket
            float sqrDistance = (socket.transform.GetWorldAbsolutePosition() - pluggable.transform.GetWorldAbsolutePosition()).sqrMagnitude;
            Multiplayer.LogDebug(() => $"ServerPluggableObject.ValidateInteraction() NetId: {netId}, {interactionType}, trainCar: [{networkedTrainCar.CurrentID}, {packet.TrainCarNetId}], socket No.: {packet.SocketIndex} player pos: {player.AbsoluteWorldPosition}, plug pos: {pluggable.transform.GetWorldAbsolutePosition()}, socket pos: {socket.transform.GetWorldAbsolutePosition()}, sqrDistance: {sqrDistance}, Dock distance: {DOCK_SQR_DISTANCE}");
            if (sqrDistance > DOCK_SQR_DISTANCE)
            {
                Server.LogWarning($"{player.Username} attempted to dock a plug into {networkedTrainCar.TrainCar.ID}, but socket is too far away!");
                return false;
            }
        }
        else if (interactionType == PlugInteractionType.DockHome)
        {
            //verify distance to socket
            var socket = pluggable.startAttachedTo;
            float sqrDistance = (socket.transform.GetWorldAbsolutePosition() - pluggable.transform.GetWorldAbsolutePosition()).sqrMagnitude;
            if (sqrDistance > DOCK_SQR_DISTANCE)
            {
                Server.LogWarning($"{player.Username} attempted to dock a plug into the stand, but socket is too far away!");
                return false;
            }
        }
        else if (interactionType == PlugInteractionType.Dropped)
        {
            // no verifications required
        }
        else if (interactionType == PlugInteractionType.PickedUp)
        {
            float sqrDistance = (player.AbsoluteWorldPosition - pluggable.transform.GetWorldAbsolutePosition()).sqrMagnitude;

            Multiplayer.LogDebug(() => $"ServerPluggableObject.ValidateInteraction() NetId: {netId}, {interactionType}, player pos: {player.AbsoluteWorldPosition}, plug pos: {pluggable.transform.GetWorldAbsolutePosition()}, sqrDistance: {sqrDistance}, Raycast distance: {GRAB_SQR_DISTANCE}");
            if (sqrDistance > GRAB_SQR_DISTANCE)
            {
                Server.LogWarning($"{player.Username} attempted to interact with a plug that is too far away!");
                return false;
            }
        }

        return true;
    }

    private void OnPlayerDisconnected(ServerPlayer disconnectedPlayer)
    {
        if (Plug.HeldByPlayerId == 0 || Plug.HeldByPlayerId != disconnectedPlayer.PlayerId)
            return;

        Plug.HeldByPlayerId = 0;
        Plug.DropPlug();

        //cache packet
        var packet = new CommonPitStopPlugInteractionPacket
        {
            NetId = Plug.NetId,
            InteractionType = PlugInteractionType.Dropped,
        };

        SendToStation(packet, disconnectedPlayer);
    }

    #region The hose's rope physics (PropHoseRopePatch)

    /// <summary>The stretched hose pulled the plug out of the holder's hand, with this force.</summary>
    public static void YankedByRope(NetworkedPluggableObject plug, Vector3 force, ForceMode mode)
    {
        Multiplayer.LogDebug(() => $"ServerPluggableObject.YankedByRope() [{plug.transform.parent.name}, {plug.NetId}] station: {plug.Station?.StationName}, force: {force}");

        ApplyByRope(plug, new CommonPitStopPlugInteractionPacket
        {
            NetId = plug.NetId,
            InteractionType = PlugInteractionType.Yanked,
            Position = plug.transform.position - WorldMover.currentMove,
            Rotation = plug.transform.rotation,
            YankForce = force,
            YankMode = mode,
        });
    }

    /// <summary>The hose snapped the plug, held by nobody, home to its stand.</summary>
    public static void SnappedByRope(NetworkedPluggableObject plug)
    {
        Multiplayer.LogDebug(() => $"ServerPluggableObject.SnappedByRope() [{plug.transform.parent.name}, {plug.NetId}] station: {plug.Station?.StationName}");

        ApplyByRope(plug, new CommonPitStopPlugInteractionPacket
        {
            NetId = plug.NetId,
            InteractionType = PlugInteractionType.DockHome,
        });
    }

    // The plug's state changed in the server's world by itself: apply it here, then tell the players
    // at the station.
    private static void ApplyByRope(NetworkedPluggableObject plug, CommonPitStopPlugInteractionPacket packet)
    {
        //cancel any client events
        plug.CancelLocalInteraction();

        plug.HeldByPlayerId = 0;
        plug.TrainCarNetId = NetworkedPluggableObject.INVALID_NETID;
        plug.SocketIndex = NetworkedPluggableObject.INVALID_SOCKET;
        plug.IsConnecting = false;

        //Allow host to process packet
        plug.ProcessingAsHost = true;
        plug.ProcessPacket(packet);
        plug.ProcessingAsHost = false;

        if (plug.TryGetComponent(out ServerPluggableObject serverPlug) && serverPlug.Plug != null)
            serverPlug.SendToStation(packet, null);
    }

    #endregion

    // To the players at the station except one, and never the host's own player: its world is this one.
    private void SendToStation(CommonPitStopPlugInteractionPacket packet, ServerPlayer except)
    {
        if (station == null || station.CullingManager == null)
            return;

        foreach (var player in station.CullingManager.ActivePlayers)
        {
            if (player == except || player == Server.LocalPlayer)
                continue;
            Multiplayer.LogDebug(() => $"ServerPluggableObject.SendToStation() Sending interaction packet to player: {player.Username}");
            Server.SendPitStopPlugInteractionPacket(player, packet);
        }
    }
}
