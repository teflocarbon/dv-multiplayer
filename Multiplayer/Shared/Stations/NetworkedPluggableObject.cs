using DV.CabControls;
using DV.Interaction;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Shared.Stations;

public class NetworkedPluggableObject : IdMonoBehaviour<ushort, NetworkedPluggableObject>
{
    public const sbyte INVALID_SOCKET = -1;
    public const ushort INVALID_NETID = 0;

    #region Lookup Cache
    private static readonly Dictionary<NetworkedPluggableObject, NetworkedPitStopStation> plugToStation = [];
    private static readonly Dictionary<PluggableObject, NetworkedPluggableObject> plugToNetworkedPluggable = [];

    public static bool Get(ushort netId, out NetworkedPluggableObject obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedPluggableObject> rawObj);
        obj = (NetworkedPluggableObject)rawObj;
        return b;
    }

    public static bool Get(PluggableObject pluggableObject, out NetworkedPluggableObject obj)
    {
        bool b = plugToNetworkedPluggable.TryGetValue(pluggableObject, out obj);
        return b;
    }
    #endregion

    protected override bool IsIdServerAuthoritative => false;

    #region Server Variables
    /// <summary>
    /// The server's record of the player holding this plug, 0 for nobody. Only the server keeps it
    /// (<c>Server/Stations/ServerPluggableObject</c>); a client knows the holder as <see cref="playerHolding"/>.
    /// </summary>
    public byte HeldByPlayerId { get; set; }
    #endregion

    #region Common Variables
    public PluggableObject PluggableObject { get; private set; }
    public Rigidbody PlugRB { get; private set; }
    public PropHose Hose { get; private set; }
    public NetworkedPitStopStation Station { get; private set; }
    public bool IsConnecting { get; set; } = false;

    public bool IsHeld => playerHolding != 0 || HeldByPlayerId != 0 || PluggableObject.controlGrabbed;

    private CustomNonVrGrabAnchor nonVrGrabAnchor;

    private bool handlersInitialised = false;

    public ushort TrainCarNetId { get; set; } = INVALID_NETID;  //initialise to invalid TrainCar
    public sbyte SocketIndex { get; set; } = INVALID_SOCKET;    //initialise to invalid socket

    private PlugInteractionType currentInteraction = PlugInteractionType.Rejected;

    /// <summary>
    /// Set by the server while it applies another player's interaction here, so this game doesn't
    /// report it back; cleared once applied.
    /// </summary>
    public bool ProcessingAsHost { get; set; } = false;

    /// <summary>
    /// Set on a client by its part (<c>Client/Stations/ClientPluggableObject</c>) until the server's
    /// state of the plug has arrived and been applied: the local player's interactions with it
    /// aren't reported before then.
    /// </summary>
    public bool WaitingForServerState { get; set; } = false;
    #endregion

    #region Client Variables
    private byte playerHolding;
    // Our own player picked this plug up, so its next drop or dock is ours to report. Vanilla's
    // PluggedIn also fires when we replay the host's state (the snap into the stand after a bulk
    // update finishes after the server's state is applied), and reporting that made the host refuse a plug
    // nobody held, every time a player came into a service station's range.
    private bool grabbedLocally;
    #endregion

    #region Unity
    protected override void Awake()
    {
        if (NetId == INVALID_NETID)
            base.Awake();

        PluggableObject = GetComponent<PluggableObject>();
        Hose = transform.parent.GetComponentInChildren<PropHose>();
        PlugRB = PluggableObject.GetComponent<Rigidbody>();


        //Multiplayer.LogDebug(() => $"NetworkedPluggableObject.Awake() {PluggableObject?.controlBase?.spec?.name}, {transform.parent.name}");
        Multiplayer.LogDebug(() => $"NetworkedPluggableObject.Awake() {this.GetObjectPath()}, netId: {NetId}, PluggableObject found: {PluggableObject != null}, RB Found: {PlugRB != null}, Hose found: {Hose != null}");
    }

    protected IEnumerator Start()
    {
        //Multiplayer.LogDebug(() => $"NetworkedPluggableObject.Start() {PluggableObject?.controlBase?.spec?.name}, {transform.parent.name}");
        yield return new WaitUntil(() => PluggableObject?.controlBase != null);

        //Multiplayer.LogDebug(() => $"NetworkedPluggableObject.Start() Controlbase {PluggableObject?.controlBase?.spec?.name}, {transform.parent.name}");

        nonVrGrabAnchor = this.GetComponent<CustomNonVrGrabAnchor>();

        PluggableObject.controlBase.Grabbed += OnGrabbed;
        PluggableObject.controlBase.Ungrabbed += OnUngrabbed;
        PluggableObject.PluggedIn += OnPluggedIn;

        handlersInitialised = true;
    }

    protected override void OnDestroy()
    {
        if (UnloadWatcher.isUnloading)
        {
            plugToStation.Clear();
            plugToNetworkedPluggable.Clear();
        }
        else
        {
            plugToStation.Remove(this);
            plugToNetworkedPluggable.Remove(PluggableObject);
        }

        if (PluggableObject?.controlBase != null && handlersInitialised)
        {
            PluggableObject.controlBase.Grabbed -= OnGrabbed;
            PluggableObject.controlBase.Ungrabbed -= OnUngrabbed;
            PluggableObject.PluggedIn -= OnPluggedIn;
        }

        base.OnDestroy();
    }

    protected void LateUpdate()
    {
        if (currentInteraction == PlugInteractionType.Rejected)
            return;

        Multiplayer.LogDebug(() => $"NetworkedPluggableObject.LateUpdate()station: {Station?.StationName}, processing: {NetworkLifecycle.Instance.IsProcessingPacket}, processing as Host: {ProcessingAsHost}, waiting: {WaitingForServerState}, isConnecting: {IsConnecting}");
        if (!ProcessingAsHost)
        {
            StationsClient.Current?.SendPitStopPlugInteractionPacket(NetId, currentInteraction, transform.position - WorldMover.currentMove, transform.rotation, TrainCarNetId, SocketIndex);
        }
        else
        {
            //this will only trigger when there's a valid state to be sent (current interaction is not rejected)
            //and the host is processing a packet
            //this should be the end of the processing, even for docking plugs, so we can clear the connecting and processing flags
            IsConnecting = false;
            ProcessingAsHost = false;
        }

        currentInteraction = PlugInteractionType.Rejected;
    }
    #endregion

    #region Common

    /// <summary>
    /// Drops the local player's interaction waiting to be reported this frame: the server's hose
    /// physics moved the plug (<c>Server/Stations/ServerPluggableObject</c>).
    /// </summary>
    internal void CancelLocalInteraction() => currentInteraction = PlugInteractionType.Rejected;

    public void ProcessPacket(CommonPitStopPlugInteractionPacket packet)
    {
        ProcessInteraction(packet.InteractionType, packet.PlayerId, packet.TrainCarNetId, packet.SocketIndex, packet.Position, packet.Rotation, packet.YankForce, packet.YankMode);
    }

    public void ProcessInteraction(PlugInteractionType interaction, byte playerId, ushort trainNetId, sbyte socketIndex, Vector3? newPosition, Quaternion? newRotation, Vector3? yankForce = null, ForceMode yankMode = ForceMode.Impulse)
    {
        bool result;
        Multiplayer.LogDebug(() => $"NetworkedPluggableObject.ProcessInteraction({interaction}, {playerId}, {trainNetId}, {socketIndex}, {newPosition?.ToString()}, {newRotation?.ToString()}, {yankForce}, {yankMode}) netId: {NetId}");

        // State from the network (another player's action, a yank, a refusal of ours, a bulk
        // update) supersedes our own grab: what happens to the plug next isn't ours to report.
        grabbedLocally = false;

        switch (interaction)
        {
            case PlugInteractionType.Rejected:
                //todo implement rejection
                break;

            case PlugInteractionType.PickedUp:
                //Handle the picked up state
                Multiplayer.LogDebug(() => $"ProcessPacket() NetId: {NetId}, Picked Up, player: {playerHolding}");

                GrabPlug(playerId);

                break;

            case PlugInteractionType.Dropped:
                Multiplayer.LogDebug(() => $"ProcessPacket() NetId: {NetId}, Dropped");

                DropPlug();

                if (newPosition == null || newRotation == null)
                    return;

                transform.position = (Vector3)newPosition + WorldMover.currentMove;
                transform.rotation = (Quaternion)newRotation;

                break;

            case PlugInteractionType.Yanked:
                Multiplayer.LogDebug(() => $"ProcessPacket() NetId: {NetId}, Yanked");

                DropPlug();

                if (newPosition != null || newRotation != null)
                {
                    transform.position = (Vector3)newPosition + WorldMover.currentMove;
                    transform.rotation = (Quaternion)newRotation;
                }

                PlugRB?.AddForce((Vector3)yankForce, yankMode);

                CoroutineManager.Instance.StartCoroutine(WaitForYankSettle());

                break;

            case PlugInteractionType.DockHome:
                Multiplayer.LogDebug(() => $"ProcessPacket() NetId: {NetId}, DockHome");

                DropPlug();

                //result = PluggableObject.InstantSnapTo(PluggableObject.startAttachedTo);
                result = PluggableObject.StartSnappingTo(PluggableObject.startAttachedTo, true);
                Multiplayer.LogDebug(() => $"ProcessPacket() NetId: {NetId}, DockHome, result: {result}");
                break;

            case PlugInteractionType.DockSocket:
                Multiplayer.LogDebug(() => $"ProcessPacket() NetId: {NetId}, DockSocket, trainCar: {trainNetId}, isLeft: {socketIndex}");

                DockTrainCar(trainNetId, socketIndex);
                break;
        }
    }

    private void BlockInteraction(bool block)
    {
        Multiplayer.LogDebug(() => $"BlockInteraction({block})");
        if (block)
        {
            PluggableObject.DisableStandaloneComponents();
            PluggableObject.DisableColliders();
        }
        else
        {
            PluggableObject.EnableStandaloneComponents();
            PluggableObject.EnableColliders();
        }
    }

    public void InitPitStop(NetworkedPitStopStation netPitStop)
    {
        if (NetId == 0)
            base.Awake();

        if (plugToStation.TryGetValue(this, out _))
        {
            Multiplayer.LogWarning($"Lookup cache 'plugToStation' already contains NetworkedPitStopStation \"{netPitStop?.StationName}\", skipping Init");
            return;
        }

        Station = netPitStop;
        plugToStation.Add(this, netPitStop);

        if (PluggableObject == null)
            PluggableObject = GetComponent<PluggableObject>();

        if (PluggableObject != null)
            plugToNetworkedPluggable.Add(PluggableObject, this);
    }

    public void DropPlug()
    {
        if (playerHolding != 0)
        {
            if (NetworkLifecycle.Instance.Avatars != null &&
            NetworkLifecycle.Instance.Avatars.TryGetPlayer(playerHolding, out var player))
            {
                player.DropItem();
            }
        }

        playerHolding = 0;
        PluggableObject.controlGrabbed = false;
        BlockInteraction(false);

        PluggableObject.Unplug();
        PluggableObject.controlBase?.ForceEndInteraction();

        TrainCarNetId = INVALID_NETID;
        SocketIndex = INVALID_SOCKET;
    }

    public void GrabPlug(byte playerId)
    {
        playerHolding = playerId;
        PluggableObject.controlGrabbed = true;
        BlockInteraction(true);

        PluggableObject.Unplug();

        TrainCarNetId = INVALID_NETID;
        SocketIndex = INVALID_SOCKET;

        //attach to a player
        if (NetworkLifecycle.Instance.Avatars != null &&
            NetworkLifecycle.Instance.Avatars.TryGetPlayer(playerHolding, out var player))
        {
            var target = nonVrGrabAnchor?.GetGrabAnchor();
            Multiplayer.LogDebug(() => $"GrabPlug() NetId: {NetId}, player: {player.Username}, targetPos: {target?.localPos}, targetRot: {target?.localRot}");
            player.HoldItem(gameObject, target?.localPos, target?.localRot);
        }
    }

    private void DockTrainCar(ushort trainNetId, sbyte socketIndex)
    {
        DropPlug();

        if (NetworkedTrainCar.TryGet(trainNetId, out NetworkedTrainCar netTrainCar))
        {
            var socket = GetTrainCarSocket(netTrainCar, socketIndex);
            if (socket == null)
            {
                Multiplayer.LogWarning($"Failed to dock plug in loco socket, socket not found! Plug NetId: {NetId}, TrainCar: [{netTrainCar.CurrentID}, {trainNetId}]");
                return;
            }

            //bool result = PluggableObject.InstantSnapTo(socket);
            bool result = PluggableObject.StartSnappingTo(socket, true);

            if (result)
            {
                TrainCarNetId = trainNetId;
                SocketIndex = socketIndex;
            }

            Multiplayer.LogDebug(() => $"ProcessPacket() NetId: {NetId}, DockSocket, trainCar: {trainNetId}, isLeft: {socketIndex}, result: {result}");
        }
        else
        {
            Multiplayer.LogDebug(() => $"ProcessPacket() NetId: {NetId}, DockSocket, trainCar: {trainNetId}. TrainCar not found!");
        }
    }

    public PlugSocket GetTrainCarSocket(NetworkedTrainCar netTrainCar, sbyte socketIndex)
    {
        if (netTrainCar == null || netTrainCar.TrainCar == null)
            return null;

        if (socketIndex < 0 || socketIndex >= netTrainCar.TrainCar.FuelSockets.Length)
        {
            Multiplayer.LogWarning($"Failed to find socket {socketIndex} in TrainCar: [{netTrainCar.CurrentID}, {netTrainCar.NetId}], index is out of bounds!");
            return null;
        }

        return netTrainCar.TrainCar.FuelSockets[socketIndex];
    }

    private IEnumerator WaitForYankSettle()
    {
        Multiplayer.LogDebug(() => $"WaitForYankSettle() PluggableObject.yankOutOfHand: {PluggableObject.yankOutOfHand}, velocity: {PlugRB.velocity.sqrMagnitude}");
        PluggableObject.yankOutOfHand = false; //block docking

        //allow force to be applied
        yield return new WaitForFixedUpdate();
        Multiplayer.LogDebug(() => $"WaitForYankSettle() post-WaitForFixed, PluggableObject.yankOutOfHand: {PluggableObject.yankOutOfHand}, velocity: {PlugRB.velocity.sqrMagnitude}");

        float time = Time.time;

        //wait for rigid body to come to rest
        yield return new WaitUntil(() => Mathf.Approximately(PlugRB.velocity.sqrMagnitude, 0.0f) || (Time.time - time > 2.0f));

        Multiplayer.LogDebug(() => $"WaitForYankSettle() PluggableObject.yankOutOfHand: {PluggableObject.yankOutOfHand}, velocity: {PlugRB.velocity.sqrMagnitude}, delta Time: {Time.time - time}");

        //wait for plug to come to rest (prevent docking home)
        yield return new WaitForFixedUpdate();

        PluggableObject.yankOutOfHand = true; //unblock docking
    }
    #endregion

    #region Client
    private void OnGrabbed(ControlImplBase control)
    {
        Multiplayer.LogDebug(() => $"NetworkedPluggableObject.OnGrabbed() station: {Station?.StationName}, processing: {NetworkLifecycle.Instance.IsProcessingPacket}, processing as Host: {ProcessingAsHost}, waiting: {WaitingForServerState}, isConnecting: {IsConnecting}");

        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;

        //Multiplayer.LogDebug(() => $"NetworkedPluggableObject.OnGrabbed() post [{transform.parent.name}, {NetId}] station: {Station?.StationName}");

        //Prevent new players/players entering the area from sending packets until initalised
        if (WaitingForServerState)
            return;

        TrainCarNetId = INVALID_NETID;
        SocketIndex = INVALID_SOCKET;

        // Our player has it now, so any snap before this is over. A snap we only replayed (another
        // player's dock) sets IsConnecting and never clears it, which would swallow our next drop.
        IsConnecting = false;
        grabbedLocally = true;
        currentInteraction = PlugInteractionType.PickedUp;
    }

    private void OnUngrabbed(ControlImplBase control)
    {
        Multiplayer.LogDebug(() => $"NetworkedPluggableObject.OnUngrabbed() station: {Station?.StationName}, processing: {NetworkLifecycle.Instance.IsProcessingPacket}, processing as Host: {ProcessingAsHost}, waiting: {WaitingForServerState}, isConnecting: {IsConnecting}");

        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;

        //Prevent new players/players entering the area from sending packets until initalised
        if (WaitingForServerState || !grabbedLocally)
            return;

        //Multiplayer.LogDebug(() => $"NetworkedPluggableObject.OnUngrabbed() station: {Station?.StationName}, plugging state: {PluggableObject.State}");

        // If we're snapping to a socket, don't send the Dropped packet; OnPluggedIn reports the dock
        if (IsConnecting)
            return;

        TrainCarNetId = INVALID_NETID;
        SocketIndex = INVALID_SOCKET;

        grabbedLocally = false;
        currentInteraction = PlugInteractionType.Dropped;
    }

    private void OnPluggedIn(PluggableObject plug, PlugSocket socket)
    {
        Multiplayer.LogDebug(() => $"NetworkedPluggableObject.OnPlugged() station: {Station?.StationName}, processing: {NetworkLifecycle.Instance.IsProcessingPacket}, processing as Host: {ProcessingAsHost}, waiting: {WaitingForServerState}, isConnecting: {IsConnecting}");

        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;

        //Prevent new players/players entering the area from sending packets until initalised
        if (WaitingForServerState)
            return;

        // Only a plug our player was holding was docked by us; anything else is the host's state
        // (or another player's action) finishing on our side.
        if (!grabbedLocally)
            return;
        grabbedLocally = false;

        Multiplayer.LogDebug(() => $"NetworkedPluggableObject.OnPlugged() [{transform.parent.name}, {NetId}] station: {Station?.StationName}");

        PlugInteractionType interaction;
        ushort carNetId = 0;

        if (socket == plug.startAttachedTo)
        {
            interaction = PlugInteractionType.DockHome;
            SocketIndex = INVALID_SOCKET;
        }
        else
        {
            var trainCar = TrainCar.Resolve(socket.gameObject);
            if (trainCar != null && trainCar.FuelSockets != null)
            {
                if (!NetworkedTrainCar.TryGetFromTrainCar(trainCar, out var netTrainCar))
                {
                    Multiplayer.LogDebug(() => $"NetworkedPluggableObject.OnPlugged() NetworkedTrainCar: {trainCar?.ID} Not Found! Socket: {socket.GetObjectPath()}");
                    return;
                }

                carNetId = netTrainCar.NetId;

                interaction = PlugInteractionType.DockSocket;
                SocketIndex = (sbyte)Array.IndexOf(trainCar.FuelSockets, socket);

                if (SocketIndex < 0)
                    Multiplayer.LogWarning($"Socket not recognised for TrainCar [{trainCar.ID}, {netTrainCar.NetId}], socket: {socket.GetObjectPath()}");
            }
            else
            {
                Multiplayer.LogDebug(() => $"NetworkedPluggableObject.OnPlugged() Socket not recognised: {socket.GetObjectPath()}");
                return;
            }
        }

        currentInteraction = interaction;
        TrainCarNetId = carNetId;
        IsConnecting = false;
    }
    #endregion
}
