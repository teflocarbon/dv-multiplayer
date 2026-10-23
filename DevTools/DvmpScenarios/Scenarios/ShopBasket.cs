using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV.CabControls;
using DV.CashRegister;
using DV.Interaction;
using DV.Shops;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Client.Shops;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// One basket per shop register, shared (docs/design/shops.md, PR 2). The client scans an item,
    /// and the server's basket and the client's register must match: lines, cash, revision. On a
    /// player host the host's own player then takes the scanner and scans another item, and the
    /// client presses Buy: each item is owned by whoever scanned it, and both games show the same
    /// register throughout. On a dedicated host (no player to share with) the client scans both
    /// items and owns both.
    /// </summary>
    [Scenario("shop-basket", "One shared basket per register: server and client registers match, and each bought item is owned by whoever scanned it")]
    [Budget(300)]
    public sealed class ShopBasket : Scenario<ShopBasket.Options>
    {
        public sealed class Options
        {
            /// <summary>The client's shelf item's name prefix.</summary>
            public string First = "Lighter";
            /// <summary>The second shelf item's name prefix (the host's on a player host).</summary>
            public string Second = "Stopwatch";
            /// <summary>Delay on every packet the client receives, in ms.</summary>
            public int Lag = 0;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Places.Shop(pair, 2f);
            using var lag = await pair.Client.Lag(o.Lag);
            await pair.Host.Run(() => Shop.InGame.ResetRegisters());
            pair.Cleanup(async () =>
            {
                await pair.Client.Run<bool>(s => Shop.InGame.ReturnHeldScanner(s));
                if (!pair.Dedicated) await pair.Host.Run<bool>(s => Shop.InGame.ReturnHeldScanner(s));
            });

            Scanned a =await pair.Client.Run<Scanned>(s => InGame.Scan(s, o.First));
            Report.Note($"client scanned {a.Prefab} at shop {a.Shop}: basket revision {a.Revision}, {a.Units} unit(s)");
            await Task.Delay(1000);
            await Compare(pair, a.Shop, "after the client's scan");

            Peer second = pair.Dedicated ? pair.Client : pair.Host;
            if (!pair.Dedicated)
            {
                await pair.Client.Run<bool>(s => InGame.DropHeld(s));
                Register hostShows = await pair.Host.Run(() => InGame.Show(a.Shop));
                Expect.Equal(1, hostShows.Units, "units on the host's register after the client's scan");
            }
            Scanned b = await second.Run<Scanned>(s => InGame.Scan(s, o.Second));
            Report.Note($"{(pair.Dedicated ? "client" : "host")} scanned {b.Prefab}: basket revision {b.Revision}, {b.Units} unit(s)");
            await Task.Delay(1000);
            await Compare(pair, a.Shop, "after the second scan");

            Paid p = await pair.Client.Run<Paid>(s => InGame.PayAndBuy(s, a.Shop, new[] { a.Prefab, b.Prefab }));
            Report.Note($"client paid ${p.Cost} and pressed Buy: items {string.Join(", ", p.Items.Select(i => $"{i.Prefab} {i.NetId}"))}");
            await Task.Delay(2000);
            foreach (BoughtItem item in p.Items)
            {
                int owner = await pair.Host.Run(() => InGame.Owner(item.NetId));
                int expected = item.Prefab == a.Prefab ? a.PlayerId : b.PlayerId;
                Expect.Equal(expected, owner, $"the owner of the {item.Prefab}");
            }
            Expect.Equal(2, p.Items.Length, "items bought");
            await Compare(pair, a.Shop, "after the purchase");
        }

        private static async Task Compare(Pair pair, int shop, string when)
        {
            Register server = await pair.Host.Run(() => InGame.Server(shop));
            Register client = await pair.Client.Run(() => InGame.Show(shop));
            Report.Note($"{when}: server {server.Units} unit(s), ${server.Deposited}, revision {server.Revision}; client {client.Units}, ${client.Deposited}, revision {client.Revision}");
            Expect.Equal(server.Units, client.Units, $"units on the client's register {when}");
            Expect.That(Math.Abs(server.Deposited - client.Deposited) < 0.01, $"cash on the client's register {when}", $"{client.Deposited}, expected {server.Deposited}");
            Expect.Equal(server.Revision, client.Revision, $"the client's basket revision {when}");
        }

        public sealed class Scanned
        {
            public int Shop, Units, PlayerId;
            public uint Revision;
            public string Prefab;
        }

        public sealed class Register
        {
            public int Units;
            public double Deposited;
            public uint Revision;
        }

        public sealed class BoughtItem
        {
            public string Prefab;
            public ushort NetId;
        }

        public sealed class Paid
        {
            public double Cost;
            public BoughtItem[] Items;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static global::Shop NearestShop() => GlobalShopController.Instance.globalShopList
                .OrderBy(s => Vector3.Distance(s.cashRegister.transform.position, PlayerManager.PlayerTransform.position)).First();

            /// <summary>Picks up the nearest shop's scanner and scans one of the item.</summary>
            [Step]
            public static IEnumerator Scan(Step<Scanned> step, string item)
            {
                global::Shop shop = NearestShop();
                int shopIndex = GlobalShopController.Instance.globalShopList.IndexOf(shop);
                ScanItemCashRegisterModule mod = shop.scanItemResourceModules.First(m => m.name.StartsWith(item));
                yield return Shop.InGame.TakeScanner(shop);
                yield return Shop.InGame.StandAt(mod.transform);
                float units = mod.Data.unitsToBuy;
                mod.AddItemsToBuy();
                yield return Wait.Until(() => mod.Data.unitsToBuy > units, 10f, "the scanned item on the register");
                step.Result = new Scanned
                {
                    Shop = shopIndex, Prefab = mod.sellingItemSpec.ItemPrefabName, Units = (int)mod.Data.unitsToBuy,
                    Revision = ShopRegisterView.Revision(shopIndex), PlayerId = NetworkLifecycle.Instance.LocalSession?.PlayerId ?? 0
                };
            }

            [Step]
            public static IEnumerator DropHeld(Step<bool> step)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                step.Result = true;
            }

            /// <summary>Pays what is owed from the wallet and presses Buy; the new items of those prefabs.</summary>
            [Step]
            public static IEnumerator PayAndBuy(Step<Paid> step, int shopIndex, string[] prefabs)
            {
                CashRegisterWithModules reg = GlobalShopController.Instance.globalShopList[shopIndex].cashRegister;
                yield return Shop.InGame.StandAt(reg.transform);

                var before = new HashSet<ItemBase>(UnityEngine.Object.FindObjectsOfType<ItemBase>());
                double cost = reg.GetTotalCost();
                if (cost > reg.DepositedCash)
                {
                    Wallet w = Resources.FindObjectsOfTypeAll<Wallet>().First(x => x.gameObject.scene.IsValid() && x.GetComponentInParent<Multiplayer.Shared.Players.NetworkedPlayer>() == null);
                    Shop.InGame.WalletUse(w).HandleUse(reg.GetComponent<ItemUseTarget>());
                    yield return Wait.Until(() => reg.DepositedCash >= cost, 10f, "the payment on the register");
                }
                reg.OnBuyPressed();
                // Two items, 0.5 s apart.
                yield return Wait.Until(() => UnityEngine.Object.FindObjectsOfType<ItemBase>().Count(x => !before.Contains(x) &&
                    prefabs.Any(p => x.name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) >= prefabs.Length, 15f, "the bought items");
                step.Result = new Paid
                {
                    Cost = cost,
                    Items = UnityEngine.Object.FindObjectsOfType<ItemBase>().Where(x => !before.Contains(x))
                        .Select(x => new BoughtItem
                        {
                            Prefab = prefabs.FirstOrDefault(p => x.name.StartsWith(p, StringComparison.OrdinalIgnoreCase)),
                            NetId = x.GetComponent<NetworkedItem>()?.NetId ?? 0
                        })
                        .Where(i => i.Prefab != null).ToArray()
                };
            }

            /// <summary>The register as this game shows it.</summary>
            [Step]
            public static Register Show(int shop)
            {
                CashRegisterWithModules reg = GlobalShopController.Instance.globalShopList[shop].cashRegister;
                return new Register { Units = (int)reg.TotalUnitsInBasket(), Deposited = reg.DepositedCash, Revision = ShopRegisterView.Revision(shop) };
            }

            /// <summary>Host: the server's register.</summary>
            [Step]
            public static Register Server(int shop)
            {
                var basket = NetworkServer.Instance.Shops.Basket(shop);
                return new Register { Units = basket.Lines.Sum(l => l.Units), Deposited = basket.Deposited, Revision = basket.Revision };
            }

            [Step]
            public static int Owner(ushort netId) =>
                LiveItems.TryGet(netId, out var record) ? record.PersistentOwnerPlayerId : -1;
        }
    }
}
