using DV.InventorySystem;
using DV.Shops;
using DV.CabControls;
using LiteNetLib;
using Multiplayer.Server.Items;
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

    private void OnCommonPitStopInteractionPacket(CommonPitStopInteractionPacket packet, ITransportPeer peer)
    {
        bool foundPlayer = TryGetServerPlayer(peer, out var player);
        if (!foundPlayer)
        {
            LogWarning($"Received Pit Stop Plug Interaction, but player was not found");
        }
        else
        {
            if (NetworkedPitStopStation.Get(packet.NetId, out NetworkedPitStopStation controller))
                controller.ProcessInteractionPacketAsHost(packet, player);
            else
                LogWarning($"OnCommonPitStopInteractionPacket() Failed to find PitStopStation with netId: {packet.NetId}");
        }
    }

    private void OnCommonPitStopPlugInteractionPacket(CommonPitStopPlugInteractionPacket packet, ITransportPeer peer)
    {
        bool foundPlayer = TryGetServerPlayer(peer, out var player);
        if (!foundPlayer)
        {
            LogWarning($"Received Pit Stop Plug Interaction, but player was not found");
            SendNetSerializablePacket(peer, new CommonPitStopPlugInteractionPacket
            {
                NetId = packet.NetId,
                InteractionType = (byte)PitStopStationInteractionType.Reject
            }, DeliveryMethod.ReliableOrdered);
        }

        if (NetworkedPluggableObject.Get(packet.NetId, out NetworkedPluggableObject plug) && foundPlayer)
        {
            plug.ProcessInteractionPacketAsHost(packet, player);
        }
        else
        {
            LogError($"OnCommonPitStopInteractionPacket() Failed to find PitStopStation with netId: {packet.NetId}");
        }
    }

    private void OnServerboundShopPaymentPacket(ServerboundShopPaymentPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (double.IsNaN(packet.Amount) || Math.Abs(packet.Amount) <= double.Epsilon ||
            Math.Abs(packet.Amount) > MaxShopPayment)
        {
            LogWarning($"Shop payment from P{player.PlayerId} rejected: amount {packet.Amount}");
            return;
        }

        if (packet.Amount > 0)
        {
            // The client has already taken it from its copy of the wallet; if the shared wallet is
            // shorter than it thought (a teammate spent meanwhile), take what there is.
            double paid = Math.Min(packet.Amount, Inventory.Instance.PlayerMoney);
            Log($"P{player.PlayerId} paid ${paid:N2} into a shop register");
            if (paid <= double.Epsilon)
                return;
            player.ShopCashInRegisters += paid;
            Inventory.Instance.RemoveMoney(paid); // MoneyChanged broadcasts the new balance
            return;
        }

        // Change or a cancelled basket: never more than this player has put into shop registers.
        double refund = Math.Min(-packet.Amount, player.ShopCashInRegisters);
        Log($"P{player.PlayerId} got ${refund:N2} back from a shop register (asked for ${-packet.Amount:N2})");
        if (refund <= double.Epsilon)
            return;
        player.ShopCashInRegisters -= refund;
        Inventory.Instance.AddMoney(refund);
    }

    private void OnServerboundShopPurchasePacket(ServerboundShopPurchasePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        GlobalShopController shops = GlobalShopController.Instance;
        Shop shop = shops != null && packet.ShopIndex >= 0 && packet.ShopIndex < shops.globalShopList.Count
            ? shops.globalShopList[packet.ShopIndex]
            : null;
        ShopItemData data = shop != null ? shops.GetShopItemData(packet.ItemPrefabName ?? string.Empty) : null;
        if (data == null || packet.Amount <= 0)
        {
            LogWarning($"Shop purchase from P{player.PlayerId} rejected: shop {packet.ShopIndex}, item '{packet.ItemPrefabName}' x{packet.Amount}");
            SendShopStock(peer);
            return;
        }

        double cost = (double)data.basePrice * packet.Amount;
        string rejection = null;
        if (player.ShopCashInRegisters + ShopPriceTolerance < cost)
            rejection = $"paid ${player.ShopCashInRegisters:N2} of ${cost:N2}";
        else if (data.ItemsInStock < packet.Amount)
            rejection = $"only {data.ItemsInStock} in stock";
        else if ((player.AbsoluteWorldPosition - (shop.itemSpawnTransform.position - WorldMover.currentMove)).sqrMagnitude >
                 MaxShopPurchaseDistance * MaxShopPurchaseDistance)
            rejection = "too far from the shop";

        if (rejection != null)
        {
            LogWarning($"Shop purchase of {packet.ItemPrefabName} x{packet.Amount} from P{player.PlayerId} rejected: {rejection}");
            // The client has already taken its basket's price out of the register; give back what it paid.
            double refund = Math.Min(cost, player.ShopCashInRegisters);
            if (refund > double.Epsilon)
            {
                player.ShopCashInRegisters -= refund;
                Inventory.Instance.AddMoney(refund);
            }
            SendShopStock(peer);
            return;
        }

        player.ShopCashInRegisters = Math.Max(0, player.ShopCashInRegisters - cost);
        Log($"P{player.PlayerId} bought {packet.ItemPrefabName} x{packet.Amount} for ${cost:N2}");
        data.purchasedItems += packet.Amount;
        UnlockablesManager.Instance.UnlockItem(data.item.ItemPrefabName);
        CoroutineManager.Instance.StartCoroutine(SpawnShopPurchase(data.item.ItemPrefabName, shop, packet.Amount, player));
        shops.Fire_GlobalShopDataChanged();
        SendShopStock();
    }

    // As GlobalShopController.InstantiatePurchasedItems, but always at the shop: that one leaves
    // items in the Lost and Found when the host is not near the shop. The buyer owns what they
    // bought; unowned, it went to whoever picked it up first.
    private static System.Collections.IEnumerator SpawnShopPurchase(string itemPrefabName, Shop shop, int amount, ServerPlayer buyer)
    {
        for (int i = 0; i < amount; i++)
        {
            if (shop == null)
                yield break;
            GameObject obj = UnityEngine.Object.Instantiate(Resources.Load(itemPrefabName) as GameObject,
                shop.itemSpawnTransform.position, Quaternion.identity);
            obj.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
            if (obj.TryGetComponent(out ShopRestocker restocker))
                restocker.restockOnItemDestroyed = true;
            ItemBase item = obj.GetComponent<ItemBase>();
            StorageController.Instance.AddItemToWorldStorage(item);
            item.ItemRigidbody.velocity = Vector3.zero;
            item.ItemRigidbody.angularVelocity = Vector3.zero;
            if (obj.TryGetComponent(out APurchaseTrigger trigger))
                trigger.OnPurchased(obj);
            NetworkedItem networked = obj.GetOrAddComponent<NetworkedItem>();
            if (buyer != null && buyer.Guid != Guid.Empty && networked != null && networked.NetId != 0 &&
                AuthoritativeItemRegistry.Ensure(networked) is { PersistentOwnerPlayerId: 0 } record)
            {
                record.PersistentOwnerPlayerId = buyer.PlayerId;
                record.PersistentOwnerIdentity = buyer.Guid;
                buyer.AddOwnedItem(networked.NetId);
                ItemChangeLog.RecordMove(networked, record, ItemChangeLog.Move(ItemMoveReason.Purchased, buyer,
                    $"at {shop.name}, {i + 1} of {amount}, owner {buyer.Username}, lying in the world"));
            }
            else if (networked != null && networked.NetId != 0)
                Multiplayer.LogWarning($"Item {networked.name} (netId {networked.NetId}) bought by " +
                    $"{buyer?.Username ?? "nobody"} at {shop.name} has no owner: it already had one, or the buyer has no identity");
            yield return WaitFor.Seconds(0.5f);
        }
    }

    public void SendShopStock(ITransportPeer peer = null)
    {
        GlobalShopController shops = GlobalShopController.Instance;
        if (shops == null)
            return;
        ClientboundShopStockPacket packet = new()
        {
            ItemPrefabNames = shops.shopItemsData.Select(d => d.item.ItemPrefabName).ToArray(),
            PurchasedItems = shops.shopItemsData.Select(d => d.purchasedItems).ToArray()
        };
        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, excludeSelf: true);
    }

    private void OnCommonCashRegisterWithModulesActionPacket(CommonCashRegisterWithModulesActionPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player))
        {
            LogWarning($"Cash Register With Modules Action received, but player was not found");
            return;
        }

        if (!NetworkedCashRegisterWithModules.Get(packet.NetId, out NetworkedCashRegisterWithModules netCashRegister))
        {
            LogWarning($"Cash Register With Modules Action received for netId: {packet.NetId}, but cash register does not exist!");
            return;
        }

        Log($"Cash Register With Modules Action received for {netCashRegister.GetObjectPath()}, Action: {packet.Action}, Amount: {packet.Amount}");
        netCashRegister.Server_ProcessCashRegisterAction(player, packet);
    }
}
