using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV.CabControls;
using DV.CashRegister;
using DV.Interaction;
using DV.InventorySystem;
using DV.Shops;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Client.Shops;
using Multiplayer.Server.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A purchase through the server's shop register (docs/design/shops.md, PR 2): a player picks up
    /// the shop's scanner, scans an item, pays from the shared wallet and presses Buy. The wallet
    /// pays once, the item exists for both players with one net ID, owned by the buyer from birth,
    /// the stock drops on both, and both games show the same register. Then a paid basket is
    /// cancelled: its cash comes out as one banknote, the same item on both games. WHO=host has the
    /// host's own player buy, through the same requests (player host only).
    /// </summary>
    [Scenario("shop", "A player scans, pays and buys at a shop: the wallet pays once, the item and stock match on both, owned by the buyer; a cancelled basket's cash is one banknote on both (WHO=host: the host's own player)")]
    [Budget(300)]
    public sealed class Shop : Scenario<Shop.Options>
    {
        public sealed class Options
        {
            /// <summary>The shelf item's name prefix.</summary>
            public string Item = "Lighter";
            /// <summary>Who buys: client or host.</summary>
            public string Who = "client";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host: a dedicated host has no player to shop");
            Peer buyer = o.Who == "host" ? pair.Host : pair.Client;
            Peer other = o.Who == "host" ? pair.Client : pair.Host;
            await Places.Shop(pair, 2f);
            await pair.Host.Run(() => InGame.ResetRegisters());

            Report.Note($"{o.Who} buys a {o.Item}");
            Bought b = await buyer.Run<Bought>(s => InGame.Buy(s, o.Item, false));
            Report.Note($"register {b.Distance} m away, ${b.Cost}, bought {b.Done}; wallet {b.Start} -> {b.After}, stock {b.Stock0} -> {b.Stock1}, item net ID {string.Join(",", b.NetIds)}, banknotes printed {b.Banknotes}");
            Require.That(b.Done, "the purchase went through");
            Require.That(b.NetIds.Length == 1 && b.NetIds[0] != 0, "the buyer sees one bought item with a net ID", string.Join(",", b.NetIds));
            ushort id = b.NetIds[0];
            await Task.Delay(2000);
            HostView h = await pair.Host.Run(() => InGame.HostView(b.Prefab, id, b.Shop));
            Report.Note($"host: wallet {h.Money}, stock {h.Stock}, item seen {h.Seen}, owner P{h.Owner} (buyer P{b.PlayerId}), server basket {h.BasketUnits} units, ${h.Deposited}");
            Expect.That(Math.Abs(h.Money - (b.Start - b.Cost)) < 0.01, "the host's wallet paid the price", $"{h.Money}, expected {b.Start} - {b.Cost}");
            Expect.Equal(b.Stock1, h.Stock, "the host's stock matches the buyer's");
            Expect.That(h.Seen, "the host has the bought item");
            Expect.Equal(b.PlayerId, h.Owner, "the bought item's owner is the buyer");
            Expect.Zero(h.BasketUnits, "units left in the server's basket");
            Expect.Zero(b.Banknotes, "banknotes printed for a wallet payment");
            if (!pair.Dedicated)
            {
                Register r = await other.Run(() => InGame.ShowRegister(b.Shop, id));
                Report.Note($"{(o.Who == "host" ? "client" : "host")}'s register: {r.Units} units, ${r.Deposited}; item seen {r.Seen}");
                Expect.That(r.Seen, "the other player has the bought item");
                Expect.Zero(r.Units, "units left on the other player's register");
            }

            Report.Note($"{o.Who} scans, pays and cancels");
            Bought c = await buyer.Run<Bought>(s => InGame.Buy(s, o.Item, true));
            await Task.Delay(2000);
            h = await pair.Host.Run(() => InGame.HostView(b.Prefab, 0, b.Shop));
            Report.Note($"wallet {c.Start} -> {h.Money}, deposit ${c.Deposited}, banknotes printed {c.Banknotes} (net IDs {string.Join(",", c.NoteIds)}, ${c.NoteValue}), server basket {h.BasketUnits} units, ${h.Deposited}");
            Expect.Zero(h.BasketUnits, "units left in the server's basket after the cancel");
            Expect.Zero(h.Deposited, "cash left in the server's register after the cancel");
            Expect.Equal(c.Deposited > 0 ? 1 : 0, c.Banknotes, "banknotes printed by the cancel");
            Expect.That(Math.Abs(h.Money + c.NoteValue - c.Start) < 0.01, "the wallet and the cancel's banknote add up to the wallet before", $"{h.Money} + {c.NoteValue}, expected {c.Start}");
            if (c.NoteIds.Length == 1)
            {
                bool seen = await other.Run(() => InGame.Has(c.NoteIds[0]));
                Expect.That(seen, "the other game has the cancel's banknote");
            }
        }

        public sealed class Bought
        {
            public double Distance, Cost, Start, After, Deposited, NoteValue;
            public string Prefab;
            public bool Done;
            public int Shop, Stock0, Stock1, Banknotes, PlayerId;
            public ushort[] NetIds, NoteIds;
        }

        public sealed class HostView
        {
            public double Money, Deposited;
            public int Stock, Owner, BasketUnits;
            public bool Seen;
        }

        public sealed class Register
        {
            public int Units;
            public double Deposited;
            public bool Seen;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Either player, at a shop: picks up the scanner, scans the item, pays what is
            /// owed from the wallet and presses Buy (or Cancel). Reports money, stock and the new items
            /// and banknotes with their net IDs.</summary>
            [Step]
            public static IEnumerator Buy(Step<Bought> step, string item, bool cancel)
            {
                var shops = GlobalShopController.Instance.globalShopList;
                global::Shop shop = shops.OrderBy(s => Vector3.Distance(s.cashRegister.transform.position, PlayerManager.PlayerTransform.position)).First();
                int shopIndex = shops.IndexOf(shop);
                CashRegisterWithModules reg = shop.cashRegister;
                ScanItemCashRegisterModule mod = shop.scanItemResourceModules.First(m => m.name.StartsWith(item));
                Wallet w = Resources.FindObjectsOfTypeAll<Wallet>().First(x => x.gameObject.scene.IsValid() && x.GetComponentInParent<Multiplayer.Shared.Players.NetworkedPlayer>() == null);

                yield return TakeScanner(shop);
                double start = Inventory.Instance.PlayerMoney;
                int stock0 = GlobalShopController.Instance.GetShopItemData(mod.sellingItemSpec).ItemsInStock;
                var before = new HashSet<ItemBase>(UnityEngine.Object.FindObjectsOfType<ItemBase>());

                // Scan from beside the shelf item, then pay at the register, as a player does.
                yield return StandAt(mod.transform);
                mod.AddItemsToBuy();
                yield return Wait.Until(() => mod.Data.unitsToBuy >= 1, 10f, "the scanned item on the register");
                yield return StandAt(reg.transform);
                double cost = reg.GetTotalCost();
                if (cost > 0)
                {
                    Shop.InGame.WalletUse(w).HandleUse(reg.GetComponent<ItemUseTarget>());
                    yield return Wait.Until(() => reg.DepositedCash >= cost, 10f, "the payment on the register");
                }
                double deposited = reg.DepositedCash;

                if (cancel) reg.Cancel(); else reg.OnBuyPressed();
                for (int i = 0; i < 300; i++) yield return null;

                var fresh = UnityEngine.Object.FindObjectsOfType<ItemBase>().Where(x => !before.Contains(x)).ToList();
                var notes = fresh.Where(x => x.name.StartsWith("Banknotes")).ToList();
                step.Result = new Bought
                {
                    Distance = Math.Round(Vector3.Distance(reg.transform.position, PlayerManager.PlayerTransform.position)),
                    Prefab = mod.sellingItemSpec.ItemPrefabName, Cost = cost, Deposited = deposited,
                    Done = !cancel && mod.Data.unitsToBuy == 0, Start = start, After = Inventory.Instance.PlayerMoney,
                    Shop = shopIndex, PlayerId = NetworkLifecycle.Instance.LocalSession?.PlayerId ?? 0,
                    Stock0 = stock0, Stock1 = GlobalShopController.Instance.GetShopItemData(mod.sellingItemSpec).ItemsInStock,
                    NetIds = fresh.Where(x => x.name.StartsWith(mod.sellingItemSpec.ItemPrefabName, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.GetComponent<NetworkedItem>()?.NetId ?? 0).ToArray(),
                    Banknotes = notes.Count,
                    NoteIds = notes.Select(x => x.GetComponent<NetworkedItem>()?.NetId ?? 0).ToArray(),
                    NoteValue = notes.Sum(x => x.GetComponent<IMoney>()?.Amount ?? 0)
                };
                yield return ReturnScanner(shop);
            }

            /// <summary>
            /// The shop's scanner in this player's hand: from the counter, or from their inventory,
            /// where an earlier run left it (a player who logs out holding it takes it with them).
            /// </summary>
            public static IEnumerator TakeScanner(global::Shop shop)
            {
                if (Hands.Held != null && Hands.Held.GetComponent<ShopScanner>() != null)
                    yield break;
                if (Hands.Held != null) yield return Hands.Drop();
                NetworkedItem AtTheShop() => UnityEngine.Object.FindObjectsOfType<ShopScanner>()
                    .Where(s => Vector3.Distance(s.transform.position, shop.cashRegister.transform.position) < 30f)
                    .Select(s => s.GetComponent<NetworkedItem>()).FirstOrDefault(n => n != null && n.NetId != 0);
                NetworkedItem scanner = AtTheShop();
                GameObject carried = Inventory.Instance.GetItemsArray().FirstOrDefault(g => g != null && g.GetComponent<ShopScanner>() != null);
                if (scanner == null && carried != null)
                {
                    Inventory.Instance.DropItemFromHandsOrInventory(carried);
                    for (int i = 0; i < 60; i++) yield return null;
                    scanner = AtTheShop();
                }
                if (scanner == null)
                    throw new InvalidOperationException("no shop scanner at the shop or in the inventory");
                yield return LocalPlayer.GoTo(scanner);
                yield return Hands.Pickup(scanner.NetId);
            }

            /// <summary>Puts a held scanner back by the register, where the next player (and run) looks.</summary>
            public static IEnumerator ReturnScanner(global::Shop shop)
            {
                if (Hands.Held == null || Hands.Held.GetComponent<ShopScanner>() == null)
                    yield break;
                yield return StandAt(shop.cashRegister.transform);
                yield return Hands.Drop();
            }

            /// <summary>Puts a held scanner back by the nearest shop's register.</summary>
            [Step]
            public static IEnumerator ReturnHeldScanner(Step<bool> step)
            {
                global::Shop shop = GlobalShopController.Instance.globalShopList
                    .OrderBy(s => Vector3.Distance(s.cashRegister.transform.position, PlayerManager.PlayerTransform.position)).First();
                yield return ReturnScanner(shop);
                step.Result = true;
            }

            /// <summary>Host: every shop register empty, its cash back in the wallet, so a run starts clean.</summary>
            [Step]
            public static void ResetRegisters()
            {
                foreach (var entry in NetworkServer.Instance.Shops.Baskets.ToArray())
                {
                    entry.Value.ClearLines();
                    Inventory.Instance.AddMoney(entry.Value.TakeDeposit());
                    NetworkServer.Instance.SendShopRegisterState(entry.Key, entry.Value, Multiplayer.Core.Shops.ShopRegisterEvent.None, 0);
                }
            }

            /// <summary>
            /// The wallet's "use on a register" action. Vanilla adds it in <c>Wallet.Start</c>, which a
            /// wallet that has stayed in the inventory since a save loaded hasn't run (a player takes it
            /// out first, which runs it): add it as Start would.
            /// </summary>
            public static MoneyUse WalletUse(Wallet wallet) =>
                wallet.GetComponent<MoneyUse>() ?? wallet.gameObject.AddComponent<MoneyUse>();

            // A metre from something, as a player using it stands.
            public static IEnumerator StandAt(Transform target)
            {
                Vector3 at = Dev.Abs(target.position);
                Vector3 from = Dev.PlayerAbs ?? at;
                Vector3 flat = new Vector3(from.x - at.x, 0, from.z - at.z);
                Vector3 offset = flat.sqrMagnitude > 0.01f ? flat.normalized * 1f : new Vector3(1f, 0, 0);
                yield return LocalPlayer.TeleportAbs(new Vector3(at.x + offset.x, from.y, at.z + offset.z), 0.5f);
            }

            /// <summary>Host: money, stock of the prefab, whether the item exists here and who owns it,
            /// and the server's basket at the shop.</summary>
            [Step]
            public static HostView HostView(string prefab, ushort netId, int shop = -1)
            {
                var d = GlobalShopController.Instance.GetShopItemData(prefab);
                AuthoritativeItemRegistry.TryGet(netId, out var rec);
                var basket = shop >= 0 ? NetworkServer.Instance.Shops.Basket(shop) : null;
                return new HostView
                {
                    Money = Inventory.Instance.PlayerMoney, Stock = d.ItemsInStock,
                    Seen = netId != 0 && UnityEngine.Object.FindObjectsOfType<NetworkedItem>().Any(n => n.NetId == netId),
                    Owner = rec?.PersistentOwnerPlayerId ?? 0,
                    BasketUnits = basket?.Lines.Sum(l => l.Units) ?? 0, Deposited = basket?.Deposited ?? 0
                };
            }

            /// <summary>The shop's register as this game shows it, and whether it has the item.</summary>
            [Step]
            public static Register ShowRegister(int shop, ushort netId)
            {
                CashRegisterWithModules reg = GlobalShopController.Instance.globalShopList[shop].cashRegister;
                return new Register
                {
                    Units = (int)reg.TotalUnitsInBasket(), Deposited = reg.DepositedCash,
                    Seen = netId != 0 && NetworkedItem.TryGet(netId, out NetworkedItem n) && n != null
                };
            }

            [Step]
            public static bool Has(ushort netId) => NetworkedItem.TryGet(netId, out NetworkedItem n) && n != null;
        }
    }
}
