using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using DV.InventorySystem;
using DV.UI.Inventory;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Every vanilla item out of the hotbar and back (docs/design/items/inventory.md, "Taking out";
    /// D14, OI-COLD-10): the player picks each up, stashes it into a hotbar slot through the hotbar
    /// (GrabberStashingHandler, as the key does), takes it out the same way and stashes it again.
    /// The slot holds a stand-in and no object while it's stored; taken out, the object
    /// is made in that frame with the saved state it went in with, and the frame it takes stays
    /// within MAX_MS. The server and the ledger have it in the slot after each stash. WHO=host does
    /// it with the host's own player, whose slots are stand-ins too (R4C C4). PREFABS= limits the list.
    /// QUICK=1 stashes it again the moment it's in the hand, before the take-out's echo comes back
    /// (R4C C9 A4): for 3 s after, every frame, it has no object and its slot holds its stand-in. LAG= lags
    /// the client's link (150 ms by default with QUICK=1), so the echo comes after the second stash.
    /// </summary>
    [Scenario("hotbar-cycle", "Every vanilla item stashed into the hotbar, taken out and stashed again: no object while stored, its state in the first frame, the frame time bounded")]
    [Budget(3600)]
    public sealed class HotbarCycle : Scenario<HotbarCycle.Options>
    {
        public sealed class Options
        {
            /// <summary>The prefabs, comma-separated; empty for every vanilla item a client stands in.</summary>
            public string Prefabs = "";
            /// <summary>Whose inventory: "client", or "host" (the host's own player; needs a player host).</summary>
            public string Who = "client";
            /// <summary>The longest a take-out's frame may take, in milliseconds.</summary>
            public float MaxMs = 100f;
            /// <summary>Stashes it again as soon as it's in the hand, and watches its slot for 3 s.</summary>
            public bool Quick;
            /// <summary>Lag on the client's link, in ms, so the take-out's echo comes late: QUICK=1 (on a client) defaults to 150.</summary>
            public int Lag = -1;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            if (host && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = host ? pair.Host : pair.Client;
            int lag = o.Lag >= 0 ? o.Lag : o.Quick && !host ? 150 : 0;
            using IDisposable lagged = await pair.Client.Lag(lag);
            if (lag > 0) Report.Note($"the client's link lags {lag} ms");
            string[] prefabs = o.Prefabs.Length > 0
                ? o.Prefabs.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray()
                : await who.Run(() => InGame.Prefabs());
            Report.Note($"{prefabs.Length} prefabs: {string.Join(", ", prefabs)}");
            var slow = new List<string>();
            foreach (string prefab in prefabs)
            {
                ushort id = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, o.Who));
                Cycled c = await who.Run<Cycled>(s => InGame.Cycle(s, id, o.Quick), 90);
                Report.Note($"{prefab} {id}: {c}");
                if (c.Refused.Length > 0)
                {
                    // Vanilla keeps some things out of the inventory: noted, not failed.
                    Report.Note($"{prefab}: not stashable ({c.Refused})");
                }
                else
                {
                    Expect.Empty(c.Failures, $"{prefab}: what went wrong in the {who.Name}'s game");
                    Expect.That(c.OutMs <= o.MaxMs, $"{prefab}: the take-out's frame within {o.MaxMs} ms", $"{c.OutMs:F1} ms");
                    if (c.OutMs > o.MaxMs / 2) slow.Add($"{prefab} {c.OutMs:F0} ms");
                    if (c.Slot >= 0)
                    {
                        ServerSlot server = await pair.Host.Run(() => Ledger.Where(id));
                        Expect.Equal(c.Slot, server.Slot, $"{prefab}: the server's slot after the second stash");
                        Expect.Equal(c.Slot, server.LedgerSlot, $"{prefab}: the ledger's slot after the second stash");
                    }
                }
                // Each one goes before the next, so the inventory never fills.
                Expect.Empty(await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { id }), 30), $"{prefab}: still on the host after its destroy");
                await Task.Delay(1000);
                Expect.Empty(await who.Run(() => Items.Existing(new[] { id })), $"{prefab}: still in the {who.Name}'s game after its destroy");
            }
            if (slow.Count > 0) Report.Note("slowest take-outs: " + string.Join(", ", slow));
        }

        public sealed class Cycled
        {
            public int Slot = -1;
            public float OutMs, NextFrameMs;
            public bool StandIn;
            public string Refused = "";
            public string[] Failures = new string[0];

            public override string ToString() => Refused.Length > 0 ? "refused: " + Refused
                : $"slot {Slot}{(StandIn ? " (stand-in)" : "")}, take-out {OutMs:F1} ms, next frame {NextFrameMs:F1} ms" +
                  (Failures.Length > 0 ? "; " + string.Join("; ", Failures) : "");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Every vanilla item a client keeps as a stand-in when stored, but cash and the essentials.</summary>
            [Step]
            public static string[] Prefabs() =>
                DV.Globals.G.Items.items.Where(s => s != null && !s.IsEssential && s.GetComponent<Money>() == null &&
                                                    InventoryStandIn.CanStandIn(s.ItemPrefabName))
                    .Select(s => s.ItemPrefabName).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();

            /// <summary>
            /// This player picks the item up with a free hotbar slot selected, stashes it through the
            /// hotbar, takes it out and stashes it again (<paramref name="quick"/>: as soon as it's in
            /// the hand, then watches its slot every frame for 3 s). Result: the slot, the take-out's
            /// frame time, and what went wrong.
            /// </summary>
            [Step]
            public static IEnumerator Cycle(Step<Cycled> step, ushort id, bool quick)
            {
                var result = new Cycled();
                var failures = new List<string>();
                step.Result = result;
                var inv = Inventory.Instance;
                var handler = UnityEngine.Object.FindObjectOfType<GrabberStashingHandler>() ?? throw new Exception("no GrabberStashingHandler");
                var hotbar = UnityEngine.Object.FindObjectOfType<HotbarController>() ?? throw new Exception("no HotbarController");
                bool rows = InventoryRowHolder.Current != null;

                int slot = Enumerable.Range(InventoryUtils.HOTBAR_START_INDEX, InventoryUtils.HOTBAR_SIZE)
                    .Where(s => inv.PeekItemAtSlot(s, true) == null && !inv.GetSlotReservedState(s)).DefaultIfEmpty(-1).First();
                if (slot < 0) throw new Exception("no free hotbar slot");
                hotbar.UpdateSelectedSlot(slot, false);
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                if (Hands.Held == null || Hands.Held.NetId != id) { Fail("couldn't pick it up"); yield break; }
                if (!inv.CanAddItem(item.gameObject))
                {
                    result.Refused = "the inventory won't take it";
                    yield return Hands.Drop();
                    yield break;
                }
                JToken before = Parse(ItemStateText.Capture(item.gameObject));

                // In through the hotbar, into the selected slot (or wherever vanilla put it, moved there).
                hotbar.UpdateSelectedSlot(slot, false);
                handler.StashToggle(slot);
                yield return Within(() => PlayerInventory.Holds(id), 5f);
                if (!PlayerInventory.Holds(id)) { Fail("not in the inventory 5 s after the first stash"); yield break; }
                if (PlayerInventory.Slot(id) != slot && !PlayerInventory.MoveTo(id, slot)) { Fail($"couldn't move it to hotbar slot {slot}"); yield break; }
                yield return new WaitForSecondsRealtime(1.5f);
                if (!rows) failures.Add("this game's inventory isn't rows");
                if (!PlayerInventory.IsStandIn(id)) failures.Add("stashed, it kept its object, not a stand-in");
                if (NetworkedItem.TryGet(id, out NetworkedItem left) && left != null) failures.Add("stashed, its object is still there");

                // Out: the object is made in this frame, with the state it went in with.
                hotbar.UpdateSelectedSlot(slot, false);
                float t0 = Time.realtimeSinceStartup;
                handler.StashToggle(slot);
                result.OutMs = (Time.realtimeSinceStartup - t0) * 1000f;
                if (!NetworkedItem.TryGet(id, out NetworkedItem made) || made == null)
                    failures.Add("taken out, no object in the same frame");
                else
                {
                    JToken first = Parse(ItemStateText.Capture(made.gameObject));
                    if (!JToken.DeepEquals(before, first)) failures.Add($"its state in the first frame: {first}, it went in with {before}");
                }
                if (PlayerInventory.IsStandIn(id)) failures.Add("taken out, its stand-in is still there");
                float f0 = Time.realtimeSinceStartup;
                yield return null;
                result.NextFrameMs = (Time.realtimeSinceStartup - f0) * 1000f;
                yield return Within(() => Hands.Held != null && Hands.Held.NetId == id, 5f);
                if (Hands.Held == null || Hands.Held.NetId != id) { Fail("not in the hand 5 s after the take-out"); yield break; }
                if (!quick)
                {
                    yield return new WaitForSecondsRealtime(1.5f);
                    if (Hands.Held == null || Hands.Held.NetId != id) failures.Add("taken out, it left the hand (rolled back?)");
                }

                // In again (QUICK: now, before the take-out's echo).
                hotbar.UpdateSelectedSlot(slot, false);
                handler.StashToggle(slot);
                if (quick)
                {
                    // Once the put-away has retired the object, the take's late echo mustn't take the
                    // stand-in out again (it dropped at the player's feet for a round trip).
                    yield return Within(() => PlayerInventory.IsStandIn(id) && !(NetworkedItem.TryGet(id, out NetworkedItem going) && going != null), 1f);
                    var seen = new HashSet<string>();
                    float watched = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - watched < 3f)
                    {
                        string at = $"{Time.realtimeSinceStartup - watched:0.00} s after the second stash";
                        if (NetworkedItem.TryGet(id, out NetworkedItem back) && back != null && seen.Add("object"))
                            failures.Add($"its object is there {at} ({back.GetItemState()})");
                        if (!PlayerInventory.IsStandIn(id) && seen.Add("stand-in"))
                            failures.Add($"no stand-in {at}");
                        if (PlayerInventory.Slot(id) != slot && seen.Add("slot"))
                            failures.Add($"in slot {PlayerInventory.Slot(id)}, not {slot}, {at}");
                        yield return null;
                    }
                }
                yield return Within(() => PlayerInventory.Holds(id), 5f);
                if (!PlayerInventory.Holds(id)) { Fail("not in the inventory 5 s after the second stash"); yield break; }
                if (!quick) yield return new WaitForSecondsRealtime(1.5f);
                result.Slot = PlayerInventory.Slot(id);
                result.StandIn = PlayerInventory.IsStandIn(id);
                if (result.Slot != slot) failures.Add($"stashed again into slot {result.Slot}, not {slot}");
                if (!result.StandIn) failures.Add("stashed again, it kept its object");
                Hands.ReleaseControl();
                result.Failures = failures.ToArray();

                // One prefab's failure is its own: recorded, and the run goes on to the next.
                void Fail(string why)
                {
                    failures.Add(why);
                    result.Failures = failures.ToArray();
                    Hands.ReleaseControl();
                }
            }

            private static IEnumerator Within(Func<bool> done, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                while (!done() && Time.realtimeSinceStartup < end) yield return null;
            }

            private static JToken Parse(string state) => string.IsNullOrEmpty(state) ? JValue.CreateNull() : JToken.Parse(state);
        }
    }
}
