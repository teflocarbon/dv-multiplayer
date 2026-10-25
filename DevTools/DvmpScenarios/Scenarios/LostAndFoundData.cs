using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A Lost and Found entry kept as data (docs/design/items/lost-and-found.md, "Kept as data"). A mug
    /// of the client's is collected and goes to data. Then:
    /// 1. A second mug asks the ledger for the first's id: it is given a new one, since the entry holds
    ///    it (J6).
    /// 2. The entry goes (as an item remade fresh drops it): the client's inert copy of the mug goes
    ///    with it, so a reused NetId can't name it (J7).
    /// 3. A mug whose prefab no game knows (its name changed on the host) is collected: it stays an
    ///    object in Lost and Found, since it couldn't be made again from a row (J3).
    /// 4. A lantern of the client's on a flatbed's coupler is collected, and the collection made to fail
    ///    halfway (after the record moved): rolled back, it comes off the coupler into the world where
    ///    it was, on both games, never `Attached` to no car (J4).
    /// </summary>
    [Scenario("lost-and-found-data", "A Lost and Found entry kept as data holds its id, its end reaches every game, one that couldn't be rebuilt stays an object, and a failed collection rolls back (J3, J4, J6, J7)")]
    [Budget(360)]
    public sealed class LostAndFoundData : Scenario<LostAndFoundData.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            var ids = new List<string>();
            var spawned = new List<ushort>();   // by NetId: the second mug's id may change
            pair.Cleanup(async () =>
            {
                var now = new List<ushort>(spawned);
                foreach (string id in ids)
                {
                    ushort netId = await pair.Host.Run(() => Owned.NetIdOf(id));
                    if (netId != 0) now.Add(netId);
                }
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, now.Distinct().ToArray()), 60);
            });

            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(mug, "client"));
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));
            ids.Add(mugId);
            await Task.Delay(2000);   // the client has it
            Require.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, mug, "SaveRecovery")), "the client's mug went to its Lost and Found");
            Require.That(await pair.Host.Run<bool>(s => InGame.BecomesData(s, mug), 60), "the mug's entry is kept as data");

            // 1. Its id is the entry's: a copy asking for it gets a new one.
            ushort copy = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(copy, "client"));
            spawned.Add(copy);
            string claimed = await pair.Host.Run(() => InGame.Claim(copy, mugId));
            Report.Note($"the second mug asked for {mugId}, given {claimed}");
            Expect.That(claimed != mugId, "a second item asking for a data entry's id is given a new one", claimed);

            // 2. The entry goes: so does the client's copy.
            Require.That((await pair.Client.Run(() => Items.Pose(mug))).Present, "the client keeps an inert copy of the collected mug", "it has none");
            await pair.Host.Run(() => InGame.Forget(mug));
            await Task.Delay(3000);
            Expect.That(!(await pair.Client.Run(() => Items.Pose(mug))).Present, "the client's copy goes with the data entry", "still there");

            // 3. One that couldn't be made again stays an object.
            ushort odd = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(odd, "client"));
            spawned.Add(odd);
            string was = await pair.Host.Run(() => InGame.Rename(odd, "DvmpUnlistedMug"));
            try
            {
                Require.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, odd, "SaveRecovery")), "the renamed mug went to the client's Lost and Found");
                bool data = await pair.Host.Run<bool>(s => InGame.BecomesData(s, odd), 60);
                OwnerInfo after = await pair.Host.Run(() => Ownership.Read(odd));
                ItemRecord record = await pair.Host.Run(() => Items.Record(odd));
                Report.Note($"the renamed mug: data {data}; {after}; object {record.Exists}");
                Expect.That(!data, "an entry whose prefab is unknown isn't kept as data");
                Expect.That(after.Lost, "the renamed mug is still in Lost and Found", after.ToString());
                Expect.That(record.Exists, "the renamed mug is still an object on the host");
            }
            finally
            {
                await pair.Host.Run(() => InGame.Rename(odd, was));
            }

            // 4. A collection that fails halfway, of a lantern on a coupler: rolled back off the coupler, into the world.
            string flatbed = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "FlatbedEmpty", "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { flatbed })));
            ushort lantern = await pair.Host.Run<ushort>(s => SaveHomes.InGame.Grant(s, "EOTLantern"));
            spawned.Add(lantern);
            Report.Note("the client's lantern: " + await pair.Client.Run<string>(s => SaveHomes.InGame.AttachToCoupler(s, lantern, flatbed), 90));
            await Until.Value(() => pair.Host.Run(() => Items.Record(lantern)), r => r.Placement == "Attached", 20, "the host has the client's lantern attached");
            Expect.That(!await pair.Host.Run(() => InGame.CollectFailing(lantern)), "a collection that fails halfway says so");
            await Task.Delay(2000);
            ItemRecord rolled = await pair.Host.Run(() => Items.Record(lantern));
            OwnerInfo where = await pair.Host.Run(() => Ownership.Read(lantern));
            Report.Note($"the lantern after the failed collection: {rolled}; {where}");
            Expect.That(rolled.Exists, "the lantern still exists", "destroyed");
            Expect.Equal("World", rolled.Placement, "the lantern's placement, rolled back off its coupler");
            Expect.That(!where.Lost, "the lantern has no Lost and Found entry", where.ToString());
            ItemPose host = await pair.Host.Run(() => Items.Pose(lantern));
            ItemPose client = await pair.Client.Run(() => Items.Pose(lantern));
            Expect.That(client.Present, "the client still has the lantern", "missing");
            Expect.That(!client.State.Contains("Attached"), "the lantern is off the coupler on the client", client.State);
            if (host.Present && client.Present)
                Expect.That(Peers.Distance(host.Pos, client.Pos) < 2f, "the host and client have the lantern in the same place",
                    $"{Peers.Distance(host.Pos, client.Pos):0.##} m apart");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: waits up to 20 s for the item's Lost and Found entry to be kept as data. Result: whether it was.</summary>
            [Step]
            public static IEnumerator BecomesData(Step<bool> step, ushort netId)
            {
                float t0 = Time.realtimeSinceStartup;
                while (!LostAndFoundServer.IsData(netId) && Time.realtimeSinceStartup - t0 < 20f)
                    yield return null;
                step.Result = LostAndFoundServer.IsData(netId);
            }

            /// <summary>Host: the id the ledger gives the item when it asks for <paramref name="id"/>.</summary>
            [Step]
            public static string Claim(ushort netId, string id)
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null)
                    throw new Exception("no item " + netId);
                return ServerLedger.ClaimPersistentId(item, Guid.Parse(id)).ToString("D");
            }

            /// <summary>
            /// Host: drops the item's Lost and Found entry, as an item remade fresh does, and ends its
            /// ledger entry: no remade object carries its id here, and the one-home check would say so.
            /// </summary>
            [Step]
            public static void Forget(ushort netId)
            {
                Guid id = Multiplayer.Server.Items.LiveState.LiveItems.TryGet(netId, out var record) ? record.PersistentItemId : Guid.Empty;
                LostAndFoundServer.Forget(netId, "lost-and-found-data scenario");
                if (id != Guid.Empty)
                    ServerLedger.Ledger.End(id, Multiplayer.Core.Items.ChangeCauses.ItemRemovalReason.DestroyedByGame,
                        detail: "lost-and-found-data scenario: its Lost and Found entry dropped");
            }

            private const string HarmonyId = "dvmp.scenarios.lost-and-found-data";

            /// <summary>
            /// Host: collects the item into its owner's Lost and Found with its projection into inert
            /// storage made to throw, after the record has moved. Result: what the collection returned.
            /// </summary>
            [Step]
            public static bool CollectFailing(ushort netId)
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null)
                    throw new Exception("no item " + netId);
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(AccessTools.Method(typeof(LostAndFoundServer), "ProjectToInertStorage"),
                    prefix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(Fail), BindingFlags.NonPublic | BindingFlags.Static)));
                try { return LostAndFoundServer.Collect(item, LostItemReason.SaveRecovery, "lost-and-found-data scenario, failing halfway"); }
                finally { harmony.UnpatchAll(HarmonyId); }
            }

            private static bool Fail() => throw new InvalidOperationException("lost-and-found-data: a collection made to fail halfway");

            /// <summary>Host: gives the item's spec another prefab name. Result: the one it had, or "" with no object.</summary>
            [Step]
            public static string Rename(ushort netId, string prefabName)
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null || item.Item?.InventorySpecs == null)
                    return string.Empty;   // gone to data after all
                string was = item.Item.InventorySpecs.itemPrefabName;
                item.Item.InventorySpecs.itemPrefabName = prefabName;
                return was;
            }
        }
    }
}
