using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Shared.Items.LiveState;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Lost and Found holds one entry per item id (OI-ID-06; docs/design/items/lost-and-found.md). No
    /// game path can bring a second item with an id Lost and Found already holds: the ledger gives a
    /// copy a new id as it registers (OI-ID-05). So the run puts the duplicate straight in front of
    /// Lost and Found: a mug of the client's is collected, then a second mug's record is given the
    /// first's id and collected too. Lost and Found must refuse it (`DuplicatePersistentItemId`), say
    /// so in the log, and leave the second mug where it was, with the first's entry unchanged. It does,
    /// but its rollback moves the first mug's ledger home back to the world (LF9): the one-home check
    /// after the run fails.
    /// </summary>
    [KnownBug("LF9")]
    [Scenario("lost-and-found-duplicate-id", "Lost and Found refuses a second item with an id it holds (DuplicatePersistentItemId), logs it, and leaves both items as they were")]
    [Budget(180)]
    public sealed class LostAndFoundDuplicateId : Scenario<LostAndFoundDuplicateId.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort first = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(first, "client"));
            Require.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, first, "SaveRecovery")), "the first mug went to the client's Lost and Found");
            OwnerInfo lost = await pair.Host.Run(() => Ownership.Read(first));
            Report.Note("the first mug: " + lost);
            Require.Equal(lost.Id, lost.LostEntry, "the first mug's Lost and Found entry (its own id)");
            int entries = await pair.Host.Run(() => Ownership.LostCount(client));

            ushort second = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(second, "client"));
            await Task.Delay(2000);   // settled on the ground
            OwnerInfo before = await pair.Host.Run(() => Ownership.Read(second));
            Report.Note("the second mug: " + before);

            long cursor = await pair.Host.LogCursor();
            bool collected = await pair.Host.Run<bool>(s => InGame.CollectAs(s, second, lost.Id));
            Expect.That(!collected, "Lost and Found refused the second mug carrying the first's id");
            LogLine[] errors = await pair.Host.LogsSince(cursor, "error");
            foreach (LogLine line in errors)
                Report.Note("host log: " + line.Message);
            Expect.That(errors.Any(l => l.Message.Contains("was refused: DuplicatePersistentItemId")),
                "the host logged the refusal (DuplicatePersistentItemId)");

            OwnerInfo after = await pair.Host.Run(() => Ownership.Read(second));
            Report.Note("the second mug after: " + after);
            Expect.Equal(before.Id, after.Id, "the second mug's id (its own again)");
            Expect.Equal("World", after.Placement, "where the second mug is");
            Expect.That(!after.Lost, "the second mug has no Lost and Found entry");
            Expect.Equal(before.LedgerWhere, after.LedgerWhere, "the second mug's ledger home");
            OwnerInfo still = await pair.Host.Run(() => Ownership.Read(first));
            Expect.Equal("LostAndFound", still.Placement, "the first mug, still in Lost and Found");
            Expect.Equal(lost.Id, still.LostEntry, "the first mug's entry");
            Expect.Equal("LostAndFound", still.LedgerWhere, "the first mug's ledger home");
            Expect.Equal(entries, await pair.Host.Run(() => Ownership.LostCount(client)), "the client's Lost and Found entries");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: collects the item into Lost and Found with its record carrying
            /// <paramref name="id"/> instead of its own, as an item the ledger's check missed would,
            /// then gives it its own id back. Result: whether Lost and Found took it.
            /// </summary>
            [Step]
            public static IEnumerator CollectAs(Step<bool> step, ushort netId, string id)
            {
                if (!LiveItems.TryGet(netId, out LiveItems.LiveItem record) || !NetworkedItem.TryGet(netId, out NetworkedItem item))
                    throw new Exception("no item " + netId);
                Guid own = record.PersistentItemId;
                record.PersistentItemId = Guid.Parse(id);
                try { step.Result = LostAndFoundServer.Collect(item, LostItemReason.SaveRecovery, "duplicate-id scenario"); }
                finally { record.PersistentItemId = own; }
                for (int i = 0; i < 30; i++) yield return null;
            }
        }
    }
}
