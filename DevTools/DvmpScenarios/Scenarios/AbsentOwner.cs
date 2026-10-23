using System.Collections;
using System.Threading.Tasks;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Shared.Items.LiveState;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// An offline owner's item stays theirs (owned-items.md, PR 2a; item-refactor.md, "Identity only"):
    /// an offline owner is an identity with no session. The host gives a personal mug to a player who
    /// isn't here (a made-up identity, as owned-items does). The client (or the host's own player,
    /// WHO=host) picks it up and drops it: the owner must still be the absent player's identity, not
    /// the taker (before #304, an offline owner was nobody and the first taker became the owner). Then the host puts the mug in the
    /// absent player's Lost and Found, the games save and restart, and the entry must be bound to the
    /// mug at load although its owner never joins (before, it waited for them).
    /// </summary>
    [Scenario("absent-owner", "An offline owner's item isn't taken over by the player who picks it up, and their Lost and Found entry binds at load without them")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class AbsentOwner : Scenario<AbsentOwner.Options>
    {
        public sealed class Options
        {
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
            /// <summary>Who picks the mug up: client or host (the host's own player).</summary>
            public string Who = "client";
            /// <summary>The item's prefab.</summary>
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host: a dedicated host has no player to pick the item up");
            Peer taker = o.Who == "host" ? pair.Host : pair.Client;
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, o.Who));
            Absent absent = await pair.Host.Run(() => InGame.GiveToAbsent(mug));
            Report.Note($"mug {mug} ({absent.Id}) belongs to {absent.Owner}, who isn't here (connected: {absent.OwnerConnected}, wire number {absent.Number})");
            Require.That(absent.Owner.Length > 0 && !absent.OwnerConnected, "the mug's owner is an identity with no session", absent.Owner);

            await taker.Run(() => LostAndFound.InGame.Claim(mug), 60);
            Absent after = await pair.Host.Run(() => InGame.Read(mug));
            Report.Note($"after the {o.Who} picked it up and dropped it: owner {after.Owner}, connected {after.OwnerConnected}");
            Expect.Equal(absent.Owner, after.Owner, $"the owner after the {o.Who} took it (the absent player)");
            Expect.That(!after.OwnerConnected, $"the owner after the {o.Who} took it has no session");
            LedgerEntry entry = await pair.Host.Run(() => Owned.Entry(absent.Id));
            Expect.Equal(absent.Owner, entry.Owner, "the ledger's owner (the absent player)");

            Expect.That(await pair.Host.Run<bool>(s => InGame.Collect(s, mug)), "the host collected the mug into the absent player's Lost and Found");
            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(15000);
            ushort now = await pair.Host.Run(() => Owned.NetIdOf(absent.Id));
            if (!Expect.That(now != 0, "after the restart the mug is there, with its id", absent.Id))
                return;
            Items.Track(now);
            Absent bound = await pair.Host.Run(() => InGame.Read(now));
            Report.Note($"after the restart: owner {bound.Owner}, connected {bound.OwnerConnected}, Lost and Found entry {bound.LostEntry}");
            Expect.Equal(absent.Id, bound.LostEntry, "the mug's Lost and Found entry, bound at load without its owner");
            Expect.Equal(absent.Owner, bound.Owner, "the owner after the restart");
            Expect.That(!bound.OwnerConnected, "the owner after the restart has no session");
        }

        public sealed class Absent
        {
            public string Id = "", Owner = "", LostEntry = "";
            /// <summary>Whether the owner's identity has a session now.</summary>
            public bool OwnerConnected;
            /// <summary>The owner number sent on the wire (until R2), for the report.</summary>
            public int Number;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: makes the item a player's who isn't here (a new identity). Result: its id and owner.</summary>
            [Step]
            public static Absent GiveToAbsent(ushort netId)
            {
                LiveItems.TryGet(netId, out var record);
                record.Owner = System.Guid.NewGuid();
                // The ledger follows the records where they commit; this one is made by hand.
                ServerLedger.Ledger.Register(new BirthRequest
                {
                    Id = record.PersistentItemId,
                    Owner = record.Owner,
                    Prefab = record.PrefabName,
                    Where = ServerLedger.WhereOf(record),
                    Reason = BirthReason.Granted,
                    Detail = "absent-owner scenario"
                });
                return Read(netId);
            }

            /// <summary>Host: the item's id, owner identity and whether it has a session, and its Lost and Found entry's id.</summary>
            [Step]
            public static Absent Read(ushort netId)
            {
                if (!LiveItems.TryGet(netId, out var record)) return new Absent();
                System.Guid entry = LostAndFoundServer.EntryId(netId);
                return new Absent
                {
                    Id = record.PersistentItemId.ToString("D"),
                    Owner = record.Owner.ToString("D"),
                    OwnerConnected = PlayerNumbers.IsConnected(record.Owner),
                    Number = OwnerNumbers.Of(record.Owner),
                    LostEntry = entry == System.Guid.Empty ? "" : entry.ToString("D")
                };
            }

            /// <summary>Host: collects the item into its owner's Lost and Found. Result: whether it went.</summary>
            [Step]
            public static IEnumerator Collect(Step<bool> step, ushort netId)
            {
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                step.Result = item != null && LostAndFoundServer.Collect(item, LostItemReason.SaveRecovery, "absent-owner scenario");
                for (int i = 0; i < 30; i++) yield return null;
            }
        }
    }
}
