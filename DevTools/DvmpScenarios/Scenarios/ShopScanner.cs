using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.CabControls;
using DV.Shops;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Another player's shop scanner (docs/design/shops.md, PR 5). The client takes the scanner and
    /// points it at a shelf item. The server keeps what it points at. On a player host the host's own
    /// game shows the client's laser and the scanner's screen naming that item, and clears the
    /// screen when it points at nothing. When the client lets go, the host's game shows no laser.
    /// On a dedicated host (no player to see it) the server's record of the aim is checked.
    /// </summary>
    [Scenario("shop-scanner", "The client points the shop's scanner at a shelf item: the server has its aim, and on a player host the host's game shows the laser and the scanner's screen")]
    [Budget(240)]
    public sealed class ShopScannerScenario : Scenario<ShopScannerScenario.Options>
    {
        public sealed class Options
        {
            /// <summary>The shelf item's name prefix.</summary>
            public string Item = "Lighter";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Places.Shop(pair, 2f);
            Aimed a = await pair.Client.Run<Aimed>(s => InGame.TakeAndAim(s, o.Item));
            Report.Note($"client holds scanner {a.Scanner} and points it at {a.Name} (shop {a.Shop}, item {a.Module})");
            await Task.Delay(1500);

            Server s1 = await pair.Host.Run(() => InGame.ServerAim(a.Scanner));
            Report.Note($"server: scanner {a.Scanner} points at shop {s1.Shop} item {s1.Module}");
            Expect.Equal(a.Module, s1.Module, "the shelf item the server has the client's scanner pointing at");

            if (!pair.Dedicated)
            {
                Seen h = await pair.Host.Run(() => InGame.Look(a.Scanner));
                Report.Note($"host's game: laser {h.Laser}, screen '{h.Name}' '{h.Amount}' '{h.Price}'");
                Expect.That(h.Laser, "the host's game shows the client's scanner's laser");
                Expect.That(h.Shown, "the host's game shows the scanner's screen");
                Expect.That(h.Name.StartsWith(a.Name), "the host's game shows the aimed item's name on the scanner", h.Name);
                Expect.That(h.Amount.Contains("/"), "the host's game shows units against stock on the scanner", h.Amount);
            }

            await pair.Client.Run<bool>(s => InGame.LookAway(s, a.Shop, a.Module));
            await Task.Delay(1500);
            Server s2 = await pair.Host.Run(() => InGame.ServerAim(a.Scanner));
            Expect.Equal(255, s2.Module, "the server's record after the client points at nothing");
            if (!pair.Dedicated)
            {
                Seen h = await pair.Host.Run(() => InGame.Look(a.Scanner));
                Report.Note($"host's game after pointing at nothing: screen shown {h.Shown}");
                Expect.That(!h.Shown, "the host's game cleared the scanner's screen");
            }

            await pair.Client.Run<bool>(s => InGame.Drop(s));
            await Task.Delay(1500);
            if (!pair.Dedicated)
            {
                Seen h = await pair.Host.Run(() => InGame.Look(a.Scanner));
                Report.Note($"host's game after the client let go: laser {h.Laser}");
                Expect.That(!h.Laser, "the host's game shows no laser once the client lets go");
            }
        }

        public sealed class Aimed
        {
            public ushort Scanner;
            public int Shop, Module;
            public string Name;
        }

        public sealed class Server
        {
            public int Shop, Module;
        }

        public sealed class Seen
        {
            public bool Laser, Shown;
            public string Name, Amount, Price;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static global::Shop NearestShop() => GlobalShopController.Instance.globalShopList
                .OrderBy(s => Vector3.Distance(s.cashRegister.transform.position, PlayerManager.PlayerTransform.position)).First();

            private static ShopScanner Held() => Hands.Held != null ? Hands.Held.GetComponent<ShopScanner>() : null;

            /// <summary>Picks up the nearest shop's scanner and points it at the item.</summary>
            [Step]
            public static IEnumerator TakeAndAim(Step<Aimed> step, string item)
            {
                global::Shop shop = NearestShop();
                if (Held() == null)
                {
                    NetworkedItem scanner = UnityEngine.Object.FindObjectsOfType<ShopScanner>()
                        .Where(s => Vector3.Distance(s.transform.position, shop.cashRegister.transform.position) < 30f)
                        .Select(s => s.GetComponent<NetworkedItem>()).FirstOrDefault(n => n != null && n.NetId != 0)
                        ?? throw new InvalidOperationException("no shop scanner at the shop (use a fresh career)");
                    if (Hands.Held != null) yield return Hands.Drop();
                    yield return LocalPlayer.GoTo(scanner);
                    yield return Hands.Pickup(scanner.NetId);
                }
                int shopIndex = GlobalShopController.Instance.globalShopList.IndexOf(shop);
                int module = Array.FindIndex(shop.scanItemResourceModules, m => m.name.StartsWith(item));
                ScanItemCashRegisterModule target = shop.scanItemResourceModules[module];
                yield return Shop.InGame.StandAt(target.transform);
                yield return Look(target.transform, true);
                ShopScanner held = Held();
                yield return Wait.Until(() => held.currentlyHoveredScanItem == target, 5f, "the scanner to point at the shelf item");
                step.Result = new Aimed
                {
                    Scanner = Held().GetComponent<NetworkedItem>().NetId, Shop = shopIndex, Module = module,
                    Name = shop.scanItemResourceModules[module].sellingItemSpec.LocalizedName
                };
            }

            /// <summary>Turns the player towards <paramref name="target"/> (or away from it): vanilla's scanner raycasts from the camera.</summary>
            private static IEnumerator Look(Transform target, bool towards)
            {
                var controller = UnityEngine.Object.FindObjectOfType<CustomFirstPersonController>();
                Camera camera = PlayerManager.ActiveCamera;
                if (controller == null || camera == null) throw new InvalidOperationException("no first-person controller to turn");
                Collider collider = target.GetComponentsInChildren<Collider>(true).FirstOrDefault(c => c != null && c.enabled);
                Vector3 direction = (collider != null ? collider.bounds.center : target.position) - camera.transform.position;
                if (!towards) direction = -direction;
                Quaternion rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
                for (int frame = 0; frame < 4; frame++)
                {
                    controller.ForceLookRotationNoTilt(rotation);
                    yield return null;
                }
            }

            /// <summary>The player looks away from the shelf item: the held scanner points at nothing.</summary>
            [Step]
            public static IEnumerator LookAway(Step<bool> step, int shop, int module)
            {
                ShopScanner scanner = Held() ?? throw new InvalidOperationException("not holding a scanner");
                yield return Look(GlobalShopController.Instance.globalShopList[shop].scanItemResourceModules[module].transform, false);
                yield return Wait.Until(() => scanner.currentlyHoveredScanItem == null, 5f, "the scanner to point at nothing");
                step.Result = true;
            }

            [Step]
            public static IEnumerator Drop(Step<bool> step)
            {
                // Put it back by the register, where the next run looks for it.
                global::Shop shop = NearestShop();
                yield return Shop.InGame.StandAt(shop.cashRegister.transform);
                if (Hands.Held != null) yield return Hands.Drop();
                step.Result = true;
            }

            /// <summary>Host: what the server has the scanner pointing at (module 255: nothing, -1: no record).</summary>
            [Step]
            public static Server ServerAim(ushort scanner) =>
                NetworkServer.Instance.Shops.ScannerAims.TryGetValue(scanner, out var aim)
                    ? new Server { Shop = aim.Shop, Module = aim.Module }
                    : new Server { Shop = -1, Module = -1 };

            /// <summary>This game's view of the scanner: its laser and its screen.</summary>
            [Step]
            public static Seen Look(ushort scanner)
            {
                ShopScanner s = NetworkedItem.TryGet(scanner, out NetworkedItem item) ? item.GetComponent<ShopScanner>() : null;
                if (s == null) throw new InvalidOperationException($"no scanner {scanner} on this game");
                return new Seen
                {
                    Laser = s.laserBeam != null && s.laserBeam.gameObject.activeSelf,
                    Shown = s.nameText.enabled,
                    Name = s.nameText.text, Amount = s.amountText.text, Price = s.priceText.text
                };
            }
        }
    }
}
