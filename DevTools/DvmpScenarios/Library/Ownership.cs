// Ownership steps for the owned-items scenarios (owned-items.md, PRs 2a and 3): give an item to the
// client, the host's own player or a player who isn't here, read whose it is and where by its
// lifelong id (net IDs change at a restart), collect it into Lost and Found, and count each owner's
// Lost and Found. Each [Step] runs inside a game: call it through pair.Host / pair.Client.
using System;
using System.Collections;
using System.Linq;
using Multiplayer.Core.Items;
using Multiplayer.Server.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Dvmp.Scenarios
{
    /// <summary>Whose an item is and where, as the host has it (Ownership.Read / ById).</summary>
    public sealed class OwnerInfo
    {
        public bool Exists;
        public ushort NetId;
        public string Id = "", Owner = "", Placement = "", Holder = "", LostEntry = "", LedgerOwner = "", LedgerWhere = "";
        public int Number, PlacementPlayer;
        /// <summary>The owner number the client and the host's own player have now (0 for none).</summary>
        public int ClientNumber, HostNumber;

        public bool Lost => LostEntry.Length > 0;
        public bool Absent => Number == ItemTransitionCommand.AbsentOwnerId;

        public override string ToString() => Exists
            ? $"{NetId} {Id}: {Placement}{(Holder.Length > 0 ? " of " + Holder : "")}, owner {Owner} (number {Number}){(Lost ? ", in Lost and Found" : "")}; ledger {LedgerWhere}"
            : $"{Id}: no item";
    }

    public static class Ownership
    {
        /// <summary>
        /// Host: makes the item <paramref name="who"/>'s: "client", "host" (the host's own player) or
        /// "absent" (a player who isn't here, a new identity each call), or an identity itself (an
        /// absent player given more than one item). Result: whose it is now.
        /// </summary>
        [Step]
        public static OwnerInfo Give(ushort netId, string who)
        {
            if (!AuthoritativeItemRegistry.TryGet(netId, out var record)) throw new Exception("no record for " + netId);
            NetworkServer server = NetworkServer.Instance;
            Guid owner;
            if (!Guid.TryParse(who, out owner))
            {
                ServerPlayer player = who switch
                {
                    "client" => server.ServerPlayers.First(p => p != server.LocalPlayer && p.PlayerId != 0),
                    "host" => server.LocalPlayer ?? throw new Exception("a dedicated host has no player of its own"),
                    "absent" => null,
                    _ => throw new ArgumentException("who: client, host, absent or an identity, not " + who)
                };
                owner = player?.Guid ?? Guid.NewGuid();
            }
            if (NetworkedItem.TryGet(netId, out NetworkedItem item) && item.Item?.InventorySpecs != null)
                item.Item.InventorySpecs.BelongsToPlayer = true;
            record.PersistentOwnerIdentity = owner;
            record.PersistentOwnerPlayerId = OwnedItemsServer.OwnerNumber(owner);
            if (!OwnedItemsServer.Ledger.IsLive(record.PersistentItemId))
                OwnedItemsServer.Ledger.Register(new OwnedItemBirthRequest
                {
                    Id = record.PersistentItemId,
                    Owner = owner,
                    Prefab = record.PrefabName,
                    Where = OwnedItemsServer.WhereOf(record),
                    Reason = OwnedItemBirth.Granted,
                    Detail = "ownership scenario"
                });
            return Read(netId);
        }

        /// <summary>Host: whose the item is and where.</summary>
        [Step]
        public static OwnerInfo Read(ushort netId)
        {
            NetworkServer server = NetworkServer.Instance;
            ServerPlayer client = server.ServerPlayers.FirstOrDefault(p => p != server.LocalPlayer && p.PlayerId != 0);
            OwnerInfo info = new()
            {
                NetId = netId,
                ClientNumber = client?.PlayerId ?? 0,
                HostNumber = server.LocalPlayer?.PlayerId ?? 0
            };
            if (!AuthoritativeItemRegistry.TryGet(netId, out var record)) return info;
            Guid entry = NetworkedLostAndFoundManager.EntryId(netId);
            info.Exists = true;
            info.Id = record.PersistentItemId.ToString("D");
            info.Owner = record.PersistentOwnerIdentity.ToString("D");
            info.Number = record.PersistentOwnerPlayerId;
            info.Placement = record.Placement.ToString();
            info.PlacementPlayer = record.PlacementPlayerId;
            info.Holder = record.PlacementPlayerIdentity == Guid.Empty ? "" : record.PlacementPlayerIdentity.ToString("D");
            info.LostEntry = entry == Guid.Empty ? "" : entry.ToString("D");
            if (OwnedItemsServer.Ledger.TryGet(record.PersistentItemId, out OwnedItemEntry ledger))
            {
                info.LedgerOwner = ledger.Owner.ToString("D");
                info.LedgerWhere = ledger.IsLive ? ledger.Where.ToString() : "ended " + ledger.Ended.Reason;
            }
            return info;
        }

        /// <summary>Host: the item with this lifelong id now (its net ID changes at a restart), or Exists false.</summary>
        [Step]
        public static OwnerInfo ById(string id)
        {
            if (!Guid.TryParse(id, out Guid wanted)) return new OwnerInfo { Id = id ?? "" };
            AuthoritativeItemRegistry.Record record = AuthoritativeItemRegistry.GetAll().FirstOrDefault(r => r.PersistentItemId == wanted);
            return record == null ? new OwnerInfo { Id = id } : Read(record.NetId);
        }

        /// <summary>Host: collects the item into its owner's Lost and Found as the host's own code does. Result: whether it went.</summary>
        [Step]
        public static IEnumerator Collect(Step<bool> step, ushort netId, string reason)
        {
            NetworkedItem.TryGet(netId, out NetworkedItem item);
            LostItemReason why = (LostItemReason)Enum.Parse(typeof(LostItemReason), reason);
            step.Result = item != null && NetworkedLostAndFoundManager.Collect(item, why, "ownership scenario");
            for (int i = 0; i < 30; i++) yield return null;
        }

        /// <summary>Host: how many items in Lost and Found an owner has (by identity), bound or still waiting.</summary>
        [Step]
        public static int LostCount(string owner)
        {
            if (!Guid.TryParse(owner, out Guid who)) return -1;
            int bound = AuthoritativeItemRegistry.GetAll().Count(r => r.Placement == ItemPlacementKind.LostAndFound && r.PersistentOwnerIdentity == who);
            int waiting = NetworkedLostAndFoundManager.PendingEntries().Count(p => p.Owner == who);
            return bound + waiting;
        }

        /// <summary>Host: the ledger's live entries for an owner.</summary>
        [Step]
        public static int LedgerCount(string owner) =>
            Guid.TryParse(owner, out Guid who) ? OwnedItemsServer.Ledger.LiveOwnedBy(who).Count : -1;
    }
}
