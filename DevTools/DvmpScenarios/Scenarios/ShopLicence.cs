using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Shops;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A licence-gated shop item on a client (docs/design/shops.md, gap 7). A client never ran
    /// vanilla's load-time rule that takes the wireless MU controller off sale without the MU
    /// licence, so it showed and scanned as for sale, and the server refused the purchase silently.
    /// Now the stock the server sends says none may exist: without the licence the client sees it as
    /// unavailable and its scan is refused. The host then acquires the licence: the client sees it for
    /// sale and its scan goes in. Needs a save without the MU licence (a fresh career).
    /// </summary>
    [Scenario("shop-licence", "Without the MU licence the client can't scan the wireless MU controller; with it, it can")]
    [Budget(240)]
    public sealed class ShopLicence : Scenario<ShopLicence.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            Gated g = await pair.Host.Run(() => InGame.FindGated());
            if (g.Shop < 0) throw new HarnessException("no shop sells a licence-gated item");
            if (g.Held) throw new HarnessException($"the save already has the {g.Licence} licence: use a fresh career");
            Report.Note($"{g.Prefab} at shop {g.Shop}, needs {g.Licence}");
            await Places.Both(pair, g.X + 3f, g.Z + 4f, g.X, g.Z, 3f);
            pair.Cleanup(() => pair.Host.Run(() => InGame.Licence(false)));

            Scan before = await pair.Client.Run<Scan>(s => InGame.TryScan(s, g.Shop, g.Module));
            Report.Note($"without the licence: client allows {before.Allowed}, in stock {before.InStock}, scan put {before.Units} in the basket");
            Expect.Zero(before.Allowed, "how many the client thinks may exist without the licence");
            Expect.Zero(before.Units, "units scanned without the licence");

            await pair.Host.Run(() => InGame.Licence(true));
            await Task.Delay(2000);
            Scan after = await pair.Client.Run<Scan>(s => InGame.TryScan(s, g.Shop, g.Module));
            Report.Note($"with the licence: client allows {after.Allowed}, in stock {after.InStock}, scan put {after.Units} in the basket");
            Expect.That(after.Allowed > 0, "the client sees it may exist with the licence", after.Allowed.ToString());
            Expect.Equal(1, after.Units, "units scanned with the licence");
            await pair.Client.Run(() => InGame.Cancel(g.Shop));
        }

        public sealed class Gated
        {
            public int Shop = -1, Module;
            public string Prefab, Licence;
            public bool Held;
            public float X, Z;
        }

        public sealed class Scan
        {
            public int Allowed, InStock, Units;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static ShopRestockerWirelessMULoadException Gate(ScanItemCashRegisterModule m) =>
                m?.sellingItemSpec?.GetComponent<ShopRestockerWirelessMULoadException>();

            /// <summary>Host: a shop and shelf item that needs a licence, and whether the save has it.</summary>
            [Step]
            public static Gated FindGated()
            {
                var shops = GlobalShopController.Instance.globalShopList;
                for (int s = 0; s < shops.Count; s++)
                    for (int m = 0; m < shops[s].scanItemResourceModules.Length; m++)
                    {
                        var gate = Gate(shops[s].scanItemResourceModules[m]);
                        if (gate == null)
                            continue;
                        Vector3 at = Dev.Abs(shops[s].cashRegister.transform.position);
                        return new Gated
                        {
                            Shop = s, Module = m, Prefab = shops[s].scanItemResourceModules[m].sellingItemSpec.ItemPrefabName,
                            Licence = gate.license.id, Held = LicenseManager.Instance.IsGeneralLicenseAcquired(gate.license), X = at.x, Z = at.z
                        };
                    }
                return new Gated();
            }

            /// <summary>Host: acquires or removes the licence the gated item needs.</summary>
            [Step]
            public static void Licence(bool acquire)
            {
                var gate = GlobalShopController.Instance.globalShopList.SelectMany(s => s.scanItemResourceModules).Select(Gate).First(g => g != null);
                if (acquire) LicenseManager.Instance.AcquireGeneralLicense(gate.license);
                else if (LicenseManager.Instance.IsGeneralLicenseAcquired(gate.license)) LicenseManager.Instance.RemoveGeneralLicense(gate.license);
            }

            /// <summary>Holds the scanner, scans the item once and reports what the basket holds after 3 s.</summary>
            [Step]
            public static IEnumerator TryScan(Step<Scan> step, int shopIndex, int module)
            {
                global::Shop shop = GlobalShopController.Instance.globalShopList[shopIndex];
                ScanItemCashRegisterModule mod = shop.scanItemResourceModules[module];
                yield return Shop.InGame.TakeScanner(shop);
                yield return Shop.InGame.StandAt(mod.transform);
                // The patched scan sends the request whatever vanilla's own stock check would say.
                mod.AddItemsToBuy();
                yield return new WaitForSeconds(3f);
                ShopItemData data = GlobalShopController.Instance.GetShopItemData(mod.sellingItemSpec);
                step.Result = new Scan { Allowed = data.allowedToHaveAmount, InStock = data.ItemsInStock, Units = (int)mod.Data.unitsToBuy };
                yield return Shop.InGame.ReturnScanner(shop);
            }

            [Step]
            public static void Cancel(int shopIndex) => GlobalShopController.Instance.globalShopList[shopIndex].cashRegister.Cancel();
        }
    }
}
