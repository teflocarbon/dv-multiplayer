using DV.JObjectExtstensions;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Data.Player;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Transport;
using Multiplayer.Core.Identity;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Core.Shops;
using Multiplayer.Core.World;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Server.Session;

public class ServerPlayer : IDisposable
{
    public const byte MAX_CREW_NAME_LENGTH = 6;
    #region ID Management
    private static readonly NetIdAllocator<byte> idAllocator = new(value => unchecked((byte)(value + 1)));

    public void Dispose()
    {
        Multiplayer.LogDebug(() => $"Disposing ServerPlayer {Username} ({PlayerId})");
        if (PlayerId != 0)
        {
            idAllocator.Release(PlayerId);
            PlayerId = 0;
        }
    }
    #endregion

    public ITransportPeer Peer { get; private set; }
    public byte PlayerId { get; private set; }

    /// <summary>
    /// The sender of a dedicated host's console commands (it has no player of its own,
    /// docs/design/host-local-session.md, stage 3): not a player, never in the server's list, no id
    /// and no peer. Made by <see cref="CreateConsole"/> only.
    /// </summary>
    public bool IsConsole { get; private set; }

    /// <summary>A console sender (<see cref="IsConsole"/>) named <paramref name="username"/>.</summary>
    public static ServerPlayer CreateConsole(string username) => new(username);

    private ServerPlayer(string username)
    {
        IsConsole = true;
        LastLogin = DateTime.UtcNow;
        Username = username;
        OriginalUsername = username;
    }
    internal PlayerLoadingState LoadingState { get; set; } = PlayerLoadingState.None;
    /// <summary>When this player finished loading (Time.realtimeSinceStartup), or -1.</summary>
    internal float CompletedLoadingAt { get; set; } = -1f;
    /// <summary>
    /// When the first position after their load came (Time.realtimeSinceStartup), or -1: a position
    /// from before it can be from before their game put them at their saved spot (OI-JOIN-10).
    /// </summary>
    internal float FirstPositionAfterLoadAt { get; private set; } = -1f;
    // A game sends a position only when its player moves: one standing still since its load sends
    // none, and after this long its last position is where it is (K5).
    private const float StillAfterLoadSeconds = 60f;
    /// <summary>
    /// From when this player's position counts (Time.realtimeSinceStartup), or -1: their first position
    /// after their load, or with none a minute after the load.
    /// </summary>
    internal float PositionSettlesFrom => FirstPositionAfterLoadAt >= 0f ? FirstPositionAfterLoadAt
        : CompletedLoadingAt >= 0f ? CompletedLoadingAt + StillAfterLoadSeconds : -1f;
    public DateTime LastLogin { get; set; }
    private float PreviousPlayTime { get; set; }
    public float TotalPlaytime => PreviousPlayTime + (DateTime.UtcNow - LastLogin).Minutes;
    public string Username { get; set; }
    public string OriginalUsername { get; set; }
    // The player ID (PlayerIdentity): their Steam account's, once verified.
    public Guid Guid { get; set; }
    // The GUID of the install they play from, which their data was saved under before player IDs
    // were Steam accounts.
    public Guid LegacyGuid { get; set; }
    // Their verified Steam account, or 0 (the host's own player, or a host with -mp-allow-unverified).
    public ulong SteamId { get; set; }
    public string CharacterId { get; set; }
    public bool IsVR { get; }
    public uint LastHighPingTickLogged { get; set; }

    public PlayerTrackingData TrackingData { get; set; }
    public PlayerPostureFlags Posture { get; set; }        // already exists — keep
    public ushort CarId { get; set; }
    // The car whose frame TrackingData.Position is in (0: the world's). A packet names the car the
    // player is on now even when it carries no new position, and the last one is still in the frame
    // it was sent in: read in the new car's, a player who just boarded was hundreds of metres off
    // (OI-JOIN-11).
    private ushort positionCarId;

