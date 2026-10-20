// Owned-items steps (docs/design/owned-items.md): an item's lifelong id, its ledger entry, and the
// ledger's check against the items. Each [Step] runs inside a game: call it through pair.Host /
// pair.Client. The ids are strings, so they cross into the runner as plain data.
using System;
using System.Collections;
using System.Linq;
using Dvmp.Agent;
using Multiplayer.Core.Items;
using Multiplayer.Server.Items;
using Multiplayer.Shared.Items;
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
            AuthoritativeItemRegistry.TryGet(netId, out var r) && r.PersistentItemId != Guid.Empty ? r.PersistentItemId.ToString("D") : "";

        /// <summary>Host: the net ID of the item with this id now, or 0. After a restart or a withdrawal
        /// the same item has a new net ID.</summary>
        [Step]
        public static ushort NetIdOf(string id)
        {
            if (!Guid.TryParse(id, out Guid wanted)) return 0;
            return AuthoritativeItemRegistry.GetAll().FirstOrDefault(r => r.PersistentItemId == wanted)?.NetId ?? 0;
        }

        /// <summary>Host: the ledger's entry for an id.</summary>
        [Step]
        public static LedgerEntry Entry(string id)
        {
            if (!Guid.TryParse(id, out Guid wanted) || !OwnedItemsServer.Ledger.TryGet(wanted, out OwnedItemEntry e))
                return new LedgerEntry { Id = id ?? "" };
            return new LedgerEntry
            {
                Known = true,
                Live = e.IsLive,
                Id = e.Id.ToString("D"),
                Owner = e.Owner.ToString("D"),
                Prefab = e.Prefab,
                Place = e.Where.Place.ToString(),
                Hold = e.Where.Hold == OwnedItemHold.None ? "" : e.Where.Hold.ToString(),
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
        public static string[] Audit() => OwnedItemsServer.Audit().ToArray();

        /// <summary>Host: whether the ledger has been loaded and checked against the stores since this game started.</summary>
        [Step]
        public static bool Loaded() => OwnedItemsServer.Loaded;

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
            Guid id = NetworkedLostAndFoundManager.EntryId(netId);
            return id == Guid.Empty ? "" : id.ToString("D");
        }

        /// <summary>Host: the next saves leave the ledger out, as a build without it would (until the game restarts).</summary>
        [Step]
        public static void SaveWithoutLedger() => OwnedItemsServer.SaveWithoutLedger = true;

        /// <summary>
        /// Host: a clone of the item made the way a dupe bug would, with Instantiate, which copies its
        /// serialized identity. Result: the clone's net ID.
        /// </summary>
        [Step]
        public static IEnumerator Clone(Step<ushort> step, ushort netId)
        {
            NetworkedItem.TryGet(netId, out NetworkedItem original);
            if (original == null) throw new Exception("no item " + netId);
            GameObject copy = UnityEngine.Object.Instantiate(original.gameObject, original.transform.position + Vector3.up * 0.5f,
                original.transform.rotation, original.transform.parent);
            NetworkedItem item = copy.GetComponent<NetworkedItem>();
            yield return Wait.Until(() => item != null && item.NetId != 0 && item.NetId != netId &&
                                          AuthoritativeItemRegistry.TryGet(item.NetId, out _), 10f, "the clone's record");
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

        /// <summary>Whether this game's player has the item in their inventory.</summary>
        [Step]
        public static bool InInventory(ushort netId) =>
            NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null &&
            DV.InventorySystem.Inventory.Instance.IndexOf(item.gameObject) >= 0;
    }
}
