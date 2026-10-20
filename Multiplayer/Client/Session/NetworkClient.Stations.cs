using DV.Shops;
using DV.ThingTypes;
using LiteNetLib;
using Multiplayer.Client.Stations;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Utils;
using UnityEngine;

namespace Multiplayer.Client.Session;

// NetworkClient's stations handlers and sends (see docs/design/code-layout.md).
public partial class NetworkClient : IStationsClient
{
    /// <summary>
    /// Numbers the service stations and module registers the way the host does, so the ids match,
    /// and, on a game that isn't the host, gives each pit stop its client part (which waits for the
    /// server's state of the station). The registers need no client part: the shells do what a
    /// player's game does.
    /// </summary>
    private void InitialiseStations()
    {
        Log($"Initialising Cash Registers...");
        NetworkedCashRegisterWithModules.InitialiseCashRegisters();
        Log($"Initialising Pit Stops...");
        NetworkedPitStopStation.InitialisePitStops();

        if (!NetworkRole.IsRemoteClient)
            return;
        foreach (NetworkedPitStopStation station in NetworkedPitStopStation.All)
            station.GetOrAddComponent<ClientPitStopStation>().Setup();
    }

    private void OnClientboundShopStockPacket(ClientboundShopStockPacket packet)
    {
        GlobalShopController shops = GlobalShopController.Instance;
        if (shops == null || packet.ItemPrefabNames == null || packet.PurchasedItems == null)
            return;
        for (int i = 0; i < packet.ItemPrefabNames.Length && i < packet.PurchasedItems.Length; i++)
        {
            ShopItemData data = shops.GetShopItemData(packet.ItemPrefabNames[i]);
            if (data != null)
                data.purchasedItems = packet.PurchasedItems[i];
        }
        shops.Fire_GlobalShopDataChanged();
    }

    private void OnCommonPitStopInteractionPacket(CommonPitStopInteractionPacket packet)
    {
        if (!NetworkedPitStopStation.Get(packet.NetId, out var netPitStop))
        {
            LogWarning($"Pit Stop Interaction received for netId: {packet.NetId}, but pit stop does not exist!");
        }

        Log($"Pit stop interaction received for {netPitStop.StationName}");

        LogDebug(() => $"OnCommonPitStopInteractionPacket() [{netPitStop.StationName}, {packet.NetId}], interaction: [{packet.InteractionType}], resource: {packet?.ResourceType}, State: {packet.Value}");
        netPitStop.ProcessInteractionPacketAsClient(packet);
    }

    private void OnCommonPitStopPlugInteractionPacket(CommonPitStopPlugInteractionPacket packet)
    {
        if (!NetworkedPluggableObject.Get(packet.NetId, out var netPlug))
        {
            LogWarning($"Pit Stop Plug Interaction received for plug netId: {packet.NetId}, but pit stop plug does not exist!");
            return;
        }

        Log($"Pit Stop Plug Interaction received for {netPlug.NetId}");

        LogDebug(() => $"OnCommonPitStopPlugInteractionPacket() [{netPlug?.transform?.name}, {packet.NetId}], interaction: [{(PlugInteractionType)packet.InteractionType}]");
        netPlug.ProcessPacket(packet);
    }

    private void OnClientboundPitStopBulkUpdatePacket(ClientboundPitStopBulkUpdatePacket packet)
    {
        LogDebug(() => $"OnClientboundPitStopBulkUpdatePacket() NetId: {packet.NetId}, CarCount: {packet.CarCount}, CarSelection: {packet.CarSelection}, FaucetNotch: {packet.FaucetNotch}, ResourceData Count: {packet.ResourceData.Length}, PlugData: {packet.PlugData.Length}");

        if (!NetworkedPitStopStation.Get(packet.NetId, out var netPitStop) || !netPitStop.TryGetComponent(out ClientPitStopStation clientPitStop))
        {
            LogWarning($"Pit Stop Bulk Data received for station netId: {packet.NetId}, but pit stop does not exist!");
            return;
        }

        Log($"Pit Stop Bulk Data received for {netPitStop.StationName}");

        clientPitStop.ProcessBulkUpdate(packet);
    }

    private void OnCommonCashRegisterWithModulesActionPacket(CommonCashRegisterWithModulesActionPacket packet)
    {
        if (!NetworkedCashRegisterWithModules.Get(packet.NetId, out NetworkedCashRegisterWithModules netCashRegister))
        {
            LogWarning($"Cash Register With Modules Action received for netId: {packet.NetId}, but cash register does not exist!");
            return;
        }

        Log($"Cash Register With Modules Action received for {netCashRegister.GetObjectPath()}, Action: {packet.Action}, Amount: {packet.Amount}");

        netCashRegister.Client_ProcessCashRegisterAction(packet.Action, packet.Amount);
    }

    public void SendPitStopInteractionPacket(ushort netId, PitStopStationInteractionType interaction, ResourceType? resource, float state)
    {
        LogDebug(() => $"SendPitStopInteractionPacket({netId}, [{interaction}], {resource}, {state})");

        int res = resource == null ? 0 : (int)resource;
        SendPacketToServer(new CommonPitStopInteractionPacket
        {
            Tick = NetworkLifecycle.Instance.Tick,
            NetId = netId,
            InteractionType = interaction,
            ResourceType = res,
            Value = state
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendPitStopPlugInteractionPacket
    (
        ushort netId,
        PlugInteractionType interaction,
        Vector3? position = null,
        Quaternion? rotation = null,
        ushort trainCarNetId = 0,
        sbyte socketIndex = -1
    )
    {
        LogDebug(() => $"SendPitStopPlugInteractionPacket({netId}, {interaction}, pos: {position}, rot: {rotation}, trainNetId: {trainCarNetId}, socketIndex: {socketIndex})");

        var packet = new CommonPitStopPlugInteractionPacket
        {
            NetId = netId,
            InteractionType = interaction,
            TrainCarNetId = trainCarNetId,
            SocketIndex = socketIndex,
            Position = position,
            Rotation = rotation,
        };
        // SendNetSerializablePacketToServer has no in-process hook: on the host, hand it over here.
        if (NetworkLifecycle.Instance.LocalServer?.Submit(packet) == true)
            return;
        SendNetSerializablePacketToServer(packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendShopPurchase(int shopIndex, string itemPrefabName, int amount)
    {
        SendPacketToServer(new ServerboundShopPurchasePacket
        {
            ShopIndex = shopIndex,
            ItemPrefabName = itemPrefabName,
            Amount = amount
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendShopPayment(double amount)
    {
        SendPacketToServer(new ServerboundShopPaymentPacket { Amount = amount },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendCashRegisterAction(ushort netId, CashRegisterAction action, double amount = 0.0f)
    {
        SendPacketToServer(
            new CommonCashRegisterWithModulesActionPacket
            {
                NetId = netId,
                Action = action,
                Amount = amount
            },
            DeliveryMethod.ReliableOrdered
        );
    }
}
