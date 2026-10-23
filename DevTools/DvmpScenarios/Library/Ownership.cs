// Ownership steps for the owned-items scenarios (owned-items.md, PRs 2a and 3): give an item to the
// client, the host's own player or a player who isn't here, read whose it is and where by its
// lifelong id (net IDs change at a restart), collect it into Lost and Found, and count each owner's
// Lost and Found. Each [Step] runs inside a game: call it through pair.Host / pair.Client.
using System;
using System.Collections;
using System.Linq;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Dvmp.Scenarios
{
    /// <summary>Whose an item is and where, as the host has it (Ownership.Read / ById).</summary>
    public sealed class OwnerInfo
    {
        public bool Exists;
        public ushort NetId;
        public string Id = "", Owner = "", Placement = "", Holder = "", LostEntry = "", LedgerOwner = "", LedgerWhere = "";
        /// <summary>The owner number sent on the wire (until R2), and the holder's session number.</summary>
        public int Number, PlacementPlayer;
        /// <summary>The session numbers the client and the host's own player have now (0 for none).</summary>
        public int ClientNumber, HostNumber;
        /// <summary>Whether the owner's identity has a session now.</summary>
        public bool OwnerConnected;

        public bool Lost => LostEntry.Length > 0;
        /// <summary>Owned by an identity with no session: an offline owner (item-refactor.md, "Identity only").</summary>
        public bool Absent => Owner.Length > 0 && Owner != Guid.Empty.ToString("D") && !OwnerConnected;

        public override string ToString() => Exists
            ? $"{NetId} {Id}: {Placement}{(Holder.Length > 0 ? " of " + Holder : "")}, owner {Owner}{(Absent ? " (offline)" : "")}{(Lost ? ", in Lost and Found" : "")}; ledger {LedgerWhere}"
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
            if (!LiveItems.TryGet(netId, out var record)) throw new Exception("no record for " + netId);
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
            record.Owner = owner;
            if (!ServerLedger.Ledger.IsLive(record.PersistentItemId))
                ServerLedger.Ledger.Register(new BirthRequest
                {
                    Id = record.PersistentItemId,
                    Owner = owner,
                    Prefab = record.PrefabName,
                    Where = ServerLedger.WhereOf(record),
                    Reason = BirthReason.Granted,
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
            if (!LiveItems.TryGet(netId, out var record)) return info;
            Guid entry = LostAndFoundServer.EntryId(netId);
            info.Exists = true;
            info.Id = record.PersistentItemId.ToString("D");
            info.Owner = record.Owner.ToString("D");
            info.OwnerConnected = PlayerNumbers.IsConnected(record.Owner);
            info.Number = PlayerNumbers.OwnerOf(record.Owner);
            info.Placement = record.Placement.ToString();
            info.PlacementPlayer = PlayerNumbers.Of(record.Holder);
            info.Holder = record.Holder == Guid.Empty ? "" : record.Holder.ToString("D");
            info.LostEntry = entry == Guid.Empty ? "" : entry.ToString("D");
            if (ServerLedger.Ledger.TryGet(record.PersistentItemId, out Multiplayer.Core.Items.Ledger.LedgerEntry ledger))
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
            LiveItems.LiveItem record = LiveItems.GetAll().FirstOrDefault(r => r.PersistentItemId == wanted);
            return record == null ? new OwnerInfo { Id = id } : Read(record.NetId);
        }

        /// <summary>Host: collects the item into its owner's Lost and Found as the host's own code does. Result: whether it went.</summary>
        [Step]
        public static IEnumerator Collect(Step<bool> step, ushort netId, string reason)
        {
            NetworkedItem.TryGet(netId, out NetworkedItem item);
            LostItemReason why = (LostItemReason)Enum.Parse(typeof(LostItemReason), reason);
            step.Result = item != null && LostAndFoundServer.Collect(item, why, "ownership scenario");
            for (int i = 0; i < 30; i++) yield return null;
        }

        /// <summary>Host: how many items in Lost and Found an owner has (by identity), bound or still waiting.</summary>
        [Step]
        public static int LostCount(string owner)
        {
            if (!Guid.TryParse(owner, out Guid who)) return -1;
            int bound = LiveItems.GetAll().Count(r => r.Placement == ItemPlacementKind.LostAndFound && r.Owner == who);
            int waiting = LostAndFoundServer.PendingEntries().Count(p => p.Owner == who);
            return bound + waiting;
        }

        /// <summary>Host: how many players other than the host's own are connected (a killed client
        /// counts until its connection times out).</summary>
        [Step]
        public static int RemotePlayers() =>
            NetworkServer.Instance.ServerPlayers.Count(p => p != NetworkServer.Instance.LocalPlayer && p.PlayerId != 0);

        /// <summary>Host: the ledger's live entries for an owner.</summary>
        [Step]
        public static int LedgerCount(string owner) =>
            Guid.TryParse(owner, out Guid who) ? ServerLedger.Ledger.LiveOwnedBy(who).Count : -1;
    }
}
