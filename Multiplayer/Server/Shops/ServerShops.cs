using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.CabControls;
using DV.InventorySystem;
using DV.Shops;
using DV.ThingTypes;
using Multiplayer.Core.Shops;
using Multiplayer.Server.Items;
using Multiplayer.Server.Players;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Shops;
using UnityEngine;

namespace Multiplayer.Server.Shops;

/// <summary>
/// The shops' registers, owned by the server (docs/design/shops.md, "The register on the server").
/// Each shop has one basket that everyone at the shop shares; scanning, paying, Buy and Cancel are
/// players' requests (the host's own player's too), and every change goes to every game as the
/// register's whole state (<c>NetworkServer.SendShopRegisterState</c>). No game's vanilla register
/// decides anything: they show this.
/// </summary>
public sealed class ServerShops
{
    // A player aims the scanner from up to 4 m away; their position is their feet.
    private const float MaxScanDistance = 6f;
    private const float RegisterReachMargin = 1f;

    private readonly NetworkServer server;
    private readonly Dictionary<int, ShopBasket> baskets = new();

    public ServerShops(NetworkServer server)
    {
        this.server = server;
    }

    public IEnumerable<KeyValuePair<int, ShopBasket>> Baskets => baskets;

    public ShopBasket Basket(int shopIndex)
    {
        if (!baskets.TryGetValue(shopIndex, out ShopBasket basket))
            baskets[shopIndex] = basket = new ShopBasket();
        return basket;
    }

    public ShopRefusal Scan(ServerPlayer player, int shopIndex, int moduleIndex)
    {
        if (!ShopRegisters.TryGetModule(shopIndex, moduleIndex, out ScanItemCashRegisterModule module))
            return ShopRefusal.NotSoldHere;
        if ((player.WorldPosition - module.transform.position).sqrMagnitude > MaxScanDistance * MaxScanDistance)
            return ShopRefusal.NotNear;
        if (!HoldsScanner(player))
            return ShopRefusal.NoScanner;
        ShopBasket basket = Basket(shopIndex);
        if (basket.Delivering)
            return ShopRefusal.Busy;
        ShopItemData data = ItemData(module);
        ShopRefusal available = Availability(data, UnitsOfItem(shopIndex, basket, data) + 1);
        if (available != ShopRefusal.None)
            return available;

        basket.Add(moduleIndex, player.PlayerId);
        server.SendShopRegisterState(shopIndex, basket, ShopRegisterEvent.Scanned, player.PlayerId, moduleIndex);
        return ShopRefusal.None;
    }

    /// <summary>Pays in from the wallet (<paramref name="moneyNetId"/> 0) or with a banknote.</summary>
    public ShopRefusal Deposit(ServerPlayer player, int shopIndex, ushort moneyNetId)
    {
        if (!ShopRegisters.TryGetShop(shopIndex, out Shop shop))
            return ShopRefusal.NotSoldHere;
        if (!shop.cashRegister.transform.PlayerCanReach(player, RegisterReachMargin))
            return ShopRefusal.NotNear;
        ShopBasket basket = Basket(shopIndex);
        double owed = basket.Owed(module => UnitPrice(shopIndex, module));
        if (owed <= 0)
            return ShopRefusal.NothingOwed;

        double paid;
        if (moneyNetId == 0)
        {
            paid = System.Math.Min(owed, Inventory.Instance.PlayerMoney);
            if (paid <= 0)
                return ShopRefusal.WalletEmpty;
            Inventory.Instance.RemoveMoney(paid); // MoneyChanged broadcasts the new balance
        }
        else if (!server.TrySpendMoneyItem(player, moneyNetId, $"paid into {shop.name}", out paid))
            return ShopRefusal.MoneyNotYours;

        Multiplayer.Log($"P{player.PlayerId} paid ${paid:N2} into {shop.name} ({(moneyNetId == 0 ? "wallet" : $"banknote {moneyNetId}")})");
        basket.Deposit(paid);
        server.SendShopRegisterState(shopIndex, basket, ShopRegisterEvent.CashAdded, player.PlayerId);
        return ShopRefusal.None;
    }

