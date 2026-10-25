using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.Ledger;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A Lost and Found retrieval picks its slot from the server's rows, not the client's report (R3;
    /// docs/design/items/inventory.md, "Slots" and "A full inventory"). The client has two mugs of its
    /// own in its Lost and Found. The host marks the lowest slot the client's game shows free as taken
    /// in its own inventory (the ledger's <c>SlotTakenOutside</c>, wrapped for the run: the client's
    /// game never hears of it), as a row the client's game doesn't show yet would. The client
    /// collects the first mug: the server's plan (<c>LostItemRetrievalPlanner.Plan</c>, watched by a
    /// Harmony postfix) must pick another slot, and the mug lands in a slot the server has free. Then
    /// every slot is marked taken, so the inventory is full by the server's rows while the client's
    /// game still shows free slots: the second mug's retrieval is refused <c>inventory-full</c>, and it
    /// stays in Lost and Found. Without the fix the plan took the client's list of occupied slots:
    /// it picks the marked slot (the first check fails; the move itself then found another free slot),
    /// and with every slot marked it plans a slot the client shows free, so the retrieval isn't
    /// refused <c>inventory-full</c>. Either kind of host: the client is the actor.
    /// </summary>
    [Scenario("lost-and-found-slots", "A Lost and Found retrieval plans with the server's slots: never one the server has taken, and refused when they're all taken (R3)")]
    [Budget(300)]
    public sealed class LostAndFoundSlots : Scenario<LostAndFoundSlots.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            var ids = new List<string>();
            pair.Cleanup(async () =>
            {
                var now = new List<ushort>();
                foreach (string id in ids)
                {
                    ushort netId = await pair.Host.Run(() => Owned.NetIdOf(id));
                    if (netId != 0) now.Add(netId);
                }
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, now.ToArray()), 120);
            });
            var handles = new List<uint>();
            for (int i = 0; i < 2; i++)
            {
                ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
                await pair.Host.Run(() => Ownership.Give(mug, "client"));
                ids.Add(await pair.Host.Run(() => Owned.IdOf(mug)));
                Require.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, mug, "SaveRecovery")), $"mug {i + 1} went to the client's Lost and Found");
                handles.Add((await pair.Host.Run(() => Items.Record(mug))).Handle);
            }

            // The lowest slot the client's game shows free, which the server has free too, marked taken.
            int free = await pair.Client.Run(() => InGame.FirstFree());
            int[] taken = await pair.Host.Run(() => InGame.Taken(ids[0], client));
            Report.Note($"the client's game: slot {free} is its lowest free one; the server's taken slots: {string.Join(",", taken)}");
            if (free < 0) throw new HarnessException("the client's inventory is full: the run needs a free slot");
            // A slot the client shows empty that the server already has taken (another essential's claim)
            // would make the plan's two sources differ before the run marks anything.
            if (taken.Contains(free)) throw new HarnessException($"the server already has the client's empty slot {free} taken (a claim): the run needs them to agree first");
            await pair.Host.Run(() => InGame.WatchPlans(true));
            pair.Cleanup(() => pair.Host.Run(() => InGame.WatchPlans(false)));
            await pair.Host.Run(() => InGame.MarkTaken(client, new[] { free }, false));
            pair.Cleanup(() => pair.Host.Run(() => InGame.MarkTaken("", new int[0], false)));
            Require.That((await pair.Host.Run(() => InGame.Taken(ids[0], client))).Contains(free), $"slot {free} taken by the server's rows");
            Require.Equal(free, await pair.Client.Run(() => InGame.FirstFree()), $"the client's game still shows slot {free} free");

            string first = await pair.Client.Run<string>(s => FullInventoryRoutes.InGame.TryRetrieve(s, handles[0]), 60);
            string plan = await pair.Host.Run(() => InGame.LastPlan());
            Report.Note($"the first retrieval: {first}; the server's plan: {plan}");
            Expect.That(first.StartsWith("accepted"), "the first mug's retrieval is accepted", first);
            Expect.That(plan.StartsWith("accepted") && plan != "accepted " + free, $"the server's plan picks a slot it has free, not slot {free}", plan);
            Expect.That(first != "accepted slot " + free, $"the mug isn't in slot {free} on the client", first);
            ushort firstNow = await pair.Host.Run(() => Owned.NetIdOf(ids[0]));
            ServerSlot server = await pair.Host.Run(() => Ledger.Where(firstNow));
            Report.Note("the first mug on the server: " + server);
            Expect.That(server.Slot >= 0 && server.Slot != free, $"the server has the first mug in a slot, not slot {free}", server.ToString());
            Expect.Equal(server.Slot, server.LedgerSlot, "the first mug: the ledger's slot against the server's");

            // Every slot taken by the server's rows; the client's game still shows some free.
            await pair.Host.Run(() => InGame.MarkTaken(client, new int[0], true));
            Require.That(await pair.Client.Run(() => InGame.FirstFree()) >= 0, "the client's game still shows a free slot");
            string second = await pair.Client.Run<string>(s => FullInventoryRoutes.InGame.TryRetrieve(s, handles[1]), 60);
            plan = await pair.Host.Run(() => InGame.LastPlan());
            Report.Note($"the second retrieval: {second}; the server's plan: {plan}");
            Expect.That(second.StartsWith("refused") && second.Contains("inventory-full"), "the second mug's retrieval, into an inventory full by the server's rows, is refused inventory-full", second);
            Expect.Equal("refused inventory-full", plan, "the server's plan for the second mug");
            await pair.Host.Run(() => InGame.MarkTaken("", new int[0], false));
            await Task.Delay(1000);
            OwnerInfo after = await pair.Host.Run(() => Ownership.ById(ids[1]));
            Expect.That(after.Lost && after.Placement == "LostAndFound", "the refused mug is still in Lost and Found", after.ToString());
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string HarmonyId = "dvmp.scenarios.lost-and-found-slots";
            private static Func<Guid, int, Guid, bool> original;
            private static bool wrapped;
            private static Guid markedHolder;
            private static HashSet<int> marked = new();
            private static bool markAll;
            private static string lastPlan = "none";

            /// <summary>
            /// Client: the lowest slot its game shows empty (no item, no silhouette), or -1: the slot a
            /// plan from the client's report picked, as the report listed every other one.
            /// </summary>
            [Step]
            public static int FirstFree()
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                for (int s = 0; s < inv.Capacity; s++)
                    if (inv.PeekItemAtSlot(s, true) == null) return s;
                return -1;
            }

            /// <summary>Host: the slots of the holder's inventory the server has taken for the item (ServerLedger.TakenSlots).</summary>
            [Step]
            public static int[] Taken(string id, string holder) => ServerLedger.TakenSlots(Guid.Parse(id), Guid.Parse(holder));

            /// <summary>
            /// Host: the ledger counts these slots of the holder's inventory taken (null: every slot), as
            /// rows it keeps outside its entries are; "" puts the ledger's own check back.
            /// </summary>
            [Step]
            public static void MarkTaken(string holder, int[] slots, bool all)
            {
                var ledger = ServerLedger.Ledger;
                if (string.IsNullOrEmpty(holder))
                {
                    if (wrapped) ledger.SlotTakenOutside = original;
                    wrapped = false;
                    marked.Clear();
                    markAll = false;
                    return;
                }
                if (!wrapped)
                {
                    original = ledger.SlotTakenOutside;
                    ledger.SlotTakenOutside = (who, slot, except) =>
                        who == markedHolder && (markAll || marked.Contains(slot)) || original?.Invoke(who, slot, except) == true;
                    wrapped = true;
                }
                markedHolder = Guid.Parse(holder);
                markAll = all;
                marked = new HashSet<int>(slots ?? new int[0]);
            }

            /// <summary>Host: records each Lost and Found retrieval plan (on), or stops (off).</summary>
            [Step]
            public static void WatchPlans(bool on)
            {
                var harmony = new Harmony(HarmonyId);
                harmony.UnpatchAll(HarmonyId);
                lastPlan = "none";
                if (on)
                    harmony.Patch(AccessTools.Method(typeof(LostItemRetrievalPlanner), nameof(LostItemRetrievalPlanner.Plan)),
                        postfix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(PlanPostfix))));
            }

            public static void PlanPostfix(LostItemRetrievalPlan __result) =>
                lastPlan = __result.Accepted ? "accepted " + __result.TargetSlot : "refused " + __result.Reason;

            /// <summary>Host: the last plan, "accepted SLOT" or "refused REASON" ("none" since the watch began).</summary>
            [Step]
            public static string LastPlan() => lastPlan;
        }
    }
}
