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
    /// On a client the slot holds a stand-in and no object while it's stored; taken out, the object
    /// is made in that frame with the saved state it went in with, and the frame it takes stays
    /// within MAX_MS. The server and the ledger have it in the slot after each stash. WHO=host does
    /// it with the host's own player, which keeps objects until R4C C4. PREFABS= limits the list.
    /// </summary>
    [Scenario("hotbar-cycle", "Every vanilla item stashed into the hotbar, taken out and stashed again: no object while stored, its state in the first frame, the frame time bounded")]
    [Budget(1500)]
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
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            if (host && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = host ? pair.Host : pair.Client;
            string[] prefabs = o.Prefabs.Length > 0
                ? o.Prefabs.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray()
                : await who.Run(() => InGame.Prefabs());
            Report.Note($"{prefabs.Length} prefabs: {string.Join(", ", prefabs)}");
            var slow = new List<string>();
            foreach (string prefab in prefabs)
            {
                ushort id = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, o.Who));
                Cycled c = await who.Run<Cycled>(s => InGame.Cycle(s, id), 90);
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
                    ServerSlot server = await pair.Host.Run(() => Ledger.Where(id));
                    Expect.Equal(c.Slot, server.Slot, $"{prefab}: the server's slot after the second stash");
                    Expect.Equal(c.Slot, server.LedgerSlot, $"{prefab}: the ledger's slot after the second stash");
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
                                                    Multiplayer.Client.Items.Projection.ClientItemManager.CanStandIn(s.ItemPrefabName))
                    .Select(s => s.ItemPrefabName).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();

            /// <summary>
            /// This player picks the item up with a free hotbar slot selected, stashes it through the
            /// hotbar, takes it out and stashes it again. Result: the slot, the take-out's frame
            /// time, and what went wrong.
            /// </summary>
            [Step]
            public static IEnumerator Cycle(Step<Cycled> step, ushort id)
            {
                var result = new Cycled();
                var failures = new List<string>();
                step.Result = result;
                var inv = Inventory.Instance;
                var handler = UnityEngine.Object.FindObjectOfType<GrabberStashingHandler>() ?? throw new Exception("no GrabberStashingHandler");
                var hotbar = UnityEngine.Object.FindObjectOfType<HotbarController>() ?? throw new Exception("no HotbarController");
                bool remote = Multiplayer.Shared.Session.NetworkRole.IsRemoteClient;

                int slot = Enumerable.Range(InventoryUtils.HOTBAR_START_INDEX, InventoryUtils.HOTBAR_SIZE)
                    .Where(s => inv.PeekItemAtSlot(s, true) == null && !inv.GetSlotReservedState(s)).DefaultIfEmpty(-1).First();
                if (slot < 0) throw new Exception("no free hotbar slot");
                hotbar.UpdateSelectedSlot(slot, false);
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                if (Hands.Held == null || Hands.Held.NetId != id) throw new Exception("couldn't pick it up");
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
                yield return Wait.Until(() => PlayerInventory.Holds(id), 5f, "the first stash");
                if (PlayerInventory.Slot(id) != slot && !PlayerInventory.MoveTo(id, slot)) throw new Exception($"couldn't move it to hotbar slot {slot}");
                yield return new WaitForSecondsRealtime(1.5f);
                if (remote && !PlayerInventory.IsStandIn(id)) failures.Add("stashed, a client kept its object, not a stand-in");
                if (remote && NetworkedItem.TryGet(id, out NetworkedItem left) && left != null) failures.Add("stashed, its object is still there");

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
                yield return Wait.Until(() => Hands.Held != null && Hands.Held.NetId == id, 5f, "the item in the hand");
                yield return new WaitForSecondsRealtime(1.5f);
                if (Hands.Held == null || Hands.Held.NetId != id) failures.Add("taken out, it left the hand (rolled back?)");

                // In again.
                hotbar.UpdateSelectedSlot(slot, false);
                handler.StashToggle(slot);
                yield return Wait.Until(() => PlayerInventory.Holds(id), 5f, "the second stash");
                yield return new WaitForSecondsRealtime(1.5f);
                result.Slot = PlayerInventory.Slot(id);
                result.StandIn = PlayerInventory.IsStandIn(id);
                if (result.Slot != slot) failures.Add($"stashed again into slot {result.Slot}, not {slot}");
                if (remote && !result.StandIn) failures.Add("stashed again, a client kept its object");
                Hands.ReleaseControl();
                result.Failures = failures.ToArray();
            }

            private static JToken Parse(string state) => string.IsNullOrEmpty(state) ? JValue.CreateNull() : JToken.Parse(state);
        }
    }
}
