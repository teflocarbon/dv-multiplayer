// Owned-items steps (docs/legacy/items/owned-items.md): an item's lifelong id, its ledger entry, and the
// ledger's check against the items. Each [Step] runs inside a game: call it through pair.Host /
// pair.Client. The ids are strings, so they cross into the runner as plain data.
using System;
using System.Collections;
using System.Linq;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.Access;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>An item's ledger entry as the host has it (Owned.Entry).</summary>
    public sealed class LedgerEntry
    {
        public bool Known, Live;
        public string Id = "", Owner = "", Prefab = "", Place = "", Hold = "", Holder = "", Anchor = "", Born = "", Ended = "";
        public int Slot = -1, ClaimSlot = -1;

        /// <summary>Where it is, comparable between two reads: the kind of place, how it is held and on what.</summary>
        public string Where => Place + (Hold.Length > 0 ? "/" + Hold : "") + (Holder.Length > 0 ? " " + Holder : "") +
                               (Anchor.Length > 0 ? " " + Anchor : "");

        public override string ToString() => Known
            ? $"{Prefab} {Id}: {(Live ? Where : "ended " + Ended)}, owner {Owner}, born {Born}"
            : $"{Id}: not in the ledger";
    }

    public static class Owned
    {
        /// <summary>Host: the item's lifelong id, or "" when its record has none.</summary>
        [Step]
        public static string IdOf(ushort netId) =>
            LiveItems.TryGet(netId, out var r) && r.PersistentItemId != Guid.Empty ? r.PersistentItemId.ToString("D") : "";

        /// <summary>Host: the net ID of the item with this id now, or 0. After a restart or a withdrawal
        /// the same item has a new net ID.</summary>
        [Step]
        public static ushort NetIdOf(string id)
        {
            if (!Guid.TryParse(id, out Guid wanted)) return 0;
            return LiveItems.GetAll().FirstOrDefault(r => r.PersistentItemId == wanted)?.NetId ?? 0;
        }

        /// <summary>Host: the ledger's entry for an id.</summary>
        [Step]
        public static LedgerEntry Entry(string id)
        {
            if (!Guid.TryParse(id, out Guid wanted) || !ServerLedger.Ledger.TryGet(wanted, out Multiplayer.Core.Items.Ledger.LedgerEntry e))
                return new LedgerEntry { Id = id ?? "" };
            return new LedgerEntry
            {
                Known = true,
                Live = e.IsLive,
                Id = e.Id.ToString("D"),
                Owner = e.Owner.ToString("D"),
                Prefab = e.Prefab,
                Place = e.Where.Place.ToString(),
                Hold = e.Where.Hold == HoldKind.None ? "" : e.Where.Hold.ToString(),
                Holder = e.Where.Holder == Guid.Empty ? "" : e.Where.Holder.ToString("D"),
                Anchor = e.Where.Anchor,
                Slot = e.Where.Slot,
                ClaimSlot = e.ClaimSlot,
                Born = e.Born.Reason.ToString(),
                Ended = e.Ended?.Reason.ToString() ?? ""
            };
        }

        /// <summary>Host: the ledger's entries for several ids, in order.</summary>
        [Step]
        public static LedgerEntry[] Entries(string[] ids) => ids.Select(Entry).ToArray();

        /// <summary>Host: what disagrees between the ledger, the records and vanilla's storage; empty when all agree.</summary>
        [Step]
        public static string[] Audit() => ServerLedger.Audit().ToArray();

        /// <summary>
        /// Host: what the audit says about these ids only. A test career collects debris from earlier
        /// runs (an inert Lost and Found item whose entry an older build dropped) that a scenario
        /// didn't make; it is noted, not failed on.
        /// </summary>
        [Step]
        public static IEnumerator AuditFor(Step<string[]> step, string[] ids)
        {
            string[] all = ServerLedger.Audit().ToArray();
            string[] mine = all.Where(line => ids.Any(id => line.Contains(id))).ToArray();
            foreach (string other in all.Except(mine))
                step.Note("audit, not this scenario's: " + other);
            step.Result = mine;
            yield break;
        }

        /// <summary>Host: whether the ledger has been loaded and checked against the stores since this game started.</summary>
        [Step]
        public static bool Loaded() => ServerLedger.Loaded;

        /// <summary>Host: the client's identity (the first player that isn't the host's own).</summary>
        [Step]
        public static string ClientIdentity()
        {
            var server = Multiplayer.Server.Session.NetworkServer.Instance;
            var host = server.LocalPlayer;
            return server.ServerPlayers.First(p => p != host && p.PlayerId != 0).Guid.ToString("D");
        }

        /// <summary>Host: the host's own player's identity, "" on a dedicated host.</summary>
        [Step]
        public static string HostIdentity() =>
            Multiplayer.Server.Session.NetworkServer.Instance.LocalPlayer?.Guid.ToString("D") ?? "";

        /// <summary>Host: the id of the item's Lost and Found entry, "" for none.</summary>
        [Step]
        public static string LostEntryId(ushort netId)
        {
            Guid id = LostAndFoundServer.EntryId(netId);
            return id == Guid.Empty ? "" : id.ToString("D");
        }

        /// <summary>Host: the next saves leave the ledger out, as a build without it would (until the game restarts).</summary>
        [Step]
        public static void SaveWithoutLedger() => ServerLedger.SaveWithoutLedger = true;

        /// <summary>
        /// Host: a copy of the item as a copied save or a dupe bug delivers one: a new item of the same
        /// prefab that arrives carrying the original's id, set before the server registers it.
        /// Result: the copy's net ID.
        /// </summary>
        [Step]
        public static IEnumerator Clone(Step<ushort> step, ushort netId)
        {
            NetworkedItem.TryGet(netId, out NetworkedItem original);
            if (original == null || !LiveItems.TryGet(netId, out var record)) throw new Exception("no item " + netId);
            var spec = DV.Globals.G.Items.items.First(i => i.ItemPrefabName == record.PrefabName);
            GameObject copy = UnityEngine.Object.Instantiate(spec.gameObject, original.transform.position + Vector3.up * 0.5f,
                Quaternion.identity, WorldMover.OriginShiftParent);
            // ItemIdentity is the mod's internal component.
            Type identityType = typeof(LiveItems).Assembly.GetType("Multiplayer.Server.Items.SaveLoad.ItemIdentity", true);
            Component identity = copy.GetComponent(identityType) ?? copy.AddComponent(identityType);
            identityType.GetProperty("Value").SetValue(identity, record.PersistentItemId);
            NetworkedItem item = copy.GetComponent<NetworkedItem>();
            yield return Wait.Until(() => item != null && item.NetId != 0 && LiveItems.TryGet(item.NetId, out _),
                10f, "the copy's record");
            Items.Track(item.NetId);
            step.Result = item.NetId;
        }

        /// <summary>This game's player drops the item out of their inventory (or hand) at their feet.</summary>
        [Step]
        public static IEnumerator DropFromInventory(ushort netId)
        {
            yield return Items.Arrive(netId);
            NetworkedItem.TryGet(netId, out NetworkedItem item);
            if (DV.InventorySystem.Inventory.Instance.DropItemFromHandsOrInventory(item.gameObject) == null)
                throw new Exception($"item {netId} could not be dropped from the inventory");
            for (int i = 0; i < 120; i++) yield return null;
        }

        /// <summary>This game's player walks to the item.</summary>
        [Step]
        public static IEnumerator GoTo(ushort netId)
        {
            yield return Items.Arrive(netId);
            NetworkedItem.TryGet(netId, out NetworkedItem item);
            yield return LocalPlayer.GoTo(item);
            for (int i = 0; i < 30; i++) yield return null;
        }

        /// <summary>Host: marks a prefab as a station fixture (in memory) or unmarks it. Result: whether it changed.</summary>
        [Step]
        public static bool SetFixture(string prefab, bool fixture)
        {
            if (!fixture) return ItemAccessServer.Fixtures.RemovePrefab(prefab);
            bool had = ItemAccessServer.Fixtures.Contains(prefab, null);
            ItemAccessServer.Fixtures.AddPrefab(prefab);
            return !had;
        }

        /// <summary>
        /// This game's player picks the item up and tries to put it in their inventory, then waits
        /// for the server's answer. Result: whether it is in their inventory 3 s later.
        /// </summary>
        [Step]
        public static IEnumerator TryStash(Step<bool> step, ushort netId)
        {
            yield return Items.Arrive(netId);
            NetworkedItem.TryGet(netId, out NetworkedItem item);
            if (Hands.Held != null) yield return Hands.Drop();
            yield return LocalPlayer.GoTo(item);
            yield return Hands.Pickup(netId);
            DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
            yield return new WaitForSecondsRealtime(3f);
            step.Result = item != null && DV.InventorySystem.Inventory.Instance.IndexOf(item.gameObject) >= 0;
            if (Hands.Held != null) yield return Hands.Drop();
        }

        /// <summary>Whether this game's player has the item in their inventory.</summary>
        [Step]
        public static bool InInventory(ushort netId) =>
            NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null &&
            DV.InventorySystem.Inventory.Instance.IndexOf(item.gameObject) >= 0;
    }
}
