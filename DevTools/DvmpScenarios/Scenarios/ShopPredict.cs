using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Shops;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A player at 250 ms sees their own shop requests at once (docs/design/shops.md, PR 4). With
    /// every packet to the client delayed, the client holds the scanner and scans: the line is on its
    /// register within a few frames, and is still there once the server's answer has come back. Then
    /// it scans without the scanner in hand, which the server refuses: the line shows at once and
    /// goes again when the refusal arrives, a round trip later.
    /// </summary>
    [Scenario("shop-predict", "At 250 ms the client's scan shows on its register at once and stays; a scan the server refuses shows, then rolls back")]
    [Budget(240)]
    public sealed class ShopPredict : Scenario<ShopPredict.Options>
    {
        public sealed class Options
        {
            /// <summary>Delay on every packet the client receives, in ms.</summary>
            public int Lag = 250;
            /// <summary>The shelf item's name prefix.</summary>
            public string Item = "Lighter";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Places.Shop(pair, 2f);
            await pair.Host.Run(() => Shop.InGame.ResetRegisters());
            await Task.Delay(1000);
            using var lag = await pair.Client.Lag(o.Lag);

            Timeline ok = await pair.Client.Run<Timeline>(s => InGame.Scan(s, o.Item, true));
            Report.Note($"scan with the scanner: on the register after {ok.ShownFrames} frame(s); {ok.UnitsAfter} unit(s) after {ok.WaitedSeconds:0.0} s");
            Expect.That(ok.ShownFrames <= 3, "the client's scan is on its register within 3 frames", ok.ShownFrames.ToString());
            Expect.Equal(1, ok.UnitsAfter, "units on the client's register once the server has answered");

            Timeline refused = await pair.Client.Run<Timeline>(s => InGame.Scan(s, o.Item, false));
            Report.Note($"scan without the scanner: on the register after {refused.ShownFrames} frame(s), gone after {refused.GoneSeconds:0.00} s; {refused.UnitsAfter} unit(s) after {refused.WaitedSeconds:0.0} s");
            Expect.That(refused.ShownFrames <= 3, "the refused scan showed on the client's register at once", refused.ShownFrames.ToString());
            Expect.That(refused.GoneSeconds > 0 && refused.GoneSeconds < 2f, "the refused scan rolled back within 2 s", refused.GoneSeconds.ToString("0.00"));
            Expect.Equal(1, refused.UnitsAfter, "units on the client's register after the rollback (the first scan's)");

            await pair.Host.Run(() => Shop.InGame.ResetRegisters());
        }

        public sealed class Timeline
        {
            public int ShownFrames, UnitsAfter;
            public float GoneSeconds, WaitedSeconds;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Scans the item (with the scanner in hand, or after putting it down) and times it: the
            /// frames until the line is on this game's register, the seconds until a refused one is
            /// gone again, and the units after the server has surely answered.
            /// </summary>
            [Step]
            public static IEnumerator Scan(Step<Timeline> step, string item, bool holdScanner)
            {
                global::Shop shop = GlobalShopController.Instance.globalShopList
                    .OrderBy(s => Vector3.Distance(s.cashRegister.transform.position, PlayerManager.PlayerTransform.position)).First();
                ScanItemCashRegisterModule mod = shop.scanItemResourceModules.First(m => m.name.StartsWith(item));
                if (holdScanner)
                    yield return Shop.InGame.TakeScanner(shop);
                else
                    yield return Shop.InGame.ReturnScanner(shop);
                yield return Shop.InGame.StandAt(mod.transform);

                float before = mod.Data.unitsToBuy;
                mod.AddItemsToBuy();
                int frames = 0;
                while (mod.Data.unitsToBuy <= before && frames < 120)
                {
                    frames++;
                    yield return null;
                }
                float t0 = Time.realtimeSinceStartup, gone = 0f;
                while (Time.realtimeSinceStartup - t0 < 3f)
                {
                    if (gone == 0f && mod.Data.unitsToBuy <= before)
                        gone = Time.realtimeSinceStartup - t0;
                    yield return null;
                }
                step.Result = new Timeline
                {
                    ShownFrames = frames, GoneSeconds = gone, WaitedSeconds = Time.realtimeSinceStartup - t0,
                    UnitsAfter = (int)mod.Data.unitsToBuy
                };
                if (holdScanner)
                    yield return Shop.InGame.ReturnScanner(shop);
            }
        }
    }
}