    public ShopRefusal Buy(ServerPlayer player, int shopIndex, uint revision)
    {
        if (!ShopRegisters.TryGetShop(shopIndex, out Shop shop))
            return ShopRefusal.NotSoldHere;
        if (!shop.cashRegister.transform.PlayerCanReach(player, RegisterReachMargin))
            return ShopRefusal.NotNear;
        ShopBasket basket = Basket(shopIndex);
        if (basket.Delivering)
            return ShopRefusal.Busy;
        if (revision != basket.Revision)
            return ShopRefusal.BasketChanged;
        if (basket.IsEmpty)
            return ShopRefusal.BasketEmpty;

        // Things change between a scan and Buy (stock bought elsewhere, a licence): keep what may still be bought.
        Dictionary<ShopItemData, int> kept = new();
        int dropped = basket.Trim(line =>
        {
            ShopItemData data = ShopRegisters.TryGetModule(shopIndex, line.Module, out ScanItemCashRegisterModule module) ? ItemData(module) : null;
            if (data == null)
                return 0;
            kept.TryGetValue(data, out int already);
            // Asking for none checks only the gates (game mode, licence, garage).
            int allowed = Availability(data, 0) != ShopRefusal.None ? 0 : System.Math.Min(line.Units, data.ItemsInStock - already);
            kept[data] = already + System.Math.Max(0, allowed);
            return allowed;
        });
        if (dropped > 0)
        {
            Multiplayer.LogWarning($"{shop.name}: {dropped} unit(s) can no longer be bought and left the basket");
            server.SendShopRegisterState(shopIndex, basket, ShopRegisterEvent.LinesDropped, player.PlayerId);
            if (basket.IsEmpty)
                return ShopRefusal.OutOfStock;
        }

        double total = basket.Total(module => UnitPrice(shopIndex, module));
        if (total > basket.Deposited)
        {
            server.SendShopRegisterState(shopIndex, basket, ShopRegisterEvent.NotEnoughCash, player.PlayerId);
            return ShopRefusal.NotEnoughCash;
        }

        IReadOnlyList<ShopBasketLine> bought = basket.Checkout(module => UnitPrice(shopIndex, module));
        double change = basket.TakeDeposit();
        if (change > 0)
            shop.cashRegister.moneyPrinter.PrintMoney(change);
        Multiplayer.Log($"P{player.PlayerId} bought at {shop.name} for ${total:N2} (change ${change:N2}): " +
                        string.Join(", ", bought.Select(l => $"{ItemName(shopIndex, l.Module)} x{l.Units} for P{l.Owner}")));

        GlobalShopController shops = GlobalShopController.Instance;
        foreach (ShopBasketLine line in bought)
        {
            ShopItemData data = ShopRegisters.TryGetModule(shopIndex, line.Module, out ScanItemCashRegisterModule module) ? ItemData(module) : null;
            if (data == null)
                continue;
            data.purchasedItems += line.Units;
            UnlockablesManager.Instance.UnlockItem(data.item.ItemPrefabName);
        }
        shops.Fire_GlobalShopDataChanged();
        server.SendShopStock();

        basket.SetDelivering(true);
        server.SendShopRegisterState(shopIndex, basket, ShopRegisterEvent.Bought, player.PlayerId);
        CoroutineManager.Instance.StartCoroutine(Deliver(shopIndex, shop, bought));
        return ShopRefusal.None;
    }

    public ShopRefusal Cancel(ServerPlayer player, int shopIndex)
    {
        if (!ShopRegisters.TryGetShop(shopIndex, out Shop shop))
            return ShopRefusal.NotSoldHere;
        if (!shop.cashRegister.transform.PlayerCanReach(player, RegisterReachMargin))
            return ShopRefusal.NotNear;
        ShopBasket basket = Basket(shopIndex);
        if (basket.Delivering)
            return ShopRefusal.Busy;

        basket.ClearLines();
        double cash = basket.TakeDeposit();
        if (cash > 0)
            shop.cashRegister.moneyPrinter.PrintMoney(cash);
        Multiplayer.Log($"P{player.PlayerId} cancelled the basket at {shop.name}, ${cash:N2} printed");
        server.SendShopRegisterState(shopIndex, basket, ShopRegisterEvent.Cancelled, player.PlayerId);
        return ShopRefusal.None;
    }

    // A scanner's aim goes to the players who can see it.
    private const float ScannerAimRange = 50f;

    private readonly Dictionary<ushort, (byte Shop, byte Module)> scannerAims = new();

    /// <summary>What each scanner last pointed at, by its NetId (module 255: nothing).</summary>
    public IReadOnlyDictionary<ushort, (byte Shop, byte Module)> ScannerAims => scannerAims;

