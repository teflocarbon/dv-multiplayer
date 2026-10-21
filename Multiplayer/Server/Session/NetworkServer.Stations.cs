using DV.InventorySystem;
using DV.Shops;
using DV.CabControls;
using LiteNetLib;
using Multiplayer.Server.Items;
using Multiplayer.Server.Stations;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Transport;
using Multiplayer.Shared.Utils;
using ItemMoveReason = Multiplayer.Core.Items.ItemMoveReason;
using System;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Session;

// NetworkServer's stations handlers and sends (see docs/design/code-layout.md).
public partial class NetworkServer
{
    public void SendPitStopBulkDataPacket(ushort netId, int carCount, int carIndex, int faucetNotch, LocoResourceModuleData[] stationData, PitStopPlugData[] plugData, ServerPlayer player)
    {
        LogDebug(() => $"SendPitStopBulkDataPacket({netId}, {carCount}, {carIndex}, {faucetNotch}, {stationData.Count()}, {plugData.Count()}, {player})");

        var packet = new ClientboundPitStopBulkUpdatePacket
        {
            NetId = netId,
            CarCount = carCount,
            CarSelection = carIndex,
            FaucetNotch = faucetNotch,
            ResourceData = stationData,
            PlugData = plugData,
        };

        if (player.Peer != SelfPeer)
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendPitStopInteractionPacket(ServerPlayer player, CommonPitStopInteractionPacket packet)
    {
        LogDebug(() => $"SendPitStopInteractionPacket({player.Username}, {packet.NetId})");

        SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendPitStopPlugInteractionPacket(ServerPlayer player, CommonPitStopPlugInteractionPacket packet)
    {
        LogDebug(() => $"SendPitStopPlugInteractionPacket({packet.NetId}, {packet.InteractionType}, {packet.PlayerId}, {packet.Position}, {packet.Rotation}, {packet.TrainCarNetId}, {packet.SocketIndex}, {packet.YankForce}, {packet.YankMode})");
        SendNetSerializablePacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendCashRegisterAction(CommonCashRegisterWithModulesActionPacket packet, ServerPlayer[] players = null)
    {
        if (players == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, true);
        else
            foreach (var player in players)
                SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Numbers the service stations and module registers (as a client does) and gives each its
    /// server part. Called once the save has loaded, rather than when the host's own client loads,
    /// so a dedicated host has them too.
    /// </summary>
    private void InitialiseStations()
    {
        NetworkedPitStopStation.InitialisePitStops();
        NetworkedCashRegisterWithModules.InitialiseCashRegisters();

        foreach (NetworkedCashRegisterWithModules register in NetworkedCashRegisterWithModules.All)
            register.GetOrAddComponent<ServerCashRegister>().Setup();
        foreach (NetworkedPitStopStation station in NetworkedPitStopStation.All)
            station.GetOrAddComponent<ServerPitStopStation>().Setup();
    }

    // The host player's pit stop, plug and module register requests reach the server in-process
    // (the plug's through NetworkClient.SendPitStopPlugInteractionPacket, which isn't hooked), and
    // are validated and relayed like anyone's; the host's world already has them. A register
    // request is answered in-process (NetworkedCashRegisterWithModules.AnswerLocalRequest).
    partial void RegisterLocalStations()
    {
        HandleLocal<CommonPitStopInteractionPacket>(HandlePitStopInteraction);
        HandleLocal<CommonPitStopPlugInteractionPacket>(HandlePlugInteraction);
        HandleLocal<CommonCashRegisterWithModulesActionPacket>(HandleCashRegisterAction);
    }

    private void OnCommonPitStopInteractionPacket(CommonPitStopInteractionPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out var player))
            HandlePitStopInteraction(packet, player);
        else
            LogWarning($"Received Pit Stop Interaction, but player was not found");
    }

    private void HandlePitStopInteraction(CommonPitStopInteractionPacket packet, ServerPlayer player)
    {
        if (ServerPitStopStation.TryGet(packet.NetId, out ServerPitStopStation station))
            station.ProcessInteraction(packet, player);
        else
            LogWarning($"HandlePitStopInteraction() Failed to find PitStopStation with netId: {packet.NetId}");
    }

    private void OnCommonPitStopPlugInteractionPacket(CommonPitStopPlugInteractionPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player))
        {
            LogWarning($"Received Pit Stop Plug Interaction, but player was not found");
            SendNetSerializablePacket(peer, new CommonPitStopPlugInteractionPacket
            {
                NetId = packet.NetId,
                InteractionType = (byte)PitStopStationInteractionType.Reject
            }, DeliveryMethod.ReliableOrdered);
            return;
        }

        HandlePlugInteraction(packet, player);
    }

    private void HandlePlugInteraction(CommonPitStopPlugInteractionPacket packet, ServerPlayer player)
    {
        if (NetworkedPluggableObject.Get(packet.NetId, out NetworkedPluggableObject plug) &&
            plug.TryGetComponent(out ServerPluggableObject serverPlug))
            serverPlug.ProcessInteraction(packet, player);
        else
            LogError($"HandlePlugInteraction() Failed to find plug with netId: {packet.NetId}");
    }

    private void OnCommonCashRegisterWithModulesActionPacket(CommonCashRegisterWithModulesActionPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player))
        {
            LogWarning($"Cash Register With Modules Action received, but player was not found");
            return;
        }

        HandleCashRegisterAction(packet, player);
    }

    private void HandleCashRegisterAction(CommonCashRegisterWithModulesActionPacket packet, ServerPlayer player)
    {
        if (!ServerCashRegister.TryGet(packet.NetId, out ServerCashRegister register))
        {
            LogWarning($"Cash Register With Modules Action received for netId: {packet.NetId}, but cash register does not exist!");
            return;
        }

        Log($"Cash Register With Modules Action received for {register.GetObjectPath()}, Action: {packet.Action}, Amount: {packet.Amount}");
        register.ProcessAction(player, packet);
    }
}
