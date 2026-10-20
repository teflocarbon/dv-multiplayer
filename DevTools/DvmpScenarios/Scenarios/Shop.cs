using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV.CabControls;
using DV.CashRegister;
using DV.InventorySystem;
using DV.Items;
using DV.Shops;
using Multiplayer.Server.Items;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Stations;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Shop sync (upstream #43/#103): a client buys an item and pays from the shared wallet. The
    /// host's wallet must pay (once), the item must exist for both players with one net ID, the stock
    /// must drop on both; a cancelled basket must hand the money back without printing a local
    /// banknote.
    /// </summary>
    [Scenario("shop", "The client buys from a shop and cancels a basket: the shared wallet pays once, the item and stock match on both, and nothing prints a local banknote")]
    [Budget(300)]
    public sealed class Shop : Scenario<Shop.Options>
    {
        public sealed class Options
        {
            /// <summary>The shelf item's name prefix.</summary>
            public string Item = "Lighter";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            // The shop by the station.
            await Places.Shop(pair, 2f);

            Report.Note($"client buys a {o.Item}");
            Bought b = await pair.Client.Run<Bought>(s => InGame.Buy(s, o.Item, false));
            Report.Note($"register {b.Distance} m away, ${b.Cost}, bought {b.Done}; client wallet {b.Start} -> {b.After}, stock {b.Stock0} -> {b.Stock1}, item net ID {string.Join(",", b.NetIds)}, banknotes printed {b.Banknotes}");
            Require.That(b.Done, "the purchase went through");
            Require.That(b.NetIds.Length == 1 && b.NetIds[0] != 0, "the client sees one bought item with a net ID", string.Join(",", b.NetIds));
            ushort id = b.NetIds[0];
            await Task.Delay(2000);
            HostView h = await pair.Host.Run(() => InGame.HostView(b.Prefab, id));
            Report.Note($"host: wallet {h.Money}, stock {h.Stock}, item seen {h.Seen}, owner P{h.Owner} (buyer P{h.Client})");
            Expect.That(Math.Abs(h.Money - (b.Start - b.Cost)) < 0.5, "the host's wallet paid the price", $"{h.Money}, expected {b.Start} - {b.Cost}");
            Expect.Equal(b.Stock1, h.Stock, "the host's stock matches the client's");
            Expect.That(h.Seen, "the host has the bought item");
            Expect.Equal(h.Client, h.Owner, "the bought item's owner is the buyer");
            Expect.Zero(b.Banknotes, "change printed as a local banknote");

            Report.Note("client pays and cancels");
            Bought c = await pair.Client.Run<Bought>(s => InGame.Buy(s, o.Item, true));
            await Task.Delay(2000);
            h = await pair.Host.Run(() => InGame.HostView(b.Prefab, 0));
            Report.Note($"host wallet {c.Start} -> {h.Money}, banknotes printed {c.Banknotes}");
            Expect.That(Math.Abs(h.Money - c.Start) < 0.5, "the cancel refunded the wallet", $"{h.Money}, expected {c.Start}");
            Expect.Zero(c.Banknotes, "the refund printed a local banknote");
        }

        public sealed class Bought
        {
            public double Distance, Cost, Start, After;
            public string Prefab;
            public bool Done;
            public int Stock0, Stock1, Banknotes;
            public ushort[] NetIds;
        }

        public sealed class HostView
        {
            public double Money;
            public int Stock, Owner, Client;
            public bool Seen;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Either player, at a shop: puts the item in the basket, pays a little over the price
            /// from the wallet and buys (or pays and cancels). Reports money, stock and the new items with
            /// their net IDs.</summary>
            [Step]
            public static IEnumerator Buy(Step<Bought> step, string item, bool cancel)
            {
                var reg = UnityEngine.Object.FindObjectsOfType<NetworkedCashRegisterWithModules>().Where(r => r.IsShopRegister).OrderBy(r => Vector3.Distance(r.transform.position, PlayerManager.PlayerTransform.position)).First();
                var mod = reg.CashRegister.registerModules.OfType<ScanItemCashRegisterModule>().First(m => m.name.StartsWith(item));
                var w = Resources.FindObjectsOfTypeAll<Wallet>().First(x => x.gameObject.scene.IsValid() && x.GetComponentInParent<Multiplayer.Shared.Players.NetworkedPlayer>() == null);
                double start = Inventory.Instance.PlayerMoney;
                int stock0 = GlobalShopController.Instance.GetShopItemData(mod.sellingItemSpec).ItemsInStock;
                var before = new HashSet<ItemBase>(UnityEngine.Object.FindObjectsOfType<ItemBase>());
                mod.AddItemsToBuy();
                double cost = reg.CashRegister.GetTotalCost();
                reg.CashRegister.AddCash(w.TrySpend(cost + 7));
                for (int i = 0; i < 60; i++) yield return null;
                bool bought = false;
                if (cancel) reg.CashRegister.Cancel(); else bought = reg.CashRegister.Buy();
                for (int i = 0; i < 300; i++) yield return null;
                var fresh = UnityEngine.Object.FindObjectsOfType<ItemBase>().Where(x => !before.Contains(x) && x.name.StartsWith(mod.sellingItemSpec.ItemPrefabName, StringComparison.OrdinalIgnoreCase)).ToList();
                step.Result = new Bought
                {
                    Distance = Math.Round(Vector3.Distance(reg.transform.position, PlayerManager.PlayerTransform.position)),
                    Prefab = mod.sellingItemSpec.ItemPrefabName, Cost = cost, Done = bought, Start = start, After = Inventory.Instance.PlayerMoney,
                    Stock0 = stock0, Stock1 = GlobalShopController.Instance.GetShopItemData(mod.sellingItemSpec).ItemsInStock,
                    NetIds = fresh.Select(x => x.GetComponent<NetworkedItem>()?.NetId ?? 0).ToArray(),
                    Banknotes = UnityEngine.Object.FindObjectsOfType<ItemBase>().Count(x => !before.Contains(x) && x.name.StartsWith("Banknotes"))
                };
            }

            /// <summary>Host: money, stock of the prefab, whether the item exists here and who owns it.</summary>
            [Step]
            public static HostView HostView(string prefab, ushort netId)
            {
                var d = GlobalShopController.Instance.GetShopItemData(prefab);
                AuthoritativeItemRegistry.TryGet(netId, out var rec);
                return new HostView
                {
                    Money = Inventory.Instance.PlayerMoney, Stock = d.ItemsInStock,
                    Seen = netId != 0 && UnityEngine.Object.FindObjectsOfType<NetworkedItem>().Any(n => n.NetId == netId),
                    Owner = rec?.PersistentOwnerPlayerId ?? 0,
                    Client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First().PlayerId
                };
            }
        }
    }
}
