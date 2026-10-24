using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Item bug E2a (docs/research/item-bug-review.md), fixed: the client's compass is destroyed and
    /// its inventory is full of its own mugs when it rejoins. The server issues a new compass, which
    /// must end up in a slot or in the client's Lost and Found. Before the fix the server made it
    /// into a claim slot it thought free (it can't see where ordinary items sit), the client found
    /// the slot taken and nowhere to move the mug, and the compass was in no slot, at the world
    /// origin. Now the issuer counts what the player carries and sends what doesn't fit to Lost and
    /// Found (InventoryOverflow).
    /// </summary>
    [Scenario("item-bug-full-inventory-essential", "An essential issued into a full inventory at a rejoin goes to the player's Lost and Found, never into no slot (E2a)")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class ItemBugFullInventoryEssential : Scenario<ItemBugFullInventoryEssential.Options>
    {
        public sealed class Options
        {
            /// <summary>The essential the host destroys (the server issues a new one at the rejoin).</summary>
            public string Essential = "Compass";
            /// <summary>The prefab that fills the inventory.</summary>
            public string Filler = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Inventory before = await pair.Client.Run(() => InGame.Read(o.Essential));
            Report.Note($"client: {before.Free} free slots, {o.Essential} {before.Essential} in slot {before.EssentialSlot}");
            Require.That(before.Essential != 0, $"the client has a {o.Essential}");
            ushort[] destroyed = await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { before.Essential }));
            Require.That(destroyed.Length == 0, $"the host destroyed the client's {o.Essential}");
            await Task.Delay(2000);

            int need = before.Free + 1;   // the destroyed essential's slot is free too
            ushort[] fillers = await pair.Host.Run<ushort[]>(s => InGame.SpawnMany(s, o.Filler, need), 120);
            pair.Cleanup(async () =>
            {
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, fillers));
                await pair.Client.Run(() => InGame.Fetch(o.Essential), 90);
            });
            int stashed = await pair.Client.Run<int>(s => InGame.StashAll(s, fillers), 600);
            Inventory full = await pair.Client.Run(() => InGame.Read(o.Essential));
            Report.Note($"client stashed {stashed} {o.Filler}s: {full.Free} free slots");
            Require.That(full.Free == 0, "the client's inventory is full", full.Free + " free");

            await pair.Client.Quit();
            await Task.Delay(4000);
            await pair.Rejoin();
            await Task.Delay(15000);   // the client loads and the server issues the essential

            Inventory after = await pair.Client.Run(() => InGame.Read(o.Essential));
            Report.Note($"after the rejoin: {o.Essential} {after.Essential}: {after.Where}");
            ItemRecord record = after.Essential != 0 ? await pair.Host.Run(() => Items.Record(after.Essential)) : null;
            if (record != null) Report.Note($"server: {record.Placement} of {record.PlacementPlayer}, in Lost and Found {record.Lost}, last move {record.LastMove} {record.LastMoveDetail}");
            Expect.That(after.Essential != 0, $"the client owns a new {o.Essential}");
            Expect.That(after.EssentialSlot >= 0 || record?.Lost == true,
                $"the new {o.Essential} is in an inventory slot or in the client's Lost and Found", after.Where);
        }

        public sealed class Inventory
        {
            public int Free, EssentialSlot;
            public ushort Essential;
            public string Where;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: free slots, and where its <paramref name="prefab"/> is.</summary>
            [Step]
            public static Inventory Read(string prefab)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                byte me = NetworkLifecycle.Instance.LocalSession.PlayerId;
                // Stored on a client, it's a stand-in with no object (D14).
                var stored = PlayerInventory.Stored().FirstOrDefault(e => PlayerInventory.PrefabOf(e.go).Replace("(Clone)", "") == prefab);
                if (stored.go != null && PlayerInventory.IsStandIn(stored.netId))
                    return new Inventory
                    {
                        Free = Enumerable.Range(0, inv.Capacity).Count(s => inv.PeekItemAtSlot(s, true) == null),
                        Essential = stored.netId, EssentialSlot = stored.slot, Where = $"slot {stored.slot}"
                    };
                var item = Dev.All<NetworkedItem>().FirstOrDefault(n => n != null && n.NetId != 0 && n.Relation == ItemRelation.Mine && n.name.Replace("(Clone)", "") == prefab);
                int slot = item != null ? inv.IndexOf(item.gameObject) : -1;
                return new Inventory
                {
                    Free = Enumerable.Range(0, inv.Capacity).Count(s => inv.PeekItemAtSlot(s, true) == null),
                    Essential = item?.NetId ?? 0,
                    EssentialSlot = slot,
                    Where = item == null ? "none" : slot >= 0 ? $"slot {slot}" :
                        $"in no slot: {item.GetItemState()}, active {item.gameObject.activeInHierarchy}, at {Dev.Abs(item.transform.position)}, claim {item.InventoryClaimSlot} ({item.InventoryClaimFlags})"
                };
            }

            /// <summary>Host: <paramref name="count"/> personal items beside the client, in one go.
            /// Result: their net IDs.</summary>
            [Step]
            public static IEnumerator SpawnMany(Step<ushort[]> step, string prefab, int count)
            {
                var ids = new List<ushort>();
                for (int i = 0; i < count; i++)
                {
                    var one = new Step<ushort>();
                    yield return Items.SpawnPersonal(one, prefab, "client");
                    ids.Add(one.Result);
                }
                step.Result = ids.ToArray();
            }

            /// <summary>Client: picks each item up and stashes it in the first free slot. Result:
            /// how many went in.</summary>
            [Step]
            public static IEnumerator StashAll(Step<int> step, ushort[] ids)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                int count = 0;
                foreach (ushort id in ids)
                {
                    yield return Items.Arrive(id);
                    NetworkedItem.TryGet(id, out NetworkedItem item);
                    if (Hands.Held != null) yield return Hands.Drop();
                    yield return LocalPlayer.GoTo(item);
                    yield return Hands.Pickup(id);
                    if (inv.AddItemToInventory(item.gameObject) >= 0) count++;
                    for (int i = 0; i < 20; i++) yield return null;
                }
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = count;
            }

            /// <summary>Client, cleanup: its <paramref name="prefab"/> into the inventory if it's
            /// lying somewhere.</summary>
            [Step]
            public static IEnumerator Fetch(string prefab)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                byte me = NetworkLifecycle.Instance.LocalSession.PlayerId;
                var item = Dev.All<NetworkedItem>().FirstOrDefault(n => n != null && n.NetId != 0 && n.Relation == ItemRelation.Mine && n.name.Replace("(Clone)", "") == prefab);
                if (item == null || inv.IndexOf(item.gameObject) >= 0 || !item.gameObject.activeInHierarchy) yield break;
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(item.NetId);
                inv.AddItemToInventory(item.gameObject);
                for (int i = 0; i < 60; i++) yield return null;
            }
        }
    }
}
