using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Multiplayer.Shared.Transport.Gns;

/// <summary>
///     The Steam transport's behaviour on Valve's open-source GameNetworkingSockets, for test pairs
///     that run without a Steam client (-dvmp-transport gns; docs/testing.md, "GNS pairs"). Debug
///     builds only: Multiplayer.csproj compiles this folder out of every other configuration, so
///     a release can't run without Steam.
///
///     It mirrors SteamWorksTransport call for call, and Facepunch's SocketManager and
///     ConnectionManager beneath it: the same delivery mapping (SteamWorksTransport.ToSendType),
///     the same connection state handling, receive loops, socket config and latency polling.
///     GNS is the code Steam's sockets are built from, so reliable messages share one ordered
///     stream, messages may be up to 512 KB, and unreliable ones are fragmented, as on Steam.
///     There is no relay: a host listens on its IP port only, and a SteamID join is refused.
/// </summary>
public class GnsTransport : ITransport
{
    public NetStatistics Statistics => new();
    public bool IsRunning { get; private set; }

    public event Action<NetDataReader, IConnectionRequest> OnConnectionRequest;
    public event Action<ITransportPeer> OnPeerConnected;
    public event Action<ITransportPeer, DisconnectReason> OnPeerDisconnected;
    public event Action<ITransportPeer, NetDataReader, byte, DeliveryMethod> OnNetworkReceive;
    public event Action<IPEndPoint, SocketError> OnNetworkError;
    public event Action<ITransportPeer, int> OnNetworkLatencyUpdate;

    // GNS has one status callback per process; it reaches the transport that owns the connection,
    // or for a new incoming one, the listen socket it arrived on.
    private static readonly Dictionary<uint, GnsTransport> transportByConnection = [];
    private static readonly Dictionary<uint, GnsTransport> transportByListenSocket = [];
    // GNS logs from its own service thread; the main thread writes it out at the next poll.
    private static readonly ConcurrentQueue<string> debugMessages = new();

    private readonly List<Server> servers = [];
    private readonly Dictionary<uint, Server> serverByConnection = [];
    private Client client;

    private readonly Dictionary<int, GnsPeer> peerIdToPeer = [];
    private readonly Dictionary<uint, GnsPeer> connectionToPeer = [];
    private const float LatencyUpdateIntervalSeconds = 1f;
    private float nextLatencyUpdateAt;
    private readonly IntPtr realTimeStatus = Marshal.AllocHGlobal(GnsNative.RealTimeStatusSize);
    private readonly IntPtr[] messages = new IntPtr[ReceiveBatch];
    private const int ReceiveBatch = 32;

    private int nextPeerId = 1;

    /// <summary>A listen socket and its poll group (Facepunch's SocketManager).</summary>
    private class Server
    {
        public uint Socket;
        public uint PollGroup;
        public readonly HashSet<uint> Connecting = [];
        public readonly HashSet<uint> Connected = [];
    }

    /// <summary>An outgoing connection (Facepunch's ConnectionManager).</summary>
    private class Client
    {
        public uint Connection;
        public bool Connecting = true;
        public bool Connected;
        public NetDataWriter LoginPacket;
        public GnsPeer Peer;
    }

    public GnsTransport()
    {
        GnsNative.Init(OnStatusChanged, OnDebugOutput);
    }

    #region ITransport
    public bool Start()
    {
        Multiplayer.LogDebug(() => "GnsTransport.Start()");
        return true;
    }

    public bool Start(int port)
    {
        Multiplayer.LogDebug(() => $"GnsTransport.Start({port})");

        var address = GnsNative.IPAddr.Any((ushort)port);
        uint socket = GnsNative.SteamAPI_ISteamNetworkingSockets_CreateListenSocketIP(GnsNative.Sockets, ref address, 0, IntPtr.Zero);
        if (socket != 0)
        {
            servers.Add(new Server
            {
                Socket = socket,
                PollGroup = GnsNative.SteamAPI_ISteamNetworkingSockets_CreatePollGroup(GnsNative.Sockets)
            });
            transportByListenSocket[socket] = this;
            IsRunning = true;
            Multiplayer.Log($"GnsTransport listening on UDP port {port} (no Steam relay)");
        }
        else
        {
            Multiplayer.LogError($"GnsTransport couldn't listen on UDP port {port}");
        }

        return IsRunning;
    }

