using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using HarmonyLib;
using Multiplayer.Core.Items.Access;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Chat;
using Multiplayer.Server.Items.Essentials;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.OwnedList;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The ledger's rows that need a game rather than a unit test (item-refactor.md, R3):
    /// - an inactive item is still found by the one-home check (OI-LED-10);
    /// - `Ledger.Dump` lists an owner's entries (OI-LED-13);
    /// - a copy of an owned item is re-minted and logged (OI-ID-05) and born `Duplicate` from the
    ///   original (OI-ID-18, OI-LIN-07);
    /// - an admin give is owned by its target, born `Granted` (OI-ID-13, OI-LIN-07);
    /// - someone else's recall and retrieval are refused by `ItemAccess` (OI-ACC-05, OI-LIST-20);
    /// - taking from another player's inventory is refused (OI-ACC-07), and so is sending an item
    ///   there to Lost and Found (OI-LF-08);
    /// - the host's own player has its starting items from the server (OI-START-06);
    /// - with the ledger not loaded, the issuer makes nothing, and its next try runs (OI-START-08);
    /// - the owned list names a hand and another player's inventory (OI-LIST-21);
    /// - an owner retrieves their item from another player's inventory, and Lost and Found records
    ///   why (OI-LIST-12, OI-LF-20).
    /// The parts that need the host's own player are skipped on a dedicated host.
    /// </summary>
    [Scenario("ledger-rows", "The ledger's rows a game proves: inactive items, the dump, Duplicate and Granted births, refused access, retrieval from another player")]
    [Budget(360)]
    public sealed class LedgerRows : Scenario<LedgerRows.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(mug, "client"));
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));

            // OI-LED-10: inactive is not gone.
            Expect.Empty(await pair.Host.Run(() => InGame.CheckWhileInactive(mug)), "the one-home check with the mug's object inactive");

            // OI-LED-13: the dump.
            string dump = await pair.Host.Run(() => Ledger.Dump(client));
            Expect.That(dump.Contains(mugId), "Ledger.Dump lists the client's mug", dump.Length > 300 ? dump.Substring(0, 300) : dump);

            // OI-ID-18: a copy is born Duplicate, from the original.
            long cursor = await pair.Host.LogCursor();
            ushort clone = await pair.Host.Run<ushort>(s => Owned.Clone(s, mug));
            await pair.Host.Run(() => Ownership.Give(clone, "client"));
            string cloneId = await pair.Host.Run(() => Owned.IdOf(clone));
            LedgerEntry copy = await pair.Host.Run(() => Owned.Entry(cloneId));
            Report.Note($"the copy: {copy}");
            Expect.That(cloneId.Length > 0 && cloneId != mugId, "the copy has an id of its own", cloneId);
            Expect.Equal("Duplicate", copy.Born, "the copy's birth");
            Expect.Equal(mugId, await pair.Host.Run(() => InGame.BornFrom(cloneId)), "the copy's birth names the original");
            LogLine[] minted = (await pair.Host.LogsSince(cursor, "error"))
                .Where(l => l.Message.Contains("item.duplicate-persistent-id") && l.Message.Contains(mugId)).ToArray();
            Expect.Equal(1, minted.Length, "the copy's re-mint is logged once (item.duplicate-persistent-id)");

            // OI-ID-13: an admin give is the target's, born Granted.
            string granted = await pair.Host.Run<string>(s => InGame.Grant(s, client, "Lantern"), 60);
            LedgerEntry lantern = await pair.Host.Run(() => Owned.Entry(granted));
            Report.Note($"the granted lantern: {lantern}");
            Expect.Equal(client, lantern.Owner, "the granted lantern's owner");
            Expect.Equal("Granted", lantern.Born, "the granted lantern's birth");

            // OI-START-08: the issuer waits for the ledger; if it never loads, nothing is made, and the
            // next join's try reads the ledger again.
            pair.Cleanup(() => pair.Host.Run(() => InGame.SetLoaded(true)));
            await pair.Host.Run(() => InGame.SetLoaded(false));
            long issueCursor = await pair.Host.LogCursor();
            Expect.Zero(await pair.Host.Run<int>(s => InGame.IssueNow(s, client, 75f), 90),
                "items the issuer made while the ledger wasn't loaded");
            LogLine[] gaveUp = (await pair.Host.LogsSince(issueCursor, "warning"))
                .Where(l => l.Message.Contains("didn't load") && l.Message.Contains("nothing made")).ToArray();
            Expect.Equal(1, gaveUp.Length, "the issuer logged that it made nothing");
            await pair.Host.Run(() => InGame.SetLoaded(true));
            issueCursor = await pair.Host.LogCursor();
            Expect.Zero(await pair.Host.Run<int>(s => InGame.IssueNow(s, client, 20f), 30),
                "items the next try made for a client that has them all");
            Expect.That((await pair.Host.LogsSince(issueCursor)).Any(l => l.Message.Contains("Starting items: ") && l.Message.Contains(" has a ")),
                "the next try read the ledger");

            if (pair.Dedicated)
            {
                Report.Note("a dedicated host has no player of its own: the access and retrieval parts need one");
                return;
            }

            // OI-ACC-05, OI-LIST-20: someone else's recall (of an essential: only those recall) and retrieval.
            Expect.Equal(ItemAccessRefusal, await pair.Host.Run<string>(s => InGame.RecallAsHost(s, client), 30),
                "the host's recall of the client's essential");
            Expect.Equal("not-your-item", await pair.Host.Run(() => InGame.RetrieveAsHost(mugId)), "the host's retrieval of the client's mug");

            // OI-ACC-07: taking from another player's inventory.
            ushort lanternNet = await pair.Host.Run(() => Owned.NetIdOf(granted));
            await Task.Delay(3000);   // settled in the client's inventory: data only on the server
            Expect.Equal("take-from-player", await pair.Host.Run<string>(s => InGame.TakeAsHost(s, lanternNet), 30),
                "the host taking the client's lantern from its inventory");
            await Task.Delay(3000);   // data only again
            Expect.Equal("item-is-with-a-player", await pair.Host.Run(() => InGame.SendHome(granted)),
                "sending the lantern in the client's inventory to Lost and Found");

            // OI-START-06: the host's own player's starting items, from the server.
            Expect.That(await pair.Host.Run(() => InGame.HostStarting()) > 0, "the host's player has starting items in the ledger");

            // OI-LIST-12, OI-LF-20: the client retrieves its item from the host's inventory.
            ushort lent = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "host"));
            await pair.Host.Run(() => Ownership.Give(lent, "client"));
            await pair.Host.Run<int>(s => Items.Stash(s, lent, false), 60);
            string lentId = await pair.Host.Run(() => Owned.IdOf(lent));
            // OI-LIST-21: the owner's list names another player's inventory.
            string lentRow = await pair.Host.Run(() => InGame.RowWhere(client, lentId));
            Expect.That(lentRow.StartsWith("in ") && lentRow.EndsWith("'s inventory") && !lentRow.Contains("your"),
                "the client's row for its mug in the host's inventory names the host", lentRow);
            uint handle = await pair.Host.Run(() => OwnedRetrieve.InGame.HandleOf(client, lentId));
            OwnedRetrieve.Result retrieved = await pair.Client.Run<OwnedRetrieve.Result>(s => OwnedRetrieve.InGame.Retrieve(s, handle), 30);
            Expect.That(retrieved.Accepted, "the client's retrieval of its mug from the host's inventory", retrieved.Reason);
            OwnerInfo where = await pair.Host.Run(() => Ownership.ById(lentId));
            Report.Note($"the retrieved mug: {where}");
            Expect.That(where.Lost, "the retrieved mug is in the client's Lost and Found", where.ToString());
            Expect.Equal("RetrievedByOwner", await pair.Host.Run(() => InGame.LostReason(client, lentId)), "why Lost and Found has it");

            // OI-LIST-21: and the owner's own hand.
            await pair.Client.Run<int>(s => Items.Stash(s, mug, true), 60);
            Expect.Equal("in your hand", await pair.Host.Run(() => InGame.RowWhere(client, mugId)), "the client's row for the mug in its hand");
        }

        private const string ItemAccessRefusal = "requester-not-persistent-owner";

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the one-home check's breaks with the item's object inactive, then active again.</summary>
            [Step]
            public static string[] CheckWhileInactive(ushort netId)
            {
                NetworkedItem item = Items.Find(netId);
                item.gameObject.SetActive(false);
                try { return ServerLedger.CheckNow().ToArray(); }
                finally { item.gameObject.SetActive(true); }
            }

            /// <summary>Host: the id the entry's birth names as its original, or "".</summary>
            [Step]
            public static string BornFrom(string id) =>
                ServerLedger.Ledger.TryGet(Guid.Parse(id), out Multiplayer.Core.Items.Ledger.LedgerEntry e) && e.Born.From != Guid.Empty
                    ? e.Born.From.ToString("D") : "";

            /// <summary>Host: an admin give of the prefab to the player. Result: the new item's id.</summary>
            [Step]
            public static IEnumerator Grant(Step<string> step, string owner, string prefab)
            {
                ServerPlayer player = NetworkServer.Instance.ServerPlayers.First(p => p.Guid.ToString("D") == owner);
                var before = ServerLedger.Ledger.LiveOwnedBy(OwnerRef.Player(player.Guid)).Select(e => e.Id).ToHashSet();
                yield return AdminItemGrants.Spawn(player, new[] { AdminItemGrants.Find(prefab, out _) });
                Guid made = Guid.Empty;
                yield return Wait.Until(() => (made = ServerLedger.Ledger.LiveOwnedBy(OwnerRef.Player(player.Guid))
                    .Where(e => e.Prefab == prefab && !before.Contains(e.Id)).Select(e => e.Id).FirstOrDefault()) != Guid.Empty,
                    15f, "the granted item's ledger entry");
                Items.Track(Owned.NetIdOf(made.ToString("D")));
                step.Result = made.ToString("D");
            }

            /// <summary>Host: the host's own player recalls one of the owner's essentials. Result: the
            /// refusal, or "" if it went.</summary>
            [Step]
            public static IEnumerator RecallAsHost(Step<string> step, string owner)
            {
                Guid who = Guid.Parse(owner);
                var essentials = DV.Globals.G.Items.items.Where(i => i != null && i.IsEssential).Select(i => i.ItemPrefabName).ToHashSet();
                ushort netId = ServerLedger.Ledger.LiveOwnedBy(OwnerRef.Player(who)).Where(e => essentials.Contains(e.Prefab))
                    .Select(e => Owned.NetIdOf(e.Id.ToString("D"))).FirstOrDefault(n => n != 0);
                if (netId == 0) throw new Exception("the owner has no live essential");
                NetworkedItem item = null;
                PlayerItems.WithObject(netId, made => item = made);
                yield return Wait.Until(() => item != null, 10f, "the essential's object on the server");
                LiveItems.TryGet(netId, out LiveItems.LiveItem record);
                step.Result = LiveItems.TryRecall(item, NetworkServer.Instance.LocalPlayer, -1, record.Revision,
                    out _, out string reason) ? "" : reason;
            }

            /// <summary>Host: the host's own player retrieves the item. Result: the refusal, or "" if it went.</summary>
            [Step]
            public static string RetrieveAsHost(string id) =>
                Retrieval.Retrieve(NetworkServer.Instance.LocalPlayer, Guid.Parse(id), out string reason) ? "" : reason;

            /// <summary>Host: the host's own player takes the item into their hand. Result: the refusal, or "" if it went.</summary>
            [Step]
            public static IEnumerator TakeAsHost(Step<string> step, ushort netId)
            {
                NetworkedItem item = null;
                PlayerItems.WithObject(netId, made => item = made);
                yield return Wait.Until(() => item != null, 10f, "the item's object on the server");
                ServerPlayer host = NetworkServer.Instance.LocalPlayer;
                LiveItems.TryGet(netId, out LiveItems.LiveItem record);
                ItemUpdate take = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
                take.ItemState = ItemState.InHand;
                take.PlayerId = host.PlayerId;
                take.PlayerHand = PlayerHand.Right;
                take.AuthorityRevision = record.Revision;
                step.Result = LiveItems.TryApplyTransition(item, take, host, ItemTransitionReason.ClientState, false,
                    out string reason) ? "" : reason;
            }

            /// <summary>Host: as if the load had (or hadn't) run. Nothing loads it again meanwhile.</summary>
            [Step]
            public static void SetLoaded(bool on) => ServerLedger.loaded = on;

            /// <summary>Host: the issuer's pass for the player, as at a join, to its end. Result: how many
            /// ledger entries the player gained.</summary>
            [Step]
            public static IEnumerator IssueNow(Step<int> step, string owner, float seconds)
            {
                ServerPlayer player = NetworkServer.Instance.ServerPlayers.First(p => p.Guid.ToString("D") == owner);
                int before = ServerLedger.Ledger.LiveOwnedBy(OwnerRef.Player(player.Guid)).Count();
                StartingItemsIssuer.OnPlayerLoaded(player);
                float t0 = UnityEngine.Time.realtimeSinceStartup;
                yield return null;
                while (StartingItemsIssuer.issuing.Contains(player.Guid) && UnityEngine.Time.realtimeSinceStartup - t0 < seconds)
                    yield return null;
                if (StartingItemsIssuer.issuing.Contains(player.Guid)) throw new Exception("the issuer's pass didn't end");
                step.Result = ServerLedger.Ledger.LiveOwnedBy(OwnerRef.Player(player.Guid)).Count() - before;
            }

            /// <summary>Host: the item's Lost and Found by id. Result: the refusal, or "" if it went.</summary>
            [Step]
            public static string SendHome(string id) =>
                ServerLedger.SendToLostAndFound(Guid.Parse(id), LostItemReason.SaveRecovery, "ledger-rows", out string refused) ? "" : refused;

            /// <summary>Host: how many of the host's own player's items were born from its starting items.</summary>
            [Step]
            public static int HostStarting() =>
                ServerLedger.Ledger.LiveOwnedBy(OwnerRef.Player(NetworkServer.Instance.LocalPlayer.Guid))
                    .Count(e => e.Born.Reason is BirthReason.StarterKit or BirthReason.Issued);

            /// <summary>Host: where the owner's owned-items row for the item says it is, or "".</summary>
            [Step]
            public static string RowWhere(string owner, string id)
            {
                ServerPlayer player = NetworkServer.Instance.ServerPlayers.First(p => p.Guid.ToString("D") == owner);
                return OwnedItemRows.For(player).FirstOrDefault(r => r.Id == Guid.Parse(id))?.Where ?? "";
            }

            /// <summary>Host: the reason the owner's Lost and Found row for the item records, or "".</summary>
            [Step]
            public static string LostReason(string owner, string id)
            {
                ServerPlayer player = NetworkServer.Instance.ServerPlayers.First(p => p.Guid.ToString("D") == owner);
                uint handle = Multiplayer.Server.Items.Projection.ItemHandles.Of(Guid.Parse(id));
                return LostAndFoundServer.RowsFor(player).FirstOrDefault(r => r.Handle == handle)?.Where ?? "";
            }
        }
    }
}
