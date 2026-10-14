using LiteNetLib;
using LiteNetLib.Utils;
using Multiplayer.API;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Networking.Data.Player;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Data.World;
using Multiplayer.Networking.Serialization;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Multiplayer.Networking.Managers;

public abstract class NetworkManager
{
    protected const int LATENCY_FLAG = 150;

    protected readonly NetPacketProcessor netPacketProcessor;
    protected readonly NetDataWriter cachedWriter = new();

    private readonly ITransport transport;
    private readonly List<ITransport> additionalTransports = [];
    protected readonly NetManager netManager;

    protected abstract string LogPrefix { get; }

    public NetStatistics Statistics => transport.Statistics;
    public bool IsRunning => transport.IsRunning;
    public bool IsProcessingPacket { get; private set; }

    protected NetworkManager(Settings settings)
    {
        netPacketProcessor = new NetPacketProcessor();
        //transport = new LiteNetLibTransport();
        transport = new SteamWorksTransport();

        AttachTransport(transport);

        PacketSerializationRegistry.Register(netPacketProcessor);

        OnSettingsUpdated(settings);
        Settings.OnSettingsUpdated += OnSettingsUpdated;

        Subscribe();

    }

    public virtual void OnSettingsUpdated(Settings settings)
    {
        transport?.UpdateSettings(settings);
        foreach (ITransport additionalTransport in additionalTransports)
            additionalTransport.UpdateSettings(settings);
    }

    public void PollEvents()
    {
        //netManager.PollEvents();
        transport?.PollEvents();
        foreach (ITransport additionalTransport in additionalTransports)
            additionalTransport.PollEvents();
    }

    public virtual bool Start()
    {
        NetIdProvider.Instance.CheckInitialization();
        return transport.Start();
    }
    public virtual bool Start(IPAddress ipv4, IPAddress ipv6, int port)
    {
        return transport.Start(ipv4, ipv6, port);
    }
    public virtual bool Start(int port)
    {
        return transport.Start(port);
    }

    protected virtual ITransportPeer Connect(string address, int port, NetDataWriter netDataWriter)
    {
        return transport.Connect(address, port, netDataWriter);
    }

    /// <summary>
    /// Adds an optional transport that feeds the same packet processor and server/client handlers.
    /// The primary transport remains responsible for normal client connections.
    /// </summary>
    protected void AddTransport(ITransport additionalTransport, Settings settings)
    {
        if (additionalTransport == null)
            throw new ArgumentNullException(nameof(additionalTransport));

        AttachTransport(additionalTransport);
        additionalTransport.UpdateSettings(settings);
        additionalTransports.Add(additionalTransport);
    }


    public virtual void Stop()
    {
        transport.Stop(true);
        foreach (ITransport additionalTransport in additionalTransports)
            additionalTransport.Stop(true);

        DetachTransport(transport);
        foreach (ITransport additionalTransport in additionalTransports)
            DetachTransport(additionalTransport);
        additionalTransports.Clear();

        Settings.OnSettingsUpdated -= OnSettingsUpdated;

        NetIdProvider.Destroy(NetIdProvider.Instance);
    }

    protected NetDataWriter WritePacket<T>(T packet) where T : class, new()
    {
        cachedWriter.Reset();
        netPacketProcessor.Write(cachedWriter, packet);
        DebugTrace.PacketSerialized(cachedWriter, GetDebugSide(), packet);
        return cachedWriter;
    }

    protected NetDataWriter WriteNetSerializablePacket<T>(T packet) where T : INetSerializable, new()
    {
        cachedWriter.Reset();
        netPacketProcessor.WriteNetSerializable(cachedWriter, ref packet);
        DebugTrace.PacketSerialized(cachedWriter, GetDebugSide(), packet);
        return cachedWriter;
    }

    protected void SendPacket<T>(ITransportPeer peer, T packet, DeliveryMethod deliveryMethod) where T : class, new()
    {
        NetDataWriter writer = WritePacket(packet);
        DebugTrace.PacketSending(peer, writer, deliveryMethod, GetDebugSide(), typeof(T).Name);
        peer?.Send(writer, deliveryMethod);
    }