    public bool Start(IPAddress ipv4, IPAddress ipv6, int port)
    {
        Multiplayer.LogDebug(() => $"GnsTransport.Start({ipv4}, {ipv6}, {port})");
        return Start(port);
    }

    public void Stop(bool sendDisconnectPackets)
    {
        Multiplayer.LogDebug(() => "GnsTransport.Stop()");
        if (GnsNative.Sockets == IntPtr.Zero)
            return; // GNS already stopped: the game is quitting

        if (client != null)
        {
            GnsNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(GnsNative.Sockets, client.Connection, 0, "Closing Connection", true);
            transportByConnection.Remove(client.Connection);
        }

        foreach (var server in servers)
        {
            foreach (uint connection in server.Connected)
            {
                GnsNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(GnsNative.Sockets, connection, (int)NetConnectionEnd.App_Generic, "Closing Connection", true);
                transportByConnection.Remove(connection);
            }
            foreach (uint connection in server.Connecting)
                transportByConnection.Remove(connection);

            GnsNative.SteamAPI_ISteamNetworkingSockets_DestroyPollGroup(GnsNative.Sockets, server.PollGroup);
            GnsNative.SteamAPI_ISteamNetworkingSockets_CloseListenSocket(GnsNative.Sockets, server.Socket);
            transportByListenSocket.Remove(server.Socket);
        }

        servers.Clear();
        serverByConnection.Clear();
    }

    public void PollEvents()
    {
        if (GnsNative.Sockets == IntPtr.Zero)
            return;
        while (debugMessages.TryDequeue(out string message))
            Multiplayer.Log($"GNS: {message}");

        GnsNative.SteamAPI_ISteamNetworkingSockets_RunCallbacks(GnsNative.Sockets);

        if (client != null)
            ReceiveClient();

        foreach (var server in servers)
            ReceiveServer(server);

        // About once a second, as SteamWorksTransport does.
        if (Time.unscaledTime < nextLatencyUpdateAt)
            return;
        nextLatencyUpdateAt = Time.unscaledTime + LatencyUpdateIntervalSeconds;
        foreach (var kvp in connectionToPeer)
        {
            if (kvp.Value != null)
                OnNetworkLatencyUpdate?.Invoke(kvp.Value, Ping(kvp.Key)); // round trip
        }
    }

    public ITransportPeer Connect(string address, int port, NetDataWriter data)
    {
        if (port < 0)
            return ConnectRelay(address, data);
        else
            return ConnectNative(address, port, data);
    }

    public ITransportPeer ConnectNative(string address, int port, NetDataWriter data)
    {
        Multiplayer.LogDebug(() => $"GnsTransport.ConnectNative({address}, {port}, {data.Length})");

        var add = GnsNative.IPAddr.From(IPAddress.Parse(address), (ushort)port);
        uint connection = GnsNative.SteamAPI_ISteamNetworkingSockets_ConnectByIPAddress(GnsNative.Sockets, ref add, 0, IntPtr.Zero);

        client = new Client { Connection = connection, LoginPacket = data };
        client.Peer = CreatePeer(connection);
        transportByConnection[connection] = this;

        return client.Peer;
    }

    /// <summary>A Steam relay join, by SteamID: GNS has no relay.</summary>
    public ITransportPeer ConnectRelay(string steamID, NetDataWriter data)
    {
        Multiplayer.LogWarning($"GnsTransport can't join {steamID} through the Steam relay; join by IP address instead");
        return null;
    }

    public void Send(ITransportPeer peer, NetDataWriter writer, DeliveryMethod deliveryMethod)
    {
        peer.Send(writer, deliveryMethod);
    }

