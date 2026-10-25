using System.Linq;
using System.Threading.Tasks;
using DV.Customization.Gadgets.Implementations;
using Dvmp.Lib;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// An item on a hanger rides its car for interest (OI-SYNC-23; docs/design/items/projection.md,
    /// "Interest"). A hanger of the client's is installed in a loco's cab beside the client and an item
    /// hung on it (attached-offline's <c>Hang</c>): the client has it. The host moves the loco to a
    /// track at least 700 m from every player (lost-and-found-moved's <c>MoveFar</c>), the client
    /// staying where it is: the item must leave the client's interest on the server. Then the client
    /// goes to the loco: it must have the item again, snapped to the hanger. A gadget's snap sends no
    /// car (only the gadget), so before the fix interest fell back to where the item was hung: it
    /// stayed with the client left behind, and never reached the client by the loco. Both host kinds.
    /// The hanger's dangler (which the server runs) is held still on the host while the loco is moved
    /// and settles: the teleport is a jolt that swings it upside down, and vanilla then drops the item.
    /// </summary>
    [Scenario("hanger-interest", "An item on a hanger in a loco's cab follows the loco for interest: it leaves a client the loco is moved away from, and reaches it by the loco")]
    [Budget(400)]
    public sealed class HangerInterest : Scenario<HangerInterest.Options>
    {
        public sealed class Options
        {
            /// <summary>The loco the hanger is installed in.</summary>
            public string Loco = "LocoDE2";
            /// <summary>At least how far from every player the loco is moved, m (beyond the 5x5 cells that keep an item).</summary>
            public float Far = 700;
            /// <summary>Seconds a change of interest may take.</summary>
            public int Within = 20;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 5000, Nearby = 5000, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, o.Loco, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));

            AttachedOffline.Hung hung = await pair.Host.Run<AttachedOffline.Hung>(s => AttachedOffline.InGame.Hang(s, loco), 120);
            string itemId = await pair.Host.Run(() => Owned.IdOf(hung.Item));
            string hangerId = await pair.Host.Run(() => Owned.IdOf(hung.HangerNetId));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(new[] { itemId, hangerId })));
            Report.Note($"on the hanger {hung.Hanger} ({hung.HangerNetId}) in {loco}: a {hung.Prefab} ({hung.Item}), {hung.State}");
            ItemRecord record = await Until.Value(() => pair.Host.Run(() => Items.Record(hung.Item)),
                r => r.Placement == "SnappedAttachment", 20, "the host has the item snapped to the hanger");
            Report.Note($"its record: {record}");
            Require.Equal("", await pair.Client.Run<string>(s => SnappedRestart.InGame.OnHanger(s, hung.Item, hung.HangerNetId, 20f), 40),
                "the client by the loco has the item on the hanger");
            Require.That((await pair.Host.Run(() => InterestCellsMoves.InGame.ServerView(hung.Item))).Nearby,
                "the item in the client's interest on the server, the loco beside it");

            // The loco goes far away, the hanger and its item with it; the client stays.
            ushort hanger = hung.HangerNetId;
            Require.That(await pair.Host.Run(() => InGame.HoldDangler(hanger, true)), "the host holds the hanger's dangler still");
            pair.Cleanup(() => pair.Host.Run(() => InGame.HoldDangler(hanger, false)));
            LostAndFoundMoved.Moved moved = await pair.Host.Run<LostAndFoundMoved.Moved>(s => LostAndFoundMoved.InGame.MoveFar(s, loco, o.Far), 60);
            Report.Note($"moved {loco} {moved.Distance:0} m, from {moved.From} to {moved.To} (track {moved.Track})");
            await Task.Delay(5000);   // the loco settles on its new track
            await pair.Host.Run(() => InGame.HoldDangler(hanger, false));
            await Until.Value(() => pair.Host.Run(() => InterestCellsMoves.InGame.ServerView(hung.Item)), s => !s.Nearby && !s.Known, o.Within,
                "the item out of the client's interest once its loco is far (else interest read where it was hung)");
            Expect.That(!await pair.Client.Run(() => InterestCellsMoves.InGame.Has(hung.Item)), "the client left behind no longer has the item");

            // The client goes to the loco: the item must reach it there.
            float x = moved.To.X + 4f, z = moved.To.Z;
            await pair.Client.Run(() => Players.TeleportToGround(x, z), 90);
            await Until.Value(() => pair.Host.Run(() => InterestCellsMoves.InGame.ServerView(hung.Item)), s => s.Nearby && s.Known, o.Within,
                "the item in the client's interest by the loco (else interest read where it was hung)");
            Expect.Equal("", await pair.Client.Run<string>(s => SnappedRestart.InGame.OnHanger(s, hung.Item, hung.HangerNetId, 20f), 40),
                "the client by the loco has the item on the hanger again");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: holds the hanger's dangler still (disabled), or lets it swing again from rest, the
            /// car's velocity now its starting point. Result: the hanger has a dangler.
            /// </summary>
            [Step]
            public static bool HoldDangler(ushort hanger, bool hold)
            {
                var point = GadgetSnapPoints.PointOf(hanger);
                Dangler dangler = point == null ? null : Object.FindObjectsOfType<Dangler>().FirstOrDefault(d => d.snapPointGadget == point);
                if (dangler == null) return false;
                if (hold) { dangler.enabled = false; return true; }
                dangler.angularVelocity = Vector3.zero;
                if (dangler.attachedCustomizer.IsOnTrainCar)
                    Traverse.Create(dangler).Field("lastPointVelocity").SetValue(
                        dangler.attachedCustomizer.TrainCar.rb.GetPointVelocity(dangler.transform.position) * dangler.trainMult);
                dangler.enabled = point.SnappedItem != null;
                return true;
            }
        }
    }
}
