using DV.JObjectExtstensions;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Player;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Transport;
using Multiplayer.Core.Identity;
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
    private static readonly NetIdAllocator<byte> idAllocator = CreateIdAllocator();

    // The owner number of an owner who isn't connected is never a player's.
    private static NetIdAllocator<byte> CreateIdAllocator()
    {
        NetIdAllocator<byte> allocator = new(value => unchecked((byte)(value + 1)));
        allocator.TryReserve(ItemTransitionCommand.AbsentOwnerId);
        return allocator;
    }

    public void Dispose()
    {
        Multiplayer.LogDebug(() => $"Disposing ServerPlayer {Username} ({PlayerId})");
        if (PlayerId != 0)
        {
            AuthoritativeItemRegistry.UnbindPersistentOwner(this);
            NetworkedLostAndFoundManager.OnOwnerDisconnected(this);
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
        Inventory = new StorageBase
        {
            storageType = DV.ThingTypes.StorageType.Inventory
        };
    }
    internal PlayerLoadingState LoadingState { get; set; } = PlayerLoadingState.None;
    /// <summary>When this player finished loading (Time.realtimeSinceStartup), or -1.</summary>
    internal float CompletedLoadingAt { get; set; } = -1f;
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
    public HashSet<ushort> OwnedItems { get; private set; } = new HashSet<ushort>();
    public HashSet<ushort> AcknowledgedWorldItems { get; } = new HashSet<ushort>();
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
    public StorageBase Inventory { get; private set; }

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

        Inventory = new StorageBase
        {
            storageType = DV.ThingTypes.StorageType.Inventory
        };
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
                if (CarId == 0 || !NetworkedTrainCar.TryGet(CarId, out NetworkedTrainCar car))
                {
                    if (CarId != 0)
                        Multiplayer.LogDebug(() => $"AbsoluteWorldPosition() noID {Username}: CarId: {CarId}");

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
                if (CarId == 0 || !NetworkedTrainCar.TryGet(CarId, out NetworkedTrainCar car))
                {
                    if (CarId != 0)
                        Multiplayer.LogDebug(() => $"WorldPosition() noID {Username}: CarId: {CarId}");

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

    #region Item Ownership
    public bool OwnsItem(ushort itemNetId) => OwnedItems.Contains(itemNetId);

    public void AddOwnedItem(ushort itemNetId)
    {
        OwnedItems.Add(itemNetId);
        NetworkServer.Instance.LogDebug(() => $"Player {Username} now owns item {itemNetId}");
    }

    public void AddOwnedItems(IEnumerable<ushort> itemNetIds)
    {
        OwnedItems.UnionWith(itemNetIds);
        NetworkServer.Instance.LogDebug(() => $"Player {Username} batch added items: {string.Join(", ", itemNetIds)}");
    }

    public void RemoveOwnedItem(ushort itemNetId)
    {
        if (OwnedItems.Remove(itemNetId))
        {
            NetworkServer.Instance.LogDebug(() => $"Player {Username} no longer owns item {itemNetId}");
        }
    }

    public void ClearOwnedItems()
    {
        OwnedItems.Clear();
        NetworkServer.Instance.LogDebug(() => $"Cleared all owned items for player {Username}");
    }

    public bool TryGetOwnedItem(ushort itemNetId, out NetworkedItem item)
    {
        if (OwnedItems.Contains(itemNetId) && NetworkedItem.TryGet(itemNetId, out item))
        {
            return true;
        }
        item = null;
        return false;
    }
    #endregion

    public override string ToString()
    {
        return $"{PlayerId} ({Username}, {Guid.ToString()})";
    }
}