    /// <summary>SteamWorksTransport.UpdateSettings' values, set the way Facepunch sets them.</summary>
    public void UpdateSettings(Settings settings)
    {
        IntPtr utils = GnsNative.Utils;
        if (utils == IntPtr.Zero)
            return;

        float chance = 0f;
        if (settings.SimulatePacketLoss)
            chance = settings.SimulationPacketLossChance;

        GnsNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueFloat(utils, GnsNative.ConfigFakePacketLossRecv, chance);
        GnsNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueFloat(utils, GnsNative.ConfigFakePacketLossSend, chance);

        chance = 0;
        if (settings.SimulateLatency)
            chance = UnityEngine.Random.Range(settings.SimulationMinLatency, settings.SimulationMaxLatency);

        // Facepunch sets the lag as a float though it is an int32, which GNS rounds; do the same.
        GnsNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueFloat(utils, GnsNative.ConfigFakePacketLagRecv, chance);
        GnsNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueFloat(utils, GnsNative.ConfigFakePacketLagSend, chance);

        GnsNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(utils, GnsNative.ConfigSendBufferSize, 16 * 1024 * 1024);
        GnsNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(utils, GnsNative.ConfigSendRateMin, 256 * 1024);
        GnsNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(utils, GnsNative.ConfigSendRateMax, 4 * 1024 * 1024);
        GnsNative.SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(utils, GnsNative.ConfigTimeoutConnected, 20000);
    }
    #endregion

    #region Connection state
    private static void OnStatusChanged(IntPtr status)
    {
        uint connection = (uint)Marshal.ReadInt32(status, GnsNative.StatusConnection);
        uint listenSocket = (uint)Marshal.ReadInt32(status, GnsNative.StatusListenSocket);
        int state = Marshal.ReadInt32(status, GnsNative.StatusState);
        int endReason = Marshal.ReadInt32(status, GnsNative.StatusEndReason);

        try
        {
            if (!transportByConnection.TryGetValue(connection, out GnsTransport transport))
            {
                if (listenSocket == 0 || !transportByListenSocket.TryGetValue(listenSocket, out transport))
                    return;
                transportByConnection[connection] = transport;
            }

            if (transport.client != null && transport.client.Connection == connection)
                transport.OnClientStatusChanged(state, endReason);
            else
                transport.OnServerStatusChanged(connection, listenSocket, state, endReason);
        }
        catch (Exception e)
        {
            // Never let an exception unwind into GNS.
            Multiplayer.LogError($"GnsTransport: connection {connection} state {state} handler threw: {e}");
        }
    }

    private static void OnDebugOutput(int type, IntPtr message)
    {
        debugMessages.Enqueue(Marshal.PtrToStringAnsi(message));
    }

    // Facepunch's ConnectionManager.OnConnectionChanged, with SteamClientManager's overrides.
    private void OnClientStatusChanged(int state, int endReason)
    {
        switch (state)
        {
            case GnsNative.StateConnecting:
                if (!client.Connecting && !client.Connected)
                    client.Connecting = true;
                break;
            case GnsNative.StateConnected:
                if (client.Connecting && !client.Connected)
                {
                    client.Connecting = false;
                    client.Connected = true;
                    Multiplayer.LogDebug(() => "GnsTransport client connected");
                    IsRunning = true;
                    client.Peer.Send(client.LoginPacket, DeliveryMethod.ReliableUnordered);
                    OnPeerConnected?.Invoke(client.Peer);
                }
                break;
            case GnsNative.StateNone:
            case GnsNative.StateClosedByPeer:
            case GnsNative.StateProblemDetectedLocally:
                if (client.Connecting || client.Connected)
                {
                    client.Connecting = false;
                    client.Connected = false;
                    Multiplayer.LogDebug(() => $"GnsTransport client disconnected ({(NetConnectionEnd)endReason})");
                    OnPeerDisconnected?.Invoke(client.Peer, SteamWorksTransport.NetConnectionEndToDisconnectReason((NetConnectionEnd)endReason));
                }
                break;
        }

        client.Peer?.OnConnectionStatusChanged(state);
    }

