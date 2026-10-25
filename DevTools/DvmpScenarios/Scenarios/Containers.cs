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
    /// holding it opens it (OI-CON-08). A box in a box across a restart is containers-nested.
    /// MODE=proxies (OI-CON-10; containers.md, "The client's view"): the client fills its box with
    /// two items and a box of the same kind, and opens it. Each stored row is a hidden proxy object
    /// in the client's game, a box's row with a <c>ColdProxyContainer</c> too, and none is an item:
    /// no <c>NetworkedItem</c>, no <c>ItemBase</c>, and the server keeps no record it didn't have
    /// before the box was opened. Closed, its proxies are gone; opened again, they are one per row
    /// again. Then the stored box's row is clicked as vanilla's inventory screen clicks it (its
    /// <c>ColdProxyContainer</c> made the active container): the view must show the stored box, the
    /// box opened first still vanilla's active one (before the R8 review's fix the view closed).
    /// </summary>
    [Scenario("containers", "Deposit, reopen and withdraw through a container the player holds, a stateful item keeping its state; the host can't browse the client's container (WHO=host: the host's own, handled in-process; MODE=proxies: the open box's rows are hidden proxies, never items)")]
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
            /// <summary>round-trip, or proxies (the client's view of an open box, OI-CON-10).</summary>
            public string Mode = "round-trip";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host: a dedicated host has no player to open the container");
            if (o.Mode == "proxies")
            {
                await Proxies(pair, o);
                return;
            }
            if (o.Mode != "round-trip") throw new ArgumentException("MODE: round-trip or proxies, not " + o.Mode);
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

        // MODE=proxies: the client's open box is shown through proxies that are never items.
        private static async Task Proxies(Pair pair, Options o)
        {
            if (o.Who != "client") throw new ArgumentException("MODE=proxies is the client's view: WHO=client");
            // A box that takes a box of its kind, so one row is a box and gets a ColdProxyContainer.
            string boxPrefab = await pair.Host.Run<string>(s => Boxes.SelfNesting(s, o.Container));
            if (boxPrefab.Length == 0) throw new HarnessException("no container the game has takes a box of its own kind");
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, boxPrefab, "client"));
            ushort inner = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, boxPrefab, "client"));
            ushort first = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            ushort second = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            Report.Note($"{boxPrefab} {box} gets {o.Prefab} {first}, {o.Prefab} {second} and {boxPrefab} {inner}");
            foreach (ushort stored in new[] { first, second, inner })
                Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, box, stored), 120), $"the deposit of {stored}");

            // Opened, its three rows are proxies in the client's game and nothing else.
            ushort[] before = await pair.Host.Run(() => InGame.RecordIds());
            await pair.Client.Run(() => Boxes.OpenBox(box), 60);
            ProxyView open = await pair.Client.Run(() => InGame.ProxiesNow());
            string[] made = await pair.Host.Run(() => InGame.NewRecords(before));
            Report.Note($"open: {open}; the server's records {before.Length}, new since the box opened: {(made.Length == 0 ? "none" : string.Join(", ", made))}");
            Require.That(open.Active, "the box's view is open on the client");
            Expect.Equal(3, open.Rows, "rows in the open box's list");
            // Each would fail if a row were made a real object: an item in the game and on the server.
            Expect.Equal(open.Rows, open.Proxies, "proxy objects in the client's game (one per row)");
            Expect.Equal(open.Rows, open.ForRows, "proxies naming a row of the open list");
            Expect.Equal(open.Rows, open.Hidden, "proxies hidden (HideAndDontSave)");
            Expect.Equal(1, open.Containers, "ColdProxyContainers (the stored box's row)");
            Expect.Zero(open.WithNetworkedItem, "proxies with a NetworkedItem");
            Expect.Zero(open.WithItemBase, "proxies with an ItemBase");
            Expect.Empty(made, "records the server made while the box was open (a proxy kept as an item)");
            await pair.Client.Run(() => Boxes.CloseBox(box), 60);
            // Closed, its proxies go with it (ContainerUi.Closed, from vanilla's active container
            // changing); before R8 they stayed, hidden, until the next list replaced them.
            ProxyView closed = await pair.Client.Run(() => InGame.ProxiesNow());
            Report.Note($"closed: {closed}");
            Expect.That(!closed.Active, "the box's view is closed on the client");
            Expect.Zero(closed.Proxies, "proxy objects left after the box closed");

            // Opened again: the old proxies go, and there is again one per row, not twice as many.
            await pair.Client.Run(() => Boxes.OpenBox(box), 60);
            ProxyView again = await pair.Client.Run(() => InGame.ProxiesNow());
            Report.Note($"reopened: {again}");
            Expect.Equal(open.Rows, again.Rows, "rows in the reopened box's list");
            Expect.Equal(again.Rows, again.Proxies, "proxy objects after reopening (the old ones destroyed)");
            Expect.Zero(again.WithNetworkedItem + again.WithItemBase, "reopened proxies with a NetworkedItem or an ItemBase");

            // The stored box's row clicked on the inventory screen, as vanilla's click does it (its
            // ColdProxyContainer made the active container): the view moves to the stored box's
            // list, the box shown still vanilla's active one. Before the fix the view closed and its
            // proxies went, the one just made active with them.
            ChildView child = await pair.Client.Run<ChildView>(s => InGame.ClickBoxRow(s), 30);
            Report.Note($"the stored box's row clicked: {child}");
            Expect.That(child.Active, "the view is open after the stored box's row is clicked");
            Expect.Equal(child.RowHandle, child.ViewHandle, "the view shows the stored box (its row's handle)");
            Expect.Zero(child.Rows, "rows in the stored box's list (it's empty)");
            Expect.That(child.ShellActive, "vanilla's active container is the box shown, not the row's proxy");
            await pair.Client.Run(() => Boxes.CloseBox(box), 60);
        }

        /// <summary>The view after a stored box's row is clicked (InGame.ClickBoxRow).</summary>
        public sealed class ChildView
        {
            public bool Active, ShellActive;
            public uint RowHandle, ViewHandle;
            public int Rows;
            public override string ToString() =>
                $"{(Active ? "open" : "closed")} on {ViewHandle} (the row's {RowHandle}), {Rows} row(s), " +
                $"{(ShellActive ? "the box shown" : "not the box shown")} active in vanilla";
        }

        /// <summary>The client's open box's view and the proxy objects in its game (InGame.ProxiesNow).</summary>
        public sealed class ProxyView
        {
            public bool Active;
            public int Rows, Proxies, ForRows, Hidden, Containers, WithNetworkedItem, WithItemBase;
            public override string ToString() =>
                $"{(Active ? "open" : "closed")}, {Rows} row(s), {Proxies} proxy object(s) ({ForRows} for a row, {Hidden} hidden, " +
                $"{Containers} ColdProxyContainer(s), {WithNetworkedItem} with a NetworkedItem, {WithItemBase} with an ItemBase)";
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

            /// <summary>
            /// Client: the open box's list and every container proxy object in this game, hidden ones
            /// included (Resources.FindObjectsOfTypeAll): how many name a row of the list, are hidden,
            /// carry a ColdProxyContainer, a NetworkedItem or an ItemBase.
            /// </summary>
            [Step]
            public static ProxyView ProxiesNow()
            {
                var rows = CUI.Active ? CUI.activeView?.Rows ?? Array.Empty<Multiplayer.Shared.Protocol.Data.Items.ItemRow>() : Array.Empty<Multiplayer.Shared.Protocol.Data.Items.ItemRow>();
                var proxies = Resources.FindObjectsOfTypeAll<Multiplayer.Client.Items.Containers.ColdContainerItemProxy>()
                    .Where(p => p != null).ToArray();
                return new ProxyView
                {
                    Active = CUI.Active,
                    Rows = rows.Length,
                    Proxies = proxies.Length,
                    ForRows = proxies.Count(p => p.ContainerHandle == CUI.ActiveHandle && rows.Any(r => r.Handle == p.ItemHandle && r.Slot == p.Slot)),
                    Hidden = proxies.Count(p => (p.gameObject.hideFlags & HideFlags.HideAndDontSave) == HideFlags.HideAndDontSave),
                    Containers = proxies.Count(p => p.GetComponent<Multiplayer.Client.Items.Containers.ColdProxyContainer>() != null),
                    WithNetworkedItem = proxies.Count(p => p.GetComponentInChildren<NetworkedItem>(true) != null),
                    WithItemBase = proxies.Count(p => p.GetComponentInChildren<DV.CabControls.ItemBase>(true) != null)
                };
            }

            /// <summary>
            /// Client: clicks the open box's stored-box row as vanilla's inventory screen does
            /// (<c>ToggleActiveContainerOnClick</c>: the row's container made the active one), and
            /// waits up to 10 s for the view to show it. Result: the view then.
            /// </summary>
            [Step]
            public static IEnumerator ClickBoxRow(Step<ChildView> step)
            {
                var registry = DV.InventorySystem.Inventory.Instance.ItemContainerRegistry;
                DV.InventorySystem.AItemContainer shell = registry.ActiveContainer;
                var row = Resources.FindObjectsOfTypeAll<Multiplayer.Client.Items.Containers.ColdProxyContainer>()
                    .FirstOrDefault(c => c != null) ?? throw new Exception("no stored box's row in the open view");
                uint handle = row.Handle;
                registry.ActiveContainer = row;
                float until = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < until && !(CUI.Active && CUI.ActiveHandle == handle))
                    yield return null;
                step.Result = new ChildView
                {
                    Active = CUI.Active, RowHandle = handle, ViewHandle = CUI.ActiveHandle,
                    Rows = CUI.activeView?.Rows?.Length ?? 0,
                    ShellActive = shell != null && registry.ActiveContainer == shell
                };
            }

            /// <summary>Host: the net IDs of every record the server keeps.</summary>
            [Step]
            public static ushort[] RecordIds() => LiveItems.GetAll().Select(r => r.NetId).ToArray();

            /// <summary>Host: the records the server keeps that weren't among <paramref name="before"/>, as "netId prefab placement".</summary>
            [Step]
            public static string[] NewRecords(ushort[] before)
            {
                var known = new HashSet<ushort>(before);
                return LiveItems.GetAll().Where(r => !known.Contains(r.NetId))
                    .Select(r => $"{r.NetId} {r.PrefabName} {r.Placement}").ToArray();
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
