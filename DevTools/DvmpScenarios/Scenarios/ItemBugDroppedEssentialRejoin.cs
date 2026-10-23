using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items;
using Multiplayer.Shared.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The client drops an essential (the comms radio) in the world, leaving its red silhouette in
    /// its slot, stashes MUGS ordinary items and leaves. When it rejoins, the silhouette must be back
    /// in the essential's claim slot (item-lifecycle.md, "Retrieval claim slots"), wherever the
    /// essential is.
    /// - Default (LOST=1), bug E10 (docs/research/item-bug-review.md), fixed: the client walks 300 m
    ///   off with nobody near the radio, and the server collects it into the client's Lost and
    ///   Found. After the rejoin the silhouette must be there and recall the radio into its slot.
    ///   Before the fix the server never sent the rejoined owner the item, so there was no
    ///   silhouette, only a shed's Lost and Found.
    /// - LOST=0 checks E1: the essential stays in the world, and ordinary items arriving first might
    ///   take its slot, the silhouette then refused (EnsureDroppedClaim never makes room). It rejoins
    ///   REJOINS times and fails on the first refusal. It held on a player host (6 rejoins).
    /// </summary>
    [Scenario("item-bug-dropped-essential-rejoin", "A dropped essential's silhouette is back in its slot after a rejoin, from Lost and Found (default, E10: and recalls it) or the world (LOST=0, E1)")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class ItemBugDroppedEssentialRejoin : Scenario<ItemBugDroppedEssentialRejoin.Options>
    {
        public sealed class Options
        {
            /// <summary>The essential's slot (0, the comms radio).</summary>
            public int Slot;
            /// <summary>How many rejoins to try before the run passes.</summary>
            public int Rejoins = 3;
            /// <summary>Personal mugs the client stashes first, so ordinary items are more likely to arrive before the silhouette.</summary>
            public int Mugs = 6;
            /// <summary>The essential is collected into the client's Lost and Found while it is away (the host's player walks off too), and the silhouette must come back from there.</summary>
            public bool Lost = true;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Recall.Dropped d = await pair.Client.Run<Recall.Dropped>(s => Recall.InGame.DropEssential(s, o.Slot));
            Report.Note($"the client dropped {d.Name} {d.Item} from slot {o.Slot}: {d.State}, silhouette {d.Reserved}");
            if (!d.Reserved) throw new HarnessException($"{d.Name} in slot {o.Slot} left no silhouette: not an essential (a run before left the inventory rearranged; reseed the save)");
            Require.Equal("Dropped", d.State, "the essential's state after the drop");
            pair.Cleanup(() => pair.Client.Run(() => InGame.Fetch(d.Item, o.Slot), 90));
            if (o.Mugs > 0)
            {
                ushort[] mugs = await pair.Host.Run<ushort[]>(s => ItemBugFullInventoryEssential.InGame.SpawnMany(s, "Mug", o.Mugs), 120);
                pair.Cleanup(() => pair.Host.Run<ushort[]>(s => Items.Destroy(s, mugs)));
                await pair.Client.Run<int>(s => ItemBugFullInventoryEssential.InGame.StashAll(s, mugs), 300);
            }
            if (o.Lost)
            {
                // Lost and Found collects an online owner's world item beyond 200 m with nobody
                // within 100 m (a dedicated host collects nothing while nobody is connected).
                await pair.Client.Run<Abs>(s => Walk.Away(s, 300, 0));
                if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 400, 0));
                bool lost = false;
                for (int t = 0; t < 30 && !lost; t++)
                {
                    await Task.Delay(2000);
                    lost = (await pair.Host.Run(() => Items.Record(d.Item))).Lost;
                }
                Report.Note($"with the client 300 m away: in its Lost and Found {lost}; server: " + await pair.Host.Run(() => InGame.ServerClaim(d.Item)));
                Require.That(lost, "the dropped essential is collected into the client's Lost and Found");
                string online = await pair.Client.Run(() => InGame.Silhouette(d.Item));
                Report.Note("before leaving: " + online);
                Expect.That(online.StartsWith("silhouette in slot"), $"before leaving, the {d.Name} in Lost and Found has its silhouette", online);
                await pair.Client.Quit();
                await Task.Delay(4000);
                await pair.Rejoin();
                await Task.Delay(12000);
                string at = await pair.Client.Run(() => InGame.Silhouette(d.Item));
                Report.Note("after the rejoin: " + at);
                if (!Expect.That(at.StartsWith("silhouette in slot"), $"the {d.Name} in Lost and Found has its silhouette in the client's inventory", at))
                    return;
                Recall.Recalled r = await pair.Client.Run<Recall.Recalled>(s => Recall.InGame.Recall(s, d.Item, o.Slot));
                Report.Note($"recalled from Lost and Found: slot {r.Slot}, {r.State} after {r.Seconds:0.#} s");
                Expect.Equal(o.Slot, r.Slot, $"the {d.Name} recalled from its silhouette is back in its slot");
                return;
            }
            await pair.Client.Run<Abs>(s => Walk.Away(s, 15, 0));
            Report.Note("server: " + await pair.Host.Run(() => InGame.ServerClaim(d.Item)));

            for (int i = 1; i <= o.Rejoins; i++)
            {
                await pair.Client.Quit();
                await Task.Delay(4000);
                await pair.Rejoin();
                await Task.Delay(12000);   // the client receives its items
                string slot = await pair.Client.Run(() => InGame.Silhouette(d.Item));
                Report.Note($"rejoin {i}: {slot}; server: {await pair.Host.Run(() => InGame.ServerClaim(d.Item))}");
                Expect.That(slot.StartsWith("silhouette in slot"), $"rejoin {i}: the dropped {d.Name}'s silhouette is in the client's inventory", slot);
                if (!slot.StartsWith("silhouette in slot")) return;
            }
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the server's record of the item, its placement and claim.</summary>
            [Step]
            public static string ServerClaim(ushort id) =>
                AuthoritativeItemRegistry.TryGet(id, out var r)
                    ? $"{r.Placement} owner {r.PersistentOwnerPlayerId}, claim slot {r.InventoryClaimSlot} ({r.InventoryClaimFlags})"
                    : "no record";

            /// <summary>Client: where the dropped essential's silhouette is, or what holds its claim
            /// slot instead.</summary>
            [Step]
            public static string Silhouette(ushort id)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                if (!NetworkedItem.TryGet(id, out NetworkedItem n) || n == null) return "the client has no item " + id;
                int slot = inv.IndexOf(n.gameObject);
                if (slot >= 0) return $"silhouette in slot {slot} (dropped {inv.GetSlotDroppedState(slot)}, reserved {inv.GetSlotReservedState(slot)})";
                int claim = n.InventoryClaimSlot;
                GameObject at = claim >= 0 ? inv.PeekItemAtSlot(claim, true) : null;
                return $"no silhouette: {n.GetItemState()}, claim slot {claim} holds {(at != null ? at.name.Replace("(Clone)", "") : "nothing")}";
            }

            /// <summary>Client, cleanup: <see cref="Fetch"/> for the item of this name the player
            /// owns (net IDs change across a restart).</summary>
            [Step]
            public static IEnumerator FetchByName(string name, int slot)
            {
                byte me = Multiplayer.Shared.Session.NetworkLifecycle.Instance.LocalSession.PlayerId;
                var item = Dev.All<NetworkedItem>().FirstOrDefault(n => n != null && n.NetId != 0 && n.PersistentOwnerPlayerId == me && n.name.Replace("(Clone)", "") == name);
                if (item != null) yield return Fetch(item.NetId, slot);
            }

            /// <summary>Client, cleanup: recalls the essential into <paramref name="slot"/>, or walks
            /// to it, picks it up and stashes it there.</summary>
            [Step]
            public static IEnumerator Fetch(ushort id, int slot)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) yield break;
                if (inv.IndexOf(item.gameObject) >= 0)
                {
                    NetworkedItemManager.Instance.RequestItemRecall(item, inv.IndexOf(item.gameObject));
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < 10f && item.GetItemState() != ItemState.InInventory) yield return null;
                }
                if (item.GetItemState() != ItemState.InInventory)
                {
                    if (Hands.Held != null) yield return Hands.Drop();
                    yield return LocalPlayer.GoTo(item);
                    yield return Hands.Pickup(id);
                    if (inv.PeekItemAtSlot(slot, true) is GameObject other && other != item.gameObject)
                        inv.MoveItemFromTo(slot, inv.GetLastFreeSlot(), false);
                    inv.AddItemToInventory(item.gameObject, slot, false);
                    for (int i = 0; i < 60; i++) yield return null;
                }
                int now = inv.IndexOf(item.gameObject);
                if (now >= 0 && now != slot && inv.PeekItemAtSlot(slot, true) == null) inv.MoveItemFromTo(now, slot, false);
                if (item.GetItemState() != ItemState.InInventory) throw new Exception($"couldn't get {item.name} back: {item.GetItemState()}");
            }
        }
    }
}
