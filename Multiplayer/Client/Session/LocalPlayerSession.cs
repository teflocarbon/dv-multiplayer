using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using Multiplayer.Client.Api;
using Multiplayer.Client.Shops;
using Multiplayer.Client.UI;
using Multiplayer.Shared.Players;
using Multiplayer.Shared.Protocol.Data.Player;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Protocol.Serialization;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Transport;

namespace Multiplayer.Client.Session;

/// <summary>
///     The host's own player as a session of its server in the same process: a host runs no
///     <see cref="NetworkClient"/> of its own (docs/design/host-local-session.md, stage 3). A dedicated
///     host has none.
///     <list type="bullet">
///         <item>Requests go to the server in-process (<see cref="Submit{T}"/>), never as packets.</item>
///         <item>What the server sends the host's player arrives through its <see cref="LocalPeer"/> and
///         is read here only if it's presentation: each area subscribes what its player needs
///         (<see cref="Handle{T}"/>, in <c>LocalPlayerSession.&lt;Area&gt;.cs</c>). Anything else is
///         dropped, so the host never follows its own state.</item>
///         <item>The other players' avatars are the server's to keep (<see cref="NetworkLifecycle.Avatars"/>).</item>
///     </list>
/// </summary>
public partial class LocalPlayerSession : ILocalPlayerSession, IPolledSession, IClientApiSession
{
    private readonly NetPacketProcessor processor = new();
    private readonly NetDataReader reader = new();
    private readonly HashSet<ulong> droppedPacketWarnings = new();
    private readonly bool isSinglePlayer;
    private string characterModelId;
    private ChatGUI chatGUI;

    /// <summary>The host's player's peer on its server: what the server sends it lands here.</summary>
    public LocalPeer Peer { get; } = new();

    public byte PlayerId { get; private set; }
    public string Username { get; private set; }
    public string CrewName { get; private set; }
    public string DisplayName => string.IsNullOrEmpty(CrewName) ? Username : $"[{CrewName}] {Username}";

    /// <summary>The host's player is on its server: no latency.</summary>
    public int Ping => 0;

    /// <summary>The server answers the host's player at its next poll: a floor, for a frame-rate drop.</summary>
    public float RpcTimeout => 2f;

    public bool IsProcessingPacket { get; private set; }

    public LocalPlayerSession(bool singlePlayer)
    {
        isSinglePlayer = singlePlayer;
        Username = Multiplayer.Settings.GetUserName();
        characterModelId = Multiplayer.Settings.CharacterId;
        PacketSerializationRegistry.Register(processor);

        Handle<ClientboundRemoveLoadingScreenPacket>(OnRemoveLoadingScreen);
        // Sent while the save is still loading: the host's own loading screen says so already.
        Handle<ClientboundServerLoadingPacket>(_ => { });
        HandleNetSerializable<ClientboundRpcResponsePacket>(OnRpcResponse);
        Handle<CommonChatPacket>(OnChat);
        Handle<ClientboundPlayerPreferencesUpdatePacket>(OnPlayerPreferences);
        RegisterTrains();
        RegisterItems();
        RegisterWorld();
        RegisterStations();
        RegisterShops();
        RegisterJobs();
        RegisterCareer();
        RegisterPlayers();
        RegisterGadgets();

        Settings.OnSettingsUpdated += OnSettingsUpdated;
    }

    // Each area subscribes the presentation packets its host player needs in its own file.
    partial void RegisterTrains();
    partial void RegisterItems();
    partial void RegisterWorld();
    partial void RegisterStations();
    partial void RegisterShops();
    partial void RegisterJobs();
    partial void RegisterCareer();
    partial void RegisterPlayers();
    partial void RegisterGadgets();

    /// <summary>The server admitted the host's player (<c>NetworkServer.AddLocalPlayer</c>).</summary>
    public void OnAdmitted(byte playerId, string username)
    {
        PlayerId = playerId;
        Username = username;
    }

    public void Stop()
    {
        Settings.OnSettingsUpdated -= OnSettingsUpdated;
        ShopRegisterView.Reset();
        ShopScannerView.Reset();
        ShopRegisterPatches.Reset();
        Peer.Close();
    }

    /// <summary>Subscribes a packet the server sends the host's player, as a client handles it.</summary>
    private void Handle<T>(Action<T> onReceive) where T : class, new() =>
        processor.SubscribeReusable(onReceive);

