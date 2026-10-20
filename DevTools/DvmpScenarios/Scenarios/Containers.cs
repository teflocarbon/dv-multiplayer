using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Session;
using UnityEngine;
using CUI = Multiplayer.Client.Items.ColdContainerUiIntegration;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Cold containers: the client picks up a container (making it theirs), opens it, deposits an
    /// item from the inventory, closes and reopens it (contents come from the host), and withdraws
    /// the item again; a stateful item (a battery) comes back with its state. The host must not be
    /// able to browse the client's container. WHO=host: the host's own player does the round trip
    /// instead, through its own container requests, which the server handles in-process (checked);
    /// needs a player host.
    /// </summary>
    [Scenario("containers", "Deposit, reopen and withdraw through a container the player holds, a stateful item keeping its state; the host can't browse the client's container (WHO=host: the host's own, handled in-process)")]
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
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host: a dedicated host has no player to open the container");
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, o.Who));
            ushort item = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, o.Who));
            Report.Note($"container {box}, item {item}");
            // Batteries are host-authoritative: drain the host's copy to a distinctive level and let it sync.
            await pair.Host.Run(() => InGame.Drain(item, 37f));

            Peer who = o.Who == "host" ? pair.Host : pair.Client;
            string[] log = await who.Run<string[]>(s => InGame.RoundTrip(s, box, item), 120);
            foreach (string line in log) Report.Note(line);
            if (o.Who == "host")
            {
                Expect.Empty(await pair.Host.Run(() => ServerRequests.NotHandledLocally(new[] { "ServerboundContainerBrowsePacket", "ServerboundContainerMutationPacket" })),
                    "the host's container requests that go over loopback");
                return;
            }
            Browse h = await pair.Host.Run(() => InGame.HostBrowse(box));
            Report.Note($"owner {h.Owner}, host browse: {h.Status} {h.Reason}");
            Expect.That(h.Owner != h.HostId, "the client's container isn't the host's", $"owner {h.Owner}, the host is {h.HostId}");
            Expect.Equal("Unauthorized", h.Status, "the host browsing the client's container");
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
                if (inv.IndexOf(item.gameObject) < 0 || item.GetItemState() != ItemState.InInventory) throw new Exception("item did not go into the inventory: " + item.GetItemState());
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
                    if (!r.Accepted) throw new Exception(what + " rejected: " + r.RejectionReason);
                    for (int i = 0; i < 60; i++) yield return null;   // the refreshed view
                }

                yield return Open();
                log.Add($"opened: {CUI.ActiveRowCount}/{CUI.ActiveCapacity}");
                int before = CUI.ActiveRowCount;
                yield return Mutate(() => !c.AddItem(item.gameObject, -1), "deposit");
                bool goneFromInventory = item == null || inv.IndexOf(item.gameObject) < 0;
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
                ushort materialized = CUI.LastMutation.MaterializedItemNetId;
                NetworkedItem back = null;
                float w0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - w0 < 10f && ((back = Find(materialized)) == null || inv.IndexOf(back.gameObject) < 0)) yield return null;
                if (back == null || inv.IndexOf(back.gameObject) < 0) throw new Exception($"withdrawn item {materialized} not in the inventory: " + string.Join("; ", log));
                Items.Track(back.NetId);
                log.Add($"withdrew: {back.NetId} {back.Item.InventorySpecs.ItemPrefabName} in slot {inv.IndexOf(back.gameObject)}, rows {CUI.ActiveRowCount}");
                float? powerAfter = back.GetComponentInChildren<Battery>(true)?.CurrentPower;
                if (powerBefore != null) log.Add($"battery {powerBefore:F1}% -> {powerAfter:F1}%");
                if (powerBefore != null && (powerAfter == null || Mathf.Abs(powerAfter.Value - powerBefore.Value) > 1f)) throw new Exception("item state lost: " + string.Join("; ", log));
                if (back.Item.InventorySpecs.ItemPrefabName != prefab || CUI.ActiveRowCount != before) throw new Exception("withdraw mismatch: " + string.Join("; ", log));
                c.ToggleContainerAccess();
                step.Result = log.ToArray();
            }

            /// <summary>Host: the container's owner, and what the host gets when it tries to browse it itself.</summary>
            [Step]
            public static Browse HostBrowse(ushort box)
            {
                var host = NetworkLifecycle.Instance.Server.LocalPlayer;
                AuthoritativeItemRegistry.TryGet(box, out var r);
                var result = NetworkedColdContainerManager.Browse(host, box, 0, 64, out var view);
                return new Browse
                {
                    Owner = r?.PersistentOwnerPlayerId ?? 0, HostId = host.PlayerId, Status = result.Status.ToString(),
                    Reason = result.Reason ?? "", Rows = view?.Slots?.Count ?? -1
                };
            }
        }
    }
}