    /// <summary>
    /// A holder's scanner points at another shelf item: kept, and relayed to the players near it.
    /// False if the player doesn't hold that scanner or the shelf item doesn't exist.
    /// </summary>
    public bool Aim(ServerPlayer player, ushort scannerNetId, byte shopIndex, byte moduleIndex)
    {
        if (!NetworkedItem.TryGet(scannerNetId, out NetworkedItem item) || item == null ||
            !item.TryGetComponent(out ShopScanner _) || !HeldBy(item, player))
            return false;
        if (moduleIndex != byte.MaxValue && !ShopRegisters.TryGetModule(shopIndex, moduleIndex, out _))
            return false;

        scannerAims[scannerNetId] = (shopIndex, moduleIndex);
        foreach (ServerPlayer other in server.ServerPlayers)
            if (other != player && (other.WorldPosition - item.transform.position).sqrMagnitude <= ScannerAimRange * ScannerAimRange)
                server.SendShopScannerAim(other, scannerNetId, shopIndex, moduleIndex);
        return true;
    }

    // One line after the other, as vanilla: the items appear on the counter one every 0.5 s.
    private IEnumerator Deliver(int shopIndex, Shop shop, IReadOnlyList<ShopBasketLine> bought)
    {
        foreach (ShopBasketLine line in bought)
        {
            if (!ShopRegisters.TryGetModule(shopIndex, line.Module, out ScanItemCashRegisterModule module))
                continue;
            server.TryGetServerPlayer(line.Owner, out ServerPlayer owner);
            yield return ShopDelivery.Spawn(module.sellingItemSpec.ItemPrefabName, shop, line.Units, owner);
        }

        ShopBasket basket = Basket(shopIndex);
        basket.SetDelivering(false);
        server.SendShopRegisterState(shopIndex, basket, ShopRegisterEvent.Delivered, 0);
    }

    // Units of this item in the basket, whichever of the shop's lines they're on.
    private static int UnitsOfItem(int shopIndex, ShopBasket basket, ShopItemData data) =>
        basket.Lines.Where(l => ShopRegisters.TryGetModule(shopIndex, l.Module, out ScanItemCashRegisterModule m) && ItemData(m) == data)
            .Sum(l => l.Units);

    /// <summary>Whether <paramref name="wanted"/> units of the item may be bought, and if not why.</summary>
    private static ShopRefusal Availability(ShopItemData data, int wanted)
    {
        if (data == null)
            return ShopRefusal.NotSoldHere;
        if (data.unavailableDueToGameMode)
            return ShopRefusal.NotAvailable;
        // Vanilla only applies these when a save loads (ModifyAmount): checked live here.
        if (data.item.TryGetComponent(out ShopRestockerWirelessMULoadException mu) && mu.license != null &&
            !LicenseManager.Instance.IsGeneralLicenseAcquired(mu.license))
            return ShopRefusal.NeedsLicence;
        if (data.item.TryGetComponent(out ShopRestockerGarageLoadException garage) && garage.garageType != null &&
            LicenseManager.Instance.IsGarageUnlocked(garage.garageType))
            return ShopRefusal.GarageUnlocked;
        return wanted > data.ItemsInStock ? ShopRefusal.OutOfStock : ShopRefusal.None;
    }

    private static ShopItemData ItemData(ScanItemCashRegisterModule module) =>
        module?.sellingItemSpec == null ? null : GlobalShopController.Instance.GetShopItemData(module.sellingItemSpec);

    private static double UnitPrice(int shopIndex, int module) =>
        ShopRegisters.TryGetModule(shopIndex, module, out ScanItemCashRegisterModule m) ? ItemData(m)?.basePrice ?? 0 : 0;

    private static string ItemName(int shopIndex, int module) =>
        ShopRegisters.TryGetModule(shopIndex, module, out ScanItemCashRegisterModule m) ? m.sellingItemSpec.ItemPrefabName : $"module {module}";

    // The shop scanner is an item: the server knows who holds each one.
    private static bool HoldsScanner(ServerPlayer player)
    {
        foreach (ShopScanner scanner in Resources.FindObjectsOfTypeAll<ShopScanner>())
        {
            if (scanner == null || !scanner.gameObject.scene.IsValid() ||
                !NetworkedItem.TryGetNetworkedItem(scanner.GetComponent<ItemBase>(), out NetworkedItem item) || item == null)
                continue;
            if (HeldBy(item, player))
                return true;
        }
        return false;
    }

    private static bool HeldBy(NetworkedItem item, ServerPlayer player) =>
        AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record) &&
        record.Placement == ItemPlacementKind.PlayerHand && record.PlacementPlayerId == player.PlayerId;
}
