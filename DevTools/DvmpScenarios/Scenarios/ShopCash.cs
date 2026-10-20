using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.CashRegister;
using DV.Interaction;
using DV.InventorySystem;
using DV.Shops;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Cash nobody is using (docs/design/shops.md, PR 2b). The client scans an item and pays from
    /// the wallet without buying. The save then counts the till's cash as the wallet's, as vanilla
    /// does, on a dedicated host too, where no vanilla register holds it. The client walks 30 m
    /// away, and the cash is back in the wallet within a few seconds, on both games. Then it walks
    /// 300 m away, and after a minute the basket is empty. Needs a career save: sandbox prices are 0.
    /// </summary>
    [Scenario("shop-cash", "Cash in a shop register: the save counts it, walking away returns it to the wallet, and leaving the shop empties the basket")]
    [Budget(300)]
    public sealed class ShopCash : Scenario<ShopCash.Options>
    {
        public sealed class Options
        {
            /// <summary>The shelf item's name prefix.</summary>
            public string Item = "Lighter";
            /// <summary>Also wait out the minute after which an abandoned basket empties.</summary>
            public bool Leave = true;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Places.Shop(pair, 2f);
            Paid p = await pair.Client.Run<Paid>(s => InGame.ScanAndPay(s, o.Item));
            if (p.Cost <= 0) throw new HarnessException("shop-cash needs a career save: this save's prices are 0");
            await Task.Delay(1000);
            Till t = await pair.Host.Run(() => InGame.Server(p.Shop));
            Report.Note($"client paid ${p.Deposited} of ${p.Cost}; wallet {p.Start} -> {t.Wallet}, server till ${t.Deposited}, {t.Units} unit(s); the save would hold ${t.Saved}");
            Require.That(Math.Abs(t.Deposited - p.Cost) < 0.01, "the server's till holds the payment", $"{t.Deposited}, expected {p.Cost}");
            Expect.That(Math.Abs(t.Saved - p.Start) < 1, "the save counts the till's cash as the wallet's", $"{t.Saved}, expected {p.Start}");

            Report.Note("client walks 30 m away");
            await pair.Client.Run<bool>(s => InGame.WalkAway(s, p.Shop, 30f));
            await Task.Delay(5000);
            t = await pair.Host.Run(() => InGame.Server(p.Shop));
            double shown = await pair.Client.Run(() => InGame.ClientTill(p.Shop));
            Report.Note($"server till ${t.Deposited}, client's register ${shown}, wallet {t.Wallet}, {t.Units} unit(s)");
            Expect.Zero(t.Deposited, "cash left in the server's till");
            Expect.Zero(shown, "cash on the client's register");
            Expect.That(Math.Abs(t.Wallet - p.Start) < 0.01, "the wallet got the cash back", $"{t.Wallet}, expected {p.Start}");
            Expect.Equal(1, t.Units, "units still in the basket after walking away");

            if (!o.Leave)
                return;
            Report.Note("client walks 300 m away and stays a minute");
            await pair.Client.Run<bool>(s => InGame.WalkAway(s, p.Shop, 300f));
            if (!pair.Dedicated)
                await pair.Host.Run<bool>(s => InGame.WalkAway(s, p.Shop, 300f));
            await Task.Delay(70000);
            t = await pair.Host.Run(() => InGame.Server(p.Shop));
            Report.Note($"after a minute away: server basket {t.Units} unit(s)");
            Expect.Zero(t.Units, "units left in an abandoned basket");
        }

        public sealed class Paid
        {
            public int Shop;
            public double Cost, Deposited, Start;
        }

        public sealed class Till
        {
            public double Deposited, Wallet, Saved;
            public int Units;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Picks up the scanner, scans the item and pays what is owed from the wallet.</summary>
            [Step]
            public static IEnumerator ScanAndPay(Step<Paid> step, string item)
            {
                var shops = GlobalShopController.Instance.globalShopList;
                global::Shop shop = shops.OrderBy(s => Vector3.Distance(s.cashRegister.transform.position, PlayerManager.PlayerTransform.position)).First();
                CashRegisterWithModules reg = shop.cashRegister;
                ScanItemCashRegisterModule mod = shop.scanItemResourceModules.First(m => m.name.StartsWith(item));
                var scanner = UnityEngine.Object.FindObjectsOfType<ShopScanner>()
                    .OrderBy(s => Vector3.Distance(s.transform.position, reg.transform.position))
                    .Select(s => s.GetComponent<Multiplayer.Shared.Items.NetworkedItem>()).First(n => n != null);
                if (Hands.Held != scanner)
                {
                    if (Hands.Held != null) yield return Hands.Drop();
                    yield return LocalPlayer.GoTo(scanner);
                    yield return Hands.Pickup(scanner.NetId);
                }
                double start = Inventory.Instance.PlayerMoney;
                yield return Shop.InGame.StandAt(mod.transform);
                mod.AddItemsToBuy();
                yield return Wait.Until(() => mod.Data.unitsToBuy >= 1, 10f, "the scanned item on the register");
                yield return Shop.InGame.StandAt(reg.transform);
                double cost = reg.GetTotalCost();
                if (cost > 0)
                {
                    Wallet w = Resources.FindObjectsOfTypeAll<Wallet>().First(x => x.gameObject.scene.IsValid() && x.GetComponentInParent<Multiplayer.Shared.Players.NetworkedPlayer>() == null);
                    w.GetComponent<MoneyUse>().HandleUse(reg.GetComponent<ItemUseTarget>());
                    yield return Wait.Until(() => reg.DepositedCash >= cost, 10f, "the payment on the register");
                }
                step.Result = new Paid { Shop = shops.IndexOf(shop), Cost = cost, Deposited = reg.DepositedCash, Start = start };
            }

            /// <summary>Moves this game's player <paramref name="metres"/> from the shop's register.</summary>
            [Step]
            public static IEnumerator WalkAway(Step<bool> step, int shop, float metres)
            {
                Vector3 at = Dev.Abs(GlobalShopController.Instance.globalShopList[shop].cashRegister.transform.position);
                yield return LocalPlayer.TeleportToGround(at.x + metres, at.z, 1f);
                step.Result = true;
            }

            /// <summary>Host: the server's till, the wallet, and the money the save would hold.</summary>
            [Step]
            public static Till Server(int shop)
            {
                var basket = NetworkServer.Instance.Shops.Basket(shop);
                SaveGameManager.Instance.UpdateInternalData();
                return new Till
                {
                    Deposited = basket.Deposited, Units = basket.Lines.Sum(l => l.Units), Wallet = Inventory.Instance.PlayerMoney,
                    Saved = SaveGameManager.Instance.data.GetFloat(SaveGameKeys.Player_money) ?? -1
                };
            }

            [Step]
            public static double ClientTill(int shop) => GlobalShopController.Instance.globalShopList[shop].cashRegister.DepositedCash;
        }
    }
}
