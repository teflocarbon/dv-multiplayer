using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using UnityEngine;
using CUI = Multiplayer.Client.Items.Containers.ContainerUi;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Cold containers: the client picks up a container (making it theirs), opens it, deposits an
    /// item from the inventory, closes and reopens it (contents come from the host), and withdraws
    /// the item again; a stateful item (a battery) comes back with its state. The host must not be
    /// able to browse the client's container. WHO=host: the host's own player does the round trip
    /// instead, through its own container requests, which the server handles in-process (checked);
    /// needs a player host. UNOWNED=1: the container is nobody's (spawned loose), and the player
    /// holding it opens it (OI-CON-08). NESTED=1: the item goes into a box that goes into another box
    /// (R5), the outer box is left on the ground, and the host saves and both games restart; the boxes
    /// are still nested with the item inside, the graph checks whole, and the player takes the inner
    /// box out and the item out of it.
    /// </summary>
    [Scenario("containers", "Deposit, reopen and withdraw through a container the player holds, a stateful item keeping its state; the host can't browse the client's container (WHO=host: the host's own, handled in-process; NESTED=1: a box in a box across a restart)")]
    [Needs(Need.RestartsGames)]
    public sealed class Containers : Scenario<Containers.Options>
    {
        public sealed class Options
        {
            /// <summary>The container's prefab.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>The item to store.</summary>
            public string Prefab = "Mug";
            /// <summary>Who does the round trip: client or host.</summary>
            public string Who = "client";
            /// <summary>The container is nobody's: the player who holds it opens it.</summary>
            public bool Unowned;
            /// <summary>A box in a box, across a save and restart.</summary>
            public bool Nested;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host: a dedicated host has no player to open the container");
            if (o.Nested)
            {
                await NestedAcrossRestart(pair, o);
                return;
            }
            ushort box = o.Unowned
                ? await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Container, o.Who))
                : await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, o.Who));
            ushort item = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, o.Who));
            Report.Note($"container {box}, item {item}");
            // Batteries are host-authoritative: drain the host's copy to a distinctive level and let it sync.
            await pair.Host.Run(() => InGame.Drain(item, 37f));

            Peer who = o.Who == "host" ? pair.Host : pair.Client;
            string[] log = await who.Run<string[]>(s => InGame.RoundTrip(s, box, item), 120);
            foreach (string line in log) Report.Note(line);
            if (o.Unowned)
            {
                OwnerInfo shell = await pair.Host.Run(() => Ownership.Read(box));
                Report.Note($"the shell after the round trip: {shell}");
                Expect.Equal(Guid.Empty.ToString("D"), shell.Owner, "the shell's owner (nobody: an unowned shell its holder opened)");
                string holder = o.Who == "host" ? await pair.Host.Run(() => Owned.HostIdentity()) : await pair.Host.Run(() => Owned.ClientIdentity());
                Expect.Equal(holder, shell.Holder, "who holds the shell (the player who opened it)");
            }
            if (o.Who == "host")
            {
                Expect.Empty(await pair.Host.Run(() => ServerRequests.NotHandledLocally(new[] { "ServerboundItemListRequestPacket", "ServerboundItemRequestPacket" })),
                    "the host's container requests that go over loopback");
                return;
            }
            // A dedicated host has no player of its own (stage 3) to try browsing with.
            if (pair.Dedicated) return;
            Browse h = await pair.Host.Run(() => InGame.HostBrowse(box));
            Report.Note($"owner {h.Owner}, host browse: {h.Status} {h.Reason}");
            Expect.That(h.Owner != h.HostId, "the client's container isn't the host's", $"owner {h.Owner}, the host is {h.HostId}");
            Expect.Equal("Unauthorized", h.Status, "the host browsing the client's container");
        }

        // NESTED=1: the item in the inner box, the inner box in the outer one, which is left on the
        // ground; after a save and restart both are where they were, and come out again.
        private static async Task NestedAcrossRestart(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            Peer who = o.Who == "host" ? pair.Host : pair.Client;
            string prefab = await pair.Host.Run<string>(s => Boxes.SelfNesting(s, o.Container), 120);
            if (prefab.Length == 0) throw new HarnessException("no container prefab takes a box of its own kind");
            ushort outer = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, o.Who));
            ushort inner = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, o.Who));
            ushort item = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, o.Who));
            string outerId = await pair.Host.Run(() => Owned.IdOf(outer));
            string innerId = await pair.Host.Run(() => Owned.IdOf(inner));
            string itemId = await pair.Host.Run(() => Owned.IdOf(item));
            Report.Note($"boxes {prefab}: outer {outer} ({outerId}), inner {inner} ({innerId}); {o.Prefab} {item} ({itemId})");
            var after = new List<ushort>();
            pair.Cleanup(async () =>
            {
                ushort[] left = await pair.Host.Run(() => Items.Existing(after.ToArray()));
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, left), 120);
            });

            Require.Equal(Boxes.Accepted, await who.Run<string>(s => Boxes.Deposit(s, inner, item), 120), "the item into the inner box");
            Require.Equal(Boxes.Accepted, await who.Run<string>(s => Boxes.Deposit(s, outer, inner), 120), "the inner box into the outer one");
            await who.Run(() => Items.Drop(), 10);
            Expect.Equal(outerId, await pair.Host.Run(() => Boxes.StoredIn(innerId)), "the box the inner box is in, before the restart");
            Expect.Equal(innerId, await pair.Host.Run(() => Boxes.StoredIn(itemId)), "the box the item is in, before the restart");
            int innerSlot = await pair.Host.Run(() => Boxes.StoredSlot(innerId));
            int itemSlot = await pair.Host.Run(() => Boxes.StoredSlot(itemId));

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart();
            await Task.Delay(12000);   // the client receives its items

            ushort outerNow = await pair.Host.Run(() => Owned.NetIdOf(outerId));
            Expect.That(outerNow != 0, "the outer box is in the world after the restart");
            if (outerNow != 0) after.Add(outerNow);
            Expect.Equal(outerId, await pair.Host.Run(() => Boxes.StoredIn(innerId)), "the box the inner box is in, after the restart");
            Expect.Equal(innerSlot, await pair.Host.Run(() => Boxes.StoredSlot(innerId)), "the inner box's slot, after the restart");
            Expect.Equal(innerId, await pair.Host.Run(() => Boxes.StoredIn(itemId)), "the box the item is in, after the restart");
            Expect.Equal(itemSlot, await pair.Host.Run(() => Boxes.StoredSlot(itemId)), "the item's slot, after the restart");
            Expect.Equal("Container", (await pair.Host.Run(() => Owned.Entry(innerId))).Place, "the inner box's ledger home, after the restart");
            Expect.Equal("Container", (await pair.Host.Run(() => Owned.Entry(itemId))).Place, "the item's ledger home, after the restart");
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph after the restart");
            if (outerNow == 0) return;

            string innerOut = await who.Run<string>(s => Boxes.Withdraw(s, outerNow, innerSlot), 120);
            Report.Note("the inner box out: " + innerOut);
            Require.That(innerOut.StartsWith(Boxes.Accepted), "the inner box out of the outer one", innerOut);
            ushort innerNow = ushort.Parse(innerOut.Substring(Boxes.Accepted.Length + 1));
            after.Add(innerNow);
            Expect.Equal(innerId, await pair.Host.Run(() => Owned.IdOf(innerNow)), "the inner box taken out keeps its id");
            string itemOut = await who.Run<string>(s => Boxes.Withdraw(s, innerNow, itemSlot), 120);
            Report.Note("the item out: " + itemOut);
            Require.That(itemOut.StartsWith(Boxes.Accepted), "the item out of the inner box", itemOut);
            ushort itemNow = ushort.Parse(itemOut.Substring(Boxes.Accepted.Length + 1));
            after.Add(itemNow);
            Expect.Equal(itemId, await pair.Host.Run(() => Owned.IdOf(itemNow)), "the item taken out keeps its id");
        }

        public sealed class Browse
        {
            public int Owner, HostId, Rows;
            public string Status, Reason;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: drains the item's battery, if it has one, to <paramref name="level"/>,
            /// and gives it 2 s to sync.</summary>
            [Step]
            public static IEnumerator Drain(ushort id, float level)
            {
                NetworkedItem.TryGet(id, out NetworkedItem n);
                var b = n.GetComponentInChildren<Battery>(true);
                if (b != null) b.Drain(b.CurrentPower - level);
                yield return new WaitForSeconds(2);
            }

            /// <summary>
            /// Puts the item in the inventory, opens the container (held), deposits the item,
            /// closes and reopens the container, and withdraws the item again; each mutation must be
            /// accepted and show in the view. Result: what happened, a line per step.
            /// </summary>
            [Step]
            public static IEnumerator RoundTrip(Step<string[]> step, ushort boxId, ushort itemId)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                var log = new List<string>();
                NetworkedItem Find(ushort id) => NetworkedItem.TryGet(id, out NetworkedItem n) ? n : null;
                NetworkedItem box = null, item = null;
                // A just-joined client can take a while.
                yield return Wait.Until(() => (box = Find(boxId)) != null && (item = Find(itemId)) != null, 20f, "the container and the item to arrive");
                string prefab = item.Item.InventorySpecs.ItemPrefabName;
                // A stateful item must come back with its state (Drain set the host's battery).
                float? powerBefore = item.GetComponentInChildren<Battery>(true)?.CurrentPower;

                var open = inv.ItemContainerRegistry.ActiveContainer;   // leftovers from an earlier run
                if (open != null) { open.ToggleContainerAccess(); for (int i = 0; i < 10; i++) yield return null; }
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(itemId);
                inv.AddItemToInventory(item.gameObject);
                for (int i = 0; i < 60; i++) yield return null;
                // On a client it's now a stand-in and its object is gone (D14).
                if (!PlayerInventory.Holds(itemId) || !PlayerInventory.IsStandIn(itemId) && item.GetItemState() != ItemState.InInventory)
                    throw new Exception("item did not go into the inventory: " + (item != null ? item.GetItemState().ToString() : "no object"));
                yield return LocalPlayer.GoTo(box);
                yield return Hands.Pickup(boxId);
                var c = box.GetComponentInChildren<DV.Items.ItemContainer>(true);

                IEnumerator Open()
                {
                    c.ToggleContainerAccess();
                    yield return Wait.Until(() => CUI.Active, 10f, "the container view");
                }
                IEnumerator Mutate(Func<bool> request, string what)
                {
                    uint seq = CUI.MutationSequence;
                    if (!request()) throw new Exception(what + " was not intercepted");
                    float t0 = Time.realtimeSinceStartup;
                    while (CUI.MutationSequence == seq && Time.realtimeSinceStartup - t0 < 10f) yield return null;
                    var r = CUI.LastMutation;
                    if (CUI.MutationSequence == seq || r == null) throw new Exception(what + ": no result");
                    if (!r.Accepted) throw new Exception(what + " rejected: " + r.Detail);
                    for (int i = 0; i < 60; i++) yield return null;   // the refreshed view
                }

                yield return Open();
                log.Add($"opened: {CUI.ActiveRowCount}/{CUI.ActiveCapacity}");
                int before = CUI.ActiveRowCount;
                yield return Mutate(() => !c.AddItem(PlayerInventory.SlotObject(itemId), -1), "deposit");
                bool goneFromInventory = PlayerInventory.Slot(itemId) < 0;
                log.Add($"deposited: rows {CUI.ActiveRowCount}, gone from inventory {goneFromInventory}");
                if (CUI.ActiveRowCount != before + 1 || !goneFromInventory) throw new Exception("deposit not reflected: " + string.Join("; ", log));
                int slot = CUI.ActiveSlots.Last();

                // Close and reopen: the contents come from the host.
                c.ToggleContainerAccess();
                for (int i = 0; i < 30; i++) yield return null;
                yield return Open();
                log.Add($"reopened: {CUI.ActiveRowCount} rows");
                if (CUI.ActiveRowCount != before + 1) throw new Exception("contents lost on reopen: " + string.Join("; ", log));

                yield return Mutate(() => !c.RemoveItem(slot, true, false), "withdraw");
                ushort materialized = CUI.LastMutation.ItemNetId;
                float w0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - w0 < 10f && !PlayerInventory.Holds(materialized)) yield return null;
                if (!PlayerInventory.Holds(materialized)) throw new Exception($"withdrawn item {materialized} not in the inventory: " + string.Join("; ", log));
                Items.Track(materialized);
                string backPrefab = PlayerInventory.PrefabOf(PlayerInventory.SlotObject(materialized));
                log.Add($"withdrew: {materialized} {backPrefab} in slot {PlayerInventory.Slot(materialized)}" +
                        $"{(PlayerInventory.IsStandIn(materialized) ? " (stand-in)" : "")}, rows {CUI.ActiveRowCount}");
                if (backPrefab != prefab || CUI.ActiveRowCount != before) throw new Exception("withdraw mismatch: " + string.Join("; ", log));
                c.ToggleContainerAccess();
                if (powerBefore != null)
                {
                    // A stand-in has no battery to read: put the box down and take the item out.
                    for (int i = 0; i < 30; i++) yield return null;
                    if (Hands.Held != null) yield return Hands.Drop();
                    yield return PlayerInventory.TakeOut(materialized);
                    float? powerAfter = Find(materialized)?.GetComponentInChildren<Battery>(true)?.CurrentPower;
                    log.Add($"battery {powerBefore:F1}% -> {powerAfter:F1}%");
                    if (powerAfter == null || Mathf.Abs(powerAfter.Value - powerBefore.Value) > 1f) throw new Exception("item state lost: " + string.Join("; ", log));
                }
                step.Result = log.ToArray();
            }

            /// <summary>Host: the container's owner, and what the host gets when it tries to browse it itself.</summary>
            [Step]
            public static Browse HostBrowse(ushort box)
            {
                var host = Multiplayer.Server.Session.NetworkServer.Instance.LocalPlayer;
                LiveItems.TryGet(box, out var r);
                var result = ContainerServer.Browse(host, box, 0, 64, out var view);
                return new Browse
                {
                    Owner = OwnerNumbers.Of(r?.Owner ?? System.Guid.Empty), HostId = host.PlayerId, Status = result.Status.ToString(),
                    Reason = result.Reason ?? "", Rows = view?.Slots?.Count ?? -1
                };
            }
        }
    }
}