    /// <summary>A position packet: its changes merged in, the player's car, and the frame its position is in.</summary>
    internal void ApplyPosition(PlayerTrackingData delta, ushort carId)
    {
        TrackingData = TrackingData.MergeFrom(delta);
        CarId = carId;
        if (!delta.Position.HasValue)
            return;
        positionCarId = carId;
        if (LoadingState == PlayerLoadingState.Complete && FirstPositionAfterLoadAt < 0f)
            FirstPositionAfterLoadAt = Time.realtimeSinceStartup;
    }
    private string _crewName;
    public string CrewName
    {
        get
        {
            if (string.IsNullOrEmpty(_crewName))
                return string.Empty;
            return _crewName;
        }
        set
        {
            if (value != null)
            {
                if (value.Length > MAX_CREW_NAME_LENGTH)
                {
                    Multiplayer.LogWarning($"CrewName for player {Username} exceeds max length of {MAX_CREW_NAME_LENGTH}. Truncating.");
                    _crewName = value.Substring(0, MAX_CREW_NAME_LENGTH);
                }
                else
                {
                    _crewName = value;
                }
            }
            else
            {
                _crewName = string.Empty;
            }

            Dictionary<PlayerPreference, string> preferences = new()
            {
                { PlayerPreference.CrewName, _crewName }
            };

            NetworkServer.Instance.SendPlayerPreferencesUpdate(this, preferences);
        }
    }

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrEmpty(CrewName))
                return Username;
            return $"[{CrewName}] {Username}";
        }
    }

    public Dictionary<NetworkedItem, uint> KnownItems { get; private set; } = new Dictionary<NetworkedItem, uint>(); //NetworkedItem, last updated tick
    public Dictionary<NetworkedItem, float> NearbyItems { get; private set; } = new Dictionary<NetworkedItem, float>(); //NetworkedItem, time since near the item
    public HashSet<ushort> AcknowledgedWorldItems { get; } = new HashSet<ushort>();
    /// <summary>This player's recent item requests and their answers, so a repeat does nothing twice (NetworkServer.Items).</summary>
    public ItemRequestMemory<ClientboundItemResultPacket> ItemRequests { get; } = new();
    // Items this player failed to project: when the host may send their Create again.
    internal Dictionary<ushort, float> ProjectionRetryAt { get; } = new Dictionary<ushort, float>();
    // Reliable rolling-stock broadcasts held while this player's train baseline is in flight
    // (sent, not yet acknowledged), delivered in order once they acknowledge it.
    internal bool TrainBaselinePending { get; set; }
    internal List<(byte[] Data, LiteNetLib.DeliveryMethod Delivery)> PendingTrainTraffic { get; } = new();
    // The track graph this player reported with ReadyForWorldState, kept because a join that
    // waits for the host to load is replayed without the packet.
    internal TrackGraphFingerprint TrackGraph { get; set; }
    // Its shops, reported and kept the same way.
    internal ShopCatalogueFingerprint ShopCatalogue { get; set; }
    public bool WorldItemCatalogueAccepted { get; internal set; }

    private Vector3 _lastWorldPos = Vector3.zero;
    private Vector3 _lastAbsoluteWorldPosition = Vector3.zero;

    public ServerPlayer(ITransportPeer peer, string username, string originalUsername, Guid guid, string characterId, bool isVr)
    {
        if (!idAllocator.TryAllocate(out byte playerId))
            throw new InvalidOperationException("No multiplayer player IDs remain.");
        PlayerId = playerId;

        Peer = peer;
        LastLogin = DateTime.UtcNow;

        Username = username;
        OriginalUsername = originalUsername;
        Guid = guid;
        CharacterId = characterId;

        IsVR = isVr;

    }

    #region Positioning
    public Vector3 RawPosition => TrackingData.Position ?? Vector3.zero;
    public float RawRotationY => TrackingData.RotationY ?? 0f;

    public Vector3 AbsoluteWorldPosition
    {
        get
        {

            Vector3 pos;
            try
            {
                if (positionCarId == 0 || !NetworkedTrainCar.TryGet(positionCarId, out NetworkedTrainCar car))
                {
                    if (positionCarId != 0)
                        Multiplayer.LogDebug(() => $"AbsoluteWorldPosition() noID {Username}: CarId: {positionCarId}");

                    pos = RawPosition;
                }
                else
                {
                    //Multiplayer.LogDebug(() => $"AbsoluteWorldPosition() hasID {Username}: CarId: {CarId}");
                    pos = car.transform.TransformPoint(RawPosition) - WorldMover.currentMove; ;
                }

                _lastAbsoluteWorldPosition = pos;
            }
            catch (Exception e)
            {
                Multiplayer.LogWarning($"AbsoluteWorldPosition() Exception {Username}");
                Multiplayer.LogWarning(e.Message);
                Multiplayer.LogWarning(e.StackTrace);
                pos = _lastAbsoluteWorldPosition;
            }

            return pos;

        }
    }

    public Vector3 WorldPosition
    {
        get
        {
            Vector3 pos;
            try
            {
                if (positionCarId == 0 || !NetworkedTrainCar.TryGet(positionCarId, out NetworkedTrainCar car))
                {
                    if (positionCarId != 0)
                        Multiplayer.LogDebug(() => $"WorldPosition() noID {Username}: CarId: {positionCarId}");

                    pos = RawPosition + WorldMover.currentMove;
                }
                else
                {
                    //Multiplayer.LogDebug(() => $"WorldPosition() hasID {Username}: CarId: {CarId}");
                    pos = car.transform.TransformPoint(RawPosition);
                }

                _lastWorldPos = pos;
            }
            catch (Exception e)
            {
                Multiplayer.LogWarning($"WorldPosition() Exception {Username}");
                Multiplayer.LogWarning(e.Message);
                Multiplayer.LogWarning(e.StackTrace);

                pos = _lastWorldPos;
            }

            return pos;
        }
    }

    public float WorldRotationY => CarId == 0 || !NetworkedTrainCar.TryGet(CarId, out NetworkedTrainCar car)
        ? RawRotationY
        : (Quaternion.Euler(0, RawRotationY, 0) * car.transform.rotation).eulerAngles.y;
    #endregion


    public override string ToString()
    {
        return $"{PlayerId} ({Username}, {Guid.ToString()})";
    }
}