    private void HandleNetSerializable<T>(Action<T> onReceive) where T : INetSerializable, new() =>
        processor.SubscribeNetSerializable(onReceive);

    /// <summary>
    ///     Hands one of the host's player's requests to the server, which handles it at its next poll
    ///     as from this player. Every request the host makes must be handled in-process: there's no
    ///     connection to fall back on.
    /// </summary>
    public bool Submit<T>(T request) where T : class
    {
        if (NetworkLifecycle.Instance.LocalServer?.Submit(request) == true)
            return true;
        Multiplayer.LogError($"[LocalPlayer] The server doesn't handle {typeof(T).Name} in-process, so the host's request was dropped");
        return false;
    }

    public void PollEvents()
    {
        while (Peer.TryReceive(out byte[] data))
        {
            reader.SetSource(data);
            IsProcessingPacket = true;
            try
            {
                processor.ReadAllPackets(reader);
            }
            catch (ParseException)
            {
                // The packet's type hash leads it.
                Drop(data.Length >= sizeof(ulong) ? BitConverter.ToUInt64(data, 0) : 0);
            }
            catch (Exception e)
            {
                Multiplayer.LogError($"[LocalPlayer] Packet handler threw: {e}");
            }
            finally
            {
                IsProcessingPacket = false;
            }
        }
    }

    // A packet type no area presents: the server's state for its other players, never applied here.
    // Its own method so a probe can count them (host-throw-settles).
    private void Drop(ulong hash)
    {
        if (droppedPacketWarnings.Add(hash))
            Multiplayer.LogWarning($"[LocalPlayer] Dropped {PacketNames.Describe(hash)} sent to the host's own player " +
                                   "(further ones are dropped silently): it presents nothing for it");
    }

    private void OnRemoveLoadingScreen(ClientboundRemoveLoadingScreenPacket packet)
    {
        Multiplayer.Log("[LocalPlayer] The host's player has finished loading, removing loading screen");
        LoadingScreen.Finish();
        chatGUI = ChatGUI.Create(isSinglePlayer);
    }

    private void OnRpcResponse(ClientboundRpcResponsePacket packet) =>
        RpcManager.Instance.ResolveTicket(packet.TicketId, packet.Response);

    private void OnChat(CommonChatPacket packet) => chatGUI?.ReceiveMessage(packet.message);

    // The other players' preferences reach their avatars from the server; only the host's own crew
    // name is the session's.
    private void OnPlayerPreferences(ClientboundPlayerPreferencesUpdatePacket packet)
    {
        if (packet.PlayerId == PlayerId &&
            packet.GetPreferencesDictionary().TryGetValue(PlayerPreference.CrewName, out string crewName))
            CrewName = crewName;
    }

    private void OnSettingsUpdated(Settings settings)
    {
        if (settings.CharacterId == characterModelId)
            return;
        characterModelId = settings.CharacterId;
        Dictionary<PlayerPreference, string> preferences = new() { [PlayerPreference.CharacterModel] = characterModelId };
        Submit(new ServerboundPlayerPreferenceUpdatePacket
        {
            PreferenceKeys = Array.ConvertAll(preferences.Keys.ToArray(), item => (byte)item),
            PreferenceValues = preferences.Values.ToArray()
        });
    }

    public void SendChat(string message) => Submit(new CommonChatPacket { message = message });

    #region The other players' avatars (the server keeps them)

    private static PlayerAvatars Avatars => NetworkLifecycle.Instance.Avatars;

    public IReadOnlyCollection<NetworkedPlayer> RemotePlayers => Avatars.Players;

    public bool TryGetRemotePlayer(byte playerId, out NetworkedPlayer player) => Avatars.TryGetPlayer(playerId, out player);

    public event Action<NetworkedPlayer> RemotePlayerConnected
    {
        add => Avatars.OnPlayerConnected += value;
        remove => Avatars.OnPlayerConnected -= value;
    }

    public event Action<NetworkedPlayer> RemotePlayerDisconnected
    {
        add => Avatars.OnPlayerDisconnected += value;
        remove => Avatars.OnPlayerDisconnected -= value;
    }

    public event Action<NetworkedPlayer> RemotePlayerPrefsUpdated
    {
        add => Avatars.OnPlayerPrefsUpdated += value;
        remove => Avatars.OnPlayerPrefsUpdated -= value;
    }

    #endregion
}