    // Facepunch's SocketManager.OnConnectionChanged, with SteamServerManager's overrides.
    private void OnServerStatusChanged(uint connection, uint listenSocket, int state, int endReason)
    {
        if (!serverByConnection.TryGetValue(connection, out Server server))
        {
            server = servers.Find(s => s.Socket == listenSocket);
            if (server == null)
                return;
            serverByConnection[connection] = server;
        }

        switch (state)
        {
            case GnsNative.StateConnecting:
                if (!server.Connecting.Contains(connection) && !server.Connected.Contains(connection))
                {
                    server.Connecting.Add(connection);
                    GnsNative.SteamAPI_ISteamNetworkingSockets_AcceptConnection(GnsNative.Sockets, connection);
                }
                break;
            case GnsNative.StateConnected:
                if (server.Connecting.Contains(connection) && !server.Connected.Contains(connection))
                {
                    server.Connecting.Remove(connection);
                    server.Connected.Add(connection);
                    GnsNative.SteamAPI_ISteamNetworkingSockets_SetConnectionPollGroup(GnsNative.Sockets, connection, server.PollGroup);

                    var peer = CreatePeer(connection);
                    peer.connectionRequest = new GnsConnectionRequest(connection, peer);
                    OnPeerConnected?.Invoke(peer);
                }
                break;
            case GnsNative.StateNone:
            case GnsNative.StateClosedByPeer:
            case GnsNative.StateProblemDetectedLocally:
                if (server.Connecting.Contains(connection) || server.Connected.Contains(connection))
                {
                    server.Connecting.Remove(connection);
                    server.Connected.Remove(connection);
                    GnsNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(GnsNative.Sockets, connection, 0, "Closing Connection", false);
                    serverByConnection.Remove(connection);
                    transportByConnection.Remove(connection);

                    var peer = GetPeer(connection);
                    if (peer == null)
                        return;

                    // Forget the connection, or its peer is polled for latency (and kept) forever.
                    connectionToPeer.Remove(connection);
                    peerIdToPeer.Remove(peer.Id);
                    OnPeerDisconnected?.Invoke(peer, SteamWorksTransport.NetConnectionEndToDisconnectReason((NetConnectionEnd)endReason));
                }
                break;
        }

        GetPeer(connection)?.OnConnectionStatusChanged(state);
    }
    #endregion

    #region Receive
    // Facepunch's ConnectionManager.Receive and SteamClientManager.OnMessage.
    private void ReceiveClient()
    {
        int count;
        do
        {
            count = GnsNative.SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection(GnsNative.Sockets, client.Connection, messages, ReceiveBatch);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var reader = ReadMessage(messages[i], out _, out int channel);
                    OnNetworkReceive?.Invoke(client.Peer, reader, (byte)channel, DeliveryMethod.ReliableOrdered);
                }
                finally
                {
                    GnsNative.SteamAPI_SteamNetworkingMessage_t_Release(messages[i]);
                }
            }
        }
        while (count >= ReceiveBatch && client != null);
    }

    // Facepunch's SocketManager.Receive and SteamServerManager.OnMessage.
    private void ReceiveServer(Server server)
    {
        int count;
        do
        {
            count = GnsNative.SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnPollGroup(GnsNative.Sockets, server.PollGroup, messages, ReceiveBatch);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var reader = ReadMessage(messages[i], out uint connection, out int channel);
                    var peer = GetPeer(connection);
                    if (peer == null)
                        continue;

                    if (peer.connectionRequest != null)
                    {
                        try
                        {
                            OnConnectionRequest?.Invoke(reader, peer.connectionRequest);
                        }
                        catch (Exception e)
                        {
                            Multiplayer.LogError($"GnsTransport: login handler threw: {e}");
                        }
                        peer.connectionRequest = null;
                        continue;
                    }

                    OnNetworkReceive?.Invoke(peer, reader, (byte)channel, DeliveryMethod.ReliableOrdered);
                }
                finally
                {
                    GnsNative.SteamAPI_SteamNetworkingMessage_t_Release(messages[i]);
                }
            }
        }
        while (count == ReceiveBatch);
    }

    private static NetDataReader ReadMessage(IntPtr message, out uint connection, out int channel)
    {
        IntPtr data = Marshal.ReadIntPtr(message, GnsNative.MessageData);
        int size = Marshal.ReadInt32(message, GnsNative.MessageSize);
        connection = (uint)Marshal.ReadInt32(message, GnsNative.MessageConnection);
        channel = Marshal.ReadInt32(message, GnsNative.MessageChannel);

        byte[] buffer = new byte[size];
        Marshal.Copy(data, buffer, 0, size);
        return new NetDataReader(buffer, 0, size);
    }
    #endregion

    private int Ping(uint connection)
    {
        for (int offset = 0; offset < GnsNative.RealTimeStatusSize; offset += 4)
            Marshal.WriteInt32(realTimeStatus, offset, 0);
        GnsNative.SteamAPI_ISteamNetworkingSockets_GetConnectionRealTimeStatus(GnsNative.Sockets, connection, realTimeStatus, 0, IntPtr.Zero);
        return Marshal.ReadInt32(realTimeStatus, GnsNative.RealTimeStatusPing);
    }

    private GnsPeer CreatePeer(uint connection)
    {
        var peer = new GnsPeer(nextPeerId++, connection);
        connectionToPeer[connection] = peer;
        peerIdToPeer[peer.Id] = peer;
        return peer;
    }

    private GnsPeer GetPeer(uint connection)
    {
        return connectionToPeer.TryGetValue(connection, out var peer) ? peer : null;
    }
}

