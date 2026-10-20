using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Disconnect and rejoin. The client holds an item and quits: the host must keep every item of
    /// the departed player, with the held one dropped into the world. The client rejoins and must
    /// get exactly one of each item back, in its inventory. CRASH=1 kills the client instead and
    /// rejoins before the host has noticed (a stale-session reconnect); FRESH=1 restarts both
    /// games first.
    /// </summary>
    [Scenario("rejoin", "The client quits (or crashes) holding an item: the host keeps all its items, and after a rejoin it has each back once, in its inventory")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class Rejoin : Scenario<Rejoin.Options>
    {
        public sealed class Options
        {
            /// <summary>Kill the client and rejoin straight away, while the host still thinks the old
            /// connection is alive (it only notices on timeout).</summary>
            public bool Crash;
            /// <summary>Restart both games first.</summary>
            public bool Fresh;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Fresh) await pair.Restart();
            // A fresh mug at the client's feet, so the test doesn't depend on loose items nearby.
            await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "client"));
            Holding before = await pair.Client.Run<Holding>(s => InGame.Hold(s));
            Report.Note($"holding {before.Item} ({before.Name}); owns {before.Inventory.Length} items");

            if (o.Crash) await pair.Client.Kill();
            else
            {
                await pair.Client.Quit();
                await Task.Delay(4000);   // the host notices the departure
                Expect.Empty(await pair.Host.Run(() => InGame.AfterDisconnect(before.Item, before.Inventory)), "the host keeps every item of the departed player");
            }

            await pair.Rejoin();
            await pair.Client.Run(() => InGame.Settle(8f));
            Returned after = await pair.Client.Run(() => InGame.AfterRejoin(before.Names, before.Slots));
            Report.Note($"owned {after.Owned}" + (after.Moved.Length > 0 ? "; slots changed (not failed): " + string.Join(", ", after.Moved) : string.Empty));
            Expect.Empty(after.Failures, "after the rejoin: each item back once, in the inventory");
        }

        public sealed class Holding
        {
            public ushort Item;
            public string Name;
            public ushort[] Inventory;
            /// <summary>Owned items' names, one entry per item.</summary>
            public string[] Names;
            /// <summary>"netId@slot" per owned item.</summary>
            public string[] Slots;
        }

        public sealed class Returned
        {
            public int Owned;
            public string[] Failures, Moved;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: picks up the nearest loose item, and reports it and everything this
            /// player owns.</summary>
            [Step]
            public static IEnumerator Hold(Step<Holding> step)
            {
                byte me = NetworkLifecycle.Instance.LocalSession.PlayerId;
                if (Hands.Held != null) yield return Hands.Drop();
                var item = Dev.All<NetworkedItem>()
                    .Where(n => n.NetId != 0 && n.gameObject.activeInHierarchy && n.GetItemState() == ItemState.Dropped
                                && n.GetComponent<DV.Interaction.GrabHandlerItem>() != null && TrainCar.Resolve(n.transform) == null)
                    .OrderBy(n => Vector3.Distance(n.transform.position, PlayerManager.PlayerTransform.position)).First();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(item.NetId);
                yield return new WaitForSeconds(2);
                Hands.ReleaseControl();
                var owned = Dev.All<NetworkedItem>().Where(n => n.NetId != 0 && n.GetItemState() == ItemState.InInventory && n.PersistentOwnerPlayerId == me).ToList();
                step.Result = new Holding
                {
                    Item = Hands.Held.NetId,
                    Name = Hands.Held.name,
                    Inventory = owned.Select(n => n.NetId).ToArray(),
                    Names = owned.Select(n => n.name.Replace("(Clone)", "")).OrderBy(s => s).ToArray(),
                    Slots = owned.Select(n => n.NetId + "@" + DV.InventorySystem.Inventory.Instance.IndexOf(n.gameObject)).ToArray()
                };
            }

            /// <summary>Host, after the client quit: every item it had still exists and is in the
            /// authority registry; the held one is recorded and shown dropped in the world, with
            /// colliders. Result: what's wrong.</summary>
            [Step]
            public static string[] AfterDisconnect(ushort held, ushort[] inventory)
            {
                var failures = new List<string>();
                foreach (ushort id in inventory.Append(held))
                {
                    var n = Dev.All<NetworkedItem>().FirstOrDefault(i => i != null && i.NetId == id);
                    if (n == null) failures.Add($"{id} destroyed");
                    else if (!AuthoritativeItemRegistry.TryGet(id, out _)) failures.Add($"{id} missing from authority registry");
                }
                var h = Dev.All<NetworkedItem>().FirstOrDefault(i => i != null && i.NetId == held);
                if (h != null)
                {
                    AuthoritativeItemRegistry.TryGet(held, out var record);
                    if (record == null || record.Placement != ItemPlacementKind.World) failures.Add($"held item record is {record?.Placement}, expected World");
                    if (h.GetItemState() != ItemState.Dropped) failures.Add($"held item state {h.GetItemState()}, expected Dropped");
                    if (h.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).All(c => !c.enabled)) failures.Add("held item has no enabled colliders");
                }
                return failures.ToArray();
            }

            /// <summary>The rejoined game answers and runs for a while. The wait is what's tested:
            /// a rejoined client that stops answering fails here.</summary>
            [Step]
            public static IEnumerator Settle(float seconds)
            {
                yield return new WaitForSeconds(seconds);
            }

            /// <summary>
            /// Client, after rejoining: every owned item back once (as many of each kind as before
            /// leaving), in the inventory, and no unbound duplicates. Moved: items back in another
            /// slot, reported and not failed: the host doesn't track where ordinary items sit, so a
            /// rearranged inventory comes back in the starting layout (known limitation).
            /// </summary>
            [Step]
            public static Returned AfterRejoin(string[] names, string[] slots)
            {
                byte me = NetworkLifecycle.Instance.LocalSession.PlayerId;
                var mine = Dev.All<NetworkedItem>().Where(n => n.NetId != 0 && n.PersistentOwnerPlayerId == me).ToList();
                string Name(NetworkedItem n) => n.name.Replace("(Clone)", "");
                var failures = new List<string>();
                // Items that were in the inventory before leaving (an owned item left lying in the world stays there).
                var wasInInventory = new HashSet<ushort>(slots.Where(e => e.Contains("@")).Select(e => ushort.Parse(e.Substring(0, e.IndexOf('@')))));
                foreach (var n in mine.Where(n => wasInInventory.Contains(n.NetId) && n.GetItemState() != ItemState.InInventory && n.GetItemState() != ItemState.InHand))
                    failures.Add($"{Name(n)} {n.NetId} is {n.GetItemState()}, expected in inventory");
                var expected = names.Where(s => s.Length > 0).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
                var actual = mine.Where(n => n.GetItemState() == ItemState.InInventory).GroupBy(Name).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var kv in expected)
                {
                    int have = actual.TryGetValue(kv.Key, out var items) ? items.Count : 0;
                    if (have < kv.Value)
                        failures.Add($"{kv.Key}: {have} of {kv.Value} returned (missing: {string.Join(", ", wasInInventory.Where(id => !mine.Any(n => n.NetId == id && Name(n) == kv.Key) && !actual.Values.SelectMany(v => v).Any(n => n.NetId == id)).Select(id => id + (NetworkedItem.TryGet(id, out NetworkedItem m) && m != null ? "=" + m.GetItemState() + "/o" + m.PersistentOwnerPlayerId : "=gone")))})");
                }
                foreach (var kv in actual)
                {
                    int want = expected.TryGetValue(kv.Key, out int w) ? w : 0;
                    if (kv.Value.Count > want) failures.Add($"{kv.Key} x{kv.Value.Count}, expected {want} ({string.Join(", ", kv.Value.Select(n => n.NetId))})");
                }
                // Unbound local copies (NetId 0) sitting in inventory slots are duplicates the player would see.
                var inventory = DV.InventorySystem.Inventory.Instance;
                for (int slot = 0; slot < inventory.Capacity; slot++)
                {
                    var go = inventory.PeekItemAtSlot(slot, false);
                    var n = go != null ? go.GetComponent<NetworkedItem>() : null;
                    if (go != null && (n == null || n.NetId == 0))
                        failures.Add($"unbound local {go.name.Replace("(Clone)", "")} in slot {slot}");
                }
                var moved = new List<string>();
                foreach (var entry in slots.Where(e => e.Contains("@")))
                {
                    ushort id = ushort.Parse(entry.Substring(0, entry.IndexOf('@')));
                    int slot = int.Parse(entry.Substring(entry.IndexOf('@') + 1));
                    var item = mine.FirstOrDefault(n => n.NetId == id);
                    int now = item != null ? inventory.IndexOf(item.gameObject) : -1;
                    if (item != null && now != slot) moved.Add($"{Name(item)} {slot}->{now}");
                }
                return new Returned { Owned = mine.Count, Failures = failures.ToArray(), Moved = moved.ToArray() };
            }
        }
    }
}