    protected void SendNetSerializablePacket<T>(ITransportPeer peer, T packet, DeliveryMethod deliveryMethod) where T : INetSerializable, new()
    {
        NetDataWriter writer = WriteNetSerializablePacket(packet);
        DebugTrace.PacketSending(peer, writer, deliveryMethod, GetDebugSide(), typeof(T).Name);
        peer?.Send(writer, deliveryMethod);
    }

    //protected void SendUnconnectedPacket<T>(T packet, string ipAddress, int port) where T : class, new()
    //{
    //    transport.SendUnconnectedMessage(WritePacket(packet), ipAddress, port);
    //}

    protected abstract void Subscribe();

    private void AttachTransport(ITransport target)
    {
        target.OnConnectionRequest += OnConnectionRequest;
        target.OnPeerConnected += OnPeerConnected;
        target.OnPeerDisconnected += OnPeerDisconnected;
        target.OnNetworkReceive += OnNetworkReceive;
        target.OnNetworkError += OnNetworkError;
        target.OnNetworkLatencyUpdate += OnNetworkLatencyUpdate;
    }

    private void DetachTransport(ITransport target)
    {
        target.OnConnectionRequest -= OnConnectionRequest;
        target.OnPeerConnected -= OnPeerConnected;
        target.OnPeerDisconnected -= OnPeerDisconnected;
        target.OnNetworkReceive -= OnNetworkReceive;
        target.OnNetworkError -= OnNetworkError;
        target.OnNetworkLatencyUpdate -= OnNetworkLatencyUpdate;
    }

    #region Net Events
    public void OnNetworkReceive(ITransportPeer peer, NetDataReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        //LogDebug(() => $"NetworkManager.OnNetworkReceive()");
        try
        {
            DebugTrace.PacketReceived(peer, reader, channel, deliveryMethod, GetDebugSide());
            IsProcessingPacket = true;
            netPacketProcessor.ReadAllPackets(reader, peer);
        }
        catch (ParseException e)
        {
            Multiplayer.LogWarning($"[{GetType()}] Failed to parse packet: {e.Message}\r\n{e.StackTrace}");
        }
        finally
        {
            IsProcessingPacket = false;
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        Multiplayer.LogError($"Network error from {endPoint}: {socketError}");
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        //Multiplayer.Log($"OnNetworkReceiveUnconnected({remoteEndPoint}, {messageType})");
        try
        {
            IsProcessingPacket = true;
            netPacketProcessor.ReadAllPackets(reader, remoteEndPoint);
        }
        catch (ParseException e)
        {
            Multiplayer.LogWarning($"Failed to parse packet: {e.Message}");
        }
        finally
        {
            IsProcessingPacket = false;
        }
    }

    //Standard networking callbacks
    public abstract void OnPeerConnected(ITransportPeer peer);
    public abstract void OnPeerDisconnected(ITransportPeer peer, DisconnectReason disconnectInfo);
    public abstract void OnConnectionRequest(NetDataReader requestData, IConnectionRequest request);
    public abstract void OnNetworkLatencyUpdate(ITransportPeer peer, int latency);

    #endregion

    protected DebugRuntimeSide GetDebugSide() => GetType().Name == "NetworkServer" ? DebugRuntimeSide.Server : DebugRuntimeSide.Client;

    #region Logging

    public void LogDebug(Func<object> resolver)
    {
        if (!Multiplayer.Settings.DebugLogging)
            return;
        Multiplayer.LogDebug(() => $"{LogPrefix} {resolver.Invoke()}");
    }

    public void Log(object msg)
    {
        Multiplayer.Log($"{LogPrefix} {msg}");
    }

    public void LogWarning(object msg)
    {
        Multiplayer.LogWarning($"{LogPrefix} {msg}");
    }

    public void LogError(object msg)
    {
        Multiplayer.LogError($"{LogPrefix} {msg}");
    }

    #endregion
}