public class GnsConnectionRequest : IConnectionRequest
{
    private readonly uint connection;
    private readonly GnsPeer peer;

    public GnsConnectionRequest(uint connection, GnsPeer peer)
    {
        this.connection = connection;
        this.peer = peer;
    }

    public ITransportPeer Accept()
    {
        return peer;
    }

    public void Reject(NetDataWriter data = null)
    {
        if (GnsNative.Sockets == IntPtr.Zero)
            return;
        if (data != null)
            peer?.Send(data, DeliveryMethod.ReliableUnordered);

        GnsNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(GnsNative.Sockets, connection, 0, "Closing Connection", true);
    }

    // As SteamConnectionRequest reports it.
    public IPEndPoint RemoteEndPoint => new(IPAddress.Any, 0);
}

public class GnsPeer : ITransportPeer
{
    private readonly uint connection;
    private TransportConnectionState _currentState;
    public GnsConnectionRequest connectionRequest;
    public int Id { get; }

    public GnsPeer(int id, uint connection)
    {
        Id = id;
        this.connection = connection;
    }

    public void Send(NetDataWriter writer, DeliveryMethod deliveryMethod)
    {
        if (GnsNative.Sockets == IntPtr.Zero)
            return;
        int sendType = (int)SteamWorksTransport.ToSendType(deliveryMethod);

        int result = GnsNative.SteamAPI_ISteamNetworkingSockets_SendMessageToConnection(
            GnsNative.Sockets, connection, writer.Data, (uint)writer.Length, sendType, IntPtr.Zero);
        // A refused reliable message is lost for good (a permanent desync), so never drop it silently.
        if (result != GnsNative.ResultOK && sendType == (int)Steamworks.Data.SendType.Reliable && Time.unscaledTime >= nextSendFailureLogAt)
        {
            nextSendFailureLogAt = Time.unscaledTime + 1f;
            Multiplayer.LogError($"GnsPeer {Id}: reliable send of {writer.Length} bytes failed: {(Result)result}");
        }
    }

    private float nextSendFailureLogAt;

    public void Disconnect(NetDataWriter data = null)
    {
        if (GnsNative.Sockets == IntPtr.Zero)
            return;
        if (data != null)
            Send(data, DeliveryMethod.ReliableUnordered);

        GnsNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(GnsNative.Sockets, connection, 0, "Closing Connection", true);
    }

    // SteamPeer.OnConnectionStatusChanged.
    public void OnConnectionStatusChanged(int state)
    {
        _currentState = state switch
        {
            GnsNative.StateConnected => TransportConnectionState.Connected,
            GnsNative.StateConnecting => TransportConnectionState.Connecting,
            GnsNative.StateFindingRoute => TransportConnectionState.Connecting,
            GnsNative.StateClosedByPeer => TransportConnectionState.Disconnected,
            GnsNative.StateProblemDetectedLocally => TransportConnectionState.Disconnected,
            GnsNative.StateFinWait => TransportConnectionState.Disconnecting,
            GnsNative.StateLinger => TransportConnectionState.Disconnecting,
            GnsNative.StateDead => TransportConnectionState.Disconnected,
            GnsNative.StateNone => TransportConnectionState.Disconnected,
            _ => TransportConnectionState.Disconnected
        };
    }

    public TransportConnectionState ConnectionState => _currentState;
}
