using System;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.OwnedList;
using Multiplayer.Server.Session;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A box mid-deposit or mid-withdrawal waits, wherever it is (OI-LIST-18; docs/design/items/owned-list.md,
    /// "The steps and refusals"). The client deposits a mug of its own into its own toolbox and puts the
    /// box down: its ledger home is the world, not a container. A real deposit or withdrawal finishes in a
    /// frame, so the host begins one on the cold graph itself (MODE=withdraw: the mug's withdrawal
    /// prepared, <c>ContainerGraph.PrepareWithdrawal</c>; MODE=deposit: a second mug of the client's
    /// committed into the box and not yet retired, <c>CommitDeposit</c>) and, in the same frame, asks
    /// <c>Retrieval.Retrieve</c> for the box as its owner would: refused <c>container-busy</c>, the box still
    /// where it was. The operation is then taken back (<c>AbortWithdrawal</c>, <c>RevokeDeposit</c>), the
    /// graph is settled, and the same retrieval takes the box to the client's Lost and Found with the mug
    /// still inside; the graph checks whole. HELD=1 (a player host only): the host's own player holds the
    /// client's box, so the retrieval would take it out of their hand. Without the fix the busy check was
    /// asked only of an item whose home is a container, so a box in the world or in a hand was taken
    /// mid-operation: the first retrieval is accepted and the run fails.
    /// </summary>
    [Scenario("retrieve-busy-box", "A box in the world or a hand with a deposit or withdrawal part way is refused container-busy, and retrieved once it settles (OI-LIST-18)")]
    [Budget(300)]
    public sealed class RetrieveBusyBox : Scenario<RetrieveBusyBox.Options>
    {
        public sealed class Options
        {
            /// <summary>The box's prefab.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>What is part way: "withdraw" (the mug's withdrawal) or "deposit" (another mug's deposit).</summary>
            public string Mode = "withdraw";
            /// <summary>The host's own player holds the box (a player host only).</summary>
            public bool Held;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Mode != "withdraw" && o.Mode != "deposit") throw new HarnessException("MODE: withdraw or deposit, not " + o.Mode);
            if (o.Held && pair.Dedicated) throw new HarnessException("HELD=1 needs a player host: a dedicated host has no hands to hold the box");
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, "client"));
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            ushort other = 0;
            if (o.Mode == "deposit")
            {
                other = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
                await pair.Host.Run(() => Ownership.Give(other, "client"));
            }
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));
            pair.Cleanup(async () =>
            {
                ushort now = await pair.Host.Run(() => Owned.NetIdOf(mugId));
                ushort[] left = new[] { box, now, other }.Where(id => id != 0).Distinct().ToArray();
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, left), 120);
            });

            DepositRefused.Deposit deposit = await pair.Client.Run<DepositRefused.Deposit>(s => DepositRefused.InGame.TryDeposit(s, box, mug), 120);
            Require.That(deposit.Accepted, "the mug deposited into the client's toolbox", deposit.ToString());
            await Task.Delay(2000);
            string boxId = await pair.Host.Run(() => Owned.IdOf(box));
            Require.Equal(client, (await pair.Host.Run(() => Ownership.ById(boxId))).Owner, "the box's owner (the client)");
            if (o.Held)
            {
                await pair.Host.Run(() => Items.Pickup(box), 60);
                Require.Equal(box, await pair.Host.Run(() => Items.Held()), "the box in the host's hand");
            }
            OwnerInfo where = await pair.Host.Run(() => Ownership.ById(boxId));
            Report.Note("the box: " + where);
            Require.That(where.Exists && !where.Lost, "the box is out, in no Lost and Found", where.ToString());
            Require.That((await pair.Host.Run(() => Owned.Entry(boxId))).Place != "Container", "the box's ledger home isn't a container (the case the fix covers)");
            Require.That(await pair.Host.Run(() => Boxes.StoredSlot(mugId)) >= 0, "the mug is in the box");
            string inBox = await pair.Host.Run(() => Boxes.StoredIn(mugId));

            Busy busy = await pair.Host.Run(() => InGame.RetrieveWhileBusy(client, boxId, box, o.Mode, other));
            Report.Note($"{o.Mode} part way: {busy}");
            Require.That(busy.Begun, $"the {o.Mode} begun on the graph", busy.Begin);
            Require.That(!busy.SettledWhileBusy, "the box isn't settled while it is part way");
            Expect.Equal("container-busy", busy.Refusal, "the retrieval of the box part way: refused, and why");
            Require.That(busy.SettledAfter, $"the box is settled once the {o.Mode} is taken back");
            OwnerInfo during = await pair.Host.Run(() => Ownership.ById(boxId));
            Expect.That(!during.Lost, "the box isn't in Lost and Found after the refused retrieval", during.ToString());
            if (o.Held)
                Expect.Equal(box, await pair.Host.Run(() => Items.Held()), "the host still holds the box after the refused retrieval");

            string again = await pair.Host.Run(() => InGame.Retrieve(client, boxId));
            Expect.Equal("accepted", again, "the retrieval once the box has settled");
            await Task.Delay(1000);
            OwnerInfo after = await pair.Host.Run(() => Ownership.ById(boxId));
            Report.Note("the box after: " + after);
            Expect.That(after.Lost && after.Owner == client, "the box is in the client's Lost and Found", after.ToString());
            Expect.Equal(inBox, await pair.Host.Run(() => Boxes.StoredIn(mugId)), "the box the mug is in after the retrieval (the same)");
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph after the retrieval");
            if (o.Held)
                Expect.Equal((ushort)0, await pair.Host.Run(() => Items.Held()), "what the host holds after the retrieval");
        }

        public sealed class Busy
        {
            public bool Begun, SettledWhileBusy, SettledAfter;
            public string Begin = "", Refusal = "", Undo = "";

            public override string ToString() =>
                $"begun {Begun} ({Begin}), settled while busy {SettledWhileBusy}, retrieval {(Refusal.Length == 0 ? "accepted" : "refused " + Refusal)}, " +
                $"taken back: {Undo}, settled after {SettledAfter}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static ServerPlayer Player(string identity) =>
                NetworkServer.Instance.ServerPlayers.First(p => p.Guid.ToString("D") == identity);

            /// <summary>
            /// Host: in one frame, begins the operation on the box's container (<paramref name="mode"/>
            /// "withdraw": its first row's withdrawal; "deposit": the item <paramref name="other"/>
            /// committed into a free slot), asks the owner's retrieval of the box, and takes the
            /// operation back. Result: what each step did.
            /// </summary>
            [Step]
            public static Busy RetrieveWhileBusy(string owner, string boxId, ushort boxNetId, string mode, ushort other)
            {
                ContainerGraph graph = ContainerServer.Graph;
                Guid box = Guid.Parse(boxId);
                if (!ContainerServer.TryResolveShell(boxNetId, out Guid container) || !graph.TryGetContainer(container, out ContainerRecord record))
                    throw new Exception("the box has no container");
                var result = new Busy();
                Guid operation = Guid.NewGuid();
                Guid deposited = Guid.Empty;
                if (mode == "withdraw")
                {
                    StoredItemRecord row = graph.RowsIn(container).OrderBy(r => r.Slot).FirstOrDefault()
                        ?? throw new Exception("the box is empty");
                    ContainerOperationResult begun = graph.PrepareWithdrawal(new WithdrawalCommand
                    {
                        OperationId = operation,
                        ActorIdentity = owner,
                        SourceContainerId = container,
                        ExpectedSourceRevision = record.Revision,
                        SourceSlot = row.Slot
                    }, out _);
                    result.Begun = begun.Accepted;
                    result.Begin = $"withdrawal of slot {row.Slot}: {(begun.Accepted ? begun.ItemState.ToString() : begun.Reason)}";
                }
                else
                {
                    var used = graph.RowsIn(container).Select(r => r.Slot).ToList();
                    int slot = Enumerable.Range(0, record.Capacity).Where(s => !used.Contains(s)).DefaultIfEmpty(-1).First();
                    if (slot < 0) throw new Exception("the box is full");
                    deposited = Guid.Parse(Owned.IdOf(other));
                    ContainerOperationResult begun = graph.CommitDeposit(new DepositCommand
                    {
                        OperationId = operation,
                        ActorIdentity = owner,
                        DestinationContainerId = container,
                        ExpectedDestinationRevision = record.Revision,
                        DestinationSlot = slot,
                        PhysicalNetId = other,
                        Item = new StoredItemRecord { PersistentItemId = deposited, PrefabName = "Mug", DisplayName = "Mug" }
                    });
                    result.Begun = begun.Accepted;
                    result.Begin = $"deposit into slot {slot}: {(begun.Accepted ? begun.ItemState.ToString() : begun.Reason)}";
                }
                if (!result.Begun)
                    return result;
                result.SettledWhileBusy = graph.IsSettled(box);
                try
                {
                    result.Refusal = Retrieval.Retrieve(Player(owner), box, out string reason) ? "" : reason;
                }
                finally
                {
                    ContainerOperationResult undone = mode == "withdraw"
                        ? graph.AbortWithdrawal(operation, "retrieve-busy-box")
                        : graph.RevokeDeposit(operation, deposited, "retrieve-busy-box");
                    result.Undo = undone.Reason;
                    result.SettledAfter = graph.IsSettled(box);
                }
                return result;
            }

            /// <summary>Host: the owner retrieves the item, as their request does. Result: "accepted" or the refusal.</summary>
            [Step]
            public static string Retrieve(string owner, string itemId) =>
                Retrieval.Retrieve(Player(owner), Guid.Parse(itemId), out string reason) ? "accepted" : reason;
        }
    }
}
