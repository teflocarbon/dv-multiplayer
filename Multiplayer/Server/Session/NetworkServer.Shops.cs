using System;
using System.Collections.Generic;
using System.Linq;
using DV.Shops;
using LiteNetLib;
using Multiplayer.Core.Shops;
using Multiplayer.Server.Shops;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Shops;
using Multiplayer.Shared.Transport;

namespace Multiplayer.Server.Session;

// NetworkServer's shops: the shelf seed, the shop check at join, and the registers' requests and
// state (see docs/design/shops.md).
public partial class NetworkServer
{
    // This session's shelf seed: every game lays the shops' shelves out from it (ShopShelfLayout).
    private int shopShelfSeed;
    private ServerShops shops;

    public ServerShops Shops => shops ??= new ServerShops(this);

    // Picked once the world has loaded, so a dedicated host has it too. The host's own world is laid
    // out here, a client's when it joins (SendShopLayout).
    private void InitialiseShopShelves()
    {
        shopShelfSeed = new System.Random().Next();
        Log($"Shops: {ShopCatalogue.Compute()}, shelf seed {shopShelfSeed}");
        ShopShelfLayout.Apply(shopShelfSeed);
    }

    public void SendShopLayout(ITransportPeer peer) =>
        SendPacket(peer, new ClientboundShopLayoutPacket { Seed = shopShelfSeed }, DeliveryMethod.ReliableOrdered);

    // Shop requests name shops and items by index, so a client whose shops differ (a mod adding shop
    // items on one side only) would buy the wrong thing. Refused like a different railway.
    private bool CheckShopCatalogue(ServerPlayer player)
    {
        ShopCatalogueFingerprint hostShops = ShopCatalogue.Compute();
        string mismatch = hostShops.DescribeMismatch(player.ShopCatalogue);
        if (mismatch == null)
        {
            Log($"Player {player.Username}'s shops match: {hostShops}");
            return true;
        }

        LogError($"Refusing {player.Username}: shops {player.ShopCatalogue} don't match the host's {hostShops}");
        KickPlayer(player, mismatch);
        return false;
    }

    public void SendShopStock(ITransportPeer peer = null)
    {
        GlobalShopController shopController = GlobalShopController.Instance;
        if (shopController == null)
            return;
        ClientboundShopStockPacket packet = new()
        {
            ItemPrefabNames = shopController.shopItemsData.Select(d => d.item.ItemPrefabName).ToArray(),
            PurchasedItems = shopController.shopItemsData.Select(d => d.purchasedItems).ToArray(),
            AllowedToHaveAmount = shopController.shopItemsData.Select(ServerShops.Allowed).ToArray()
        };
        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, excludeSelf: true);
    }

    /// <summary>
    /// A register's whole state, to every game (the host's own player's included: its game shows
    /// the register like anyone's), or to one joining player.
    /// </summary>
    internal void SendShopRegisterState(int shopIndex, ShopBasket basket, ShopRegisterEvent ev, byte playerId,
        int module = 0, ITransportPeer peer = null)
    {
        ClientboundShopRegisterStatePacket packet = new()
        {
            ShopIndex = (byte)shopIndex,
            Revision = basket.Revision,
            LineModules = basket.Lines.Select(l => (byte)l.Module).ToArray(),
            LineOwners = basket.Lines.Select(l => l.Owner).ToArray(),
            LineUnits = basket.Lines.Select(l => (byte)Math.Min(l.Units, byte.MaxValue)).ToArray(),
            Deposited = basket.Deposited,
            Delivering = basket.Delivering,
            Event = (byte)ev,
            EventPlayerId = playerId,
            EventModule = (byte)module
        };
        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState);
    }

    // At ReadyForWorldState: every register that isn't empty and idle.
    private void SendShopRegisters(ITransportPeer peer)
    {
        foreach (KeyValuePair<int, ShopBasket> register in Shops.Baskets)
            if (!register.Value.IsEmpty || register.Value.Deposited > 0 || register.Value.Delivering)
                SendShopRegisterState(register.Key, register.Value, ShopRegisterEvent.None, 0, peer: peer);
    }

    partial void RegisterLocalShops()
    {
        HandleLocal<ServerboundShopScanPacket>(HandleShopScan);
        HandleLocal<ServerboundShopDepositPacket>(HandleShopDeposit);
        HandleLocal<ServerboundShopBuyPacket>(HandleShopBuy);
        HandleLocal<ServerboundShopCancelPacket>(HandleShopCancel);
    }

    private void SubscribeShops()
    {
        netPacketProcessor.SubscribeReusable<ServerboundShopScanPacket, ITransportPeer>((p, peer) => FromPeer(peer, p, HandleShopScan));
        netPacketProcessor.SubscribeReusable<ServerboundShopDepositPacket, ITransportPeer>((p, peer) => FromPeer(peer, p, HandleShopDeposit));
        netPacketProcessor.SubscribeReusable<ServerboundShopBuyPacket, ITransportPeer>((p, peer) => FromPeer(peer, p, HandleShopBuy));
        netPacketProcessor.SubscribeReusable<ServerboundShopCancelPacket, ITransportPeer>((p, peer) => FromPeer(peer, p, HandleShopCancel));
    }

    private void FromPeer<T>(ITransportPeer peer, T packet, Action<T, ServerPlayer> handler)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            handler(packet, player);
    }

    private void HandleShopScan(ServerboundShopScanPacket packet, ServerPlayer player) =>
        Answer(player, packet.ShopIndex, ShopRequestKind.Scan, Shops.Scan(player, packet.ShopIndex, packet.ModuleIndex));

    private void HandleShopDeposit(ServerboundShopDepositPacket packet, ServerPlayer player) =>
        Answer(player, packet.ShopIndex, ShopRequestKind.Deposit, Shops.Deposit(player, packet.ShopIndex, packet.MoneyNetId));

    private void HandleShopBuy(ServerboundShopBuyPacket packet, ServerPlayer player) =>
        Answer(player, packet.ShopIndex, ShopRequestKind.Buy, Shops.Buy(player, packet.ShopIndex, packet.Revision));

    private void HandleShopCancel(ServerboundShopCancelPacket packet, ServerPlayer player) =>
        Answer(player, packet.ShopIndex, ShopRequestKind.Cancel, Shops.Cancel(player, packet.ShopIndex));

    // A refusal goes back to the player who asked; an accepted request shows in the register's state.
    private void Answer(ServerPlayer player, byte shopIndex, ShopRequestKind request, ShopRefusal refusal)
    {
        if (refusal == ShopRefusal.None)
            return;
        Log($"Shop {shopIndex}: P{player.PlayerId}'s {request} refused: {refusal}");
        if (request == ShopRequestKind.Scan)
            SendShopRegisterState(shopIndex, Shops.Basket(shopIndex), ShopRegisterEvent.ScanRefused, player.PlayerId);
        SendPacket(player.Peer, new ClientboundShopRequestResultPacket
        {
            ShopIndex = shopIndex,
            Request = (byte)request,
            Refusal = (byte)refusal
        }, DeliveryMethod.ReliableOrdered);
    }
}
