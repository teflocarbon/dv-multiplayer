using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using UnityEngine;
using CUI = Multiplayer.Client.Items.Containers.ContainerUi;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A repeated request does nothing twice (wire.md, "One request, one result"; F21). The client
    /// deposits its mug into its toolbox, then sends the withdrawal twice in one frame and once more
    /// after its answer, all with the same RequestId: one answer for the two at once (or the same answer
    /// twice, if the server finished the first in its frame), the same answer
    /// again for the late repeat, and one mug, in the client's inventory. A new request for the same
    /// row is then refused: the row is gone, so only the repeat was answered from memory.
    /// </summary>
    [Scenario("item-request-repeat", "A withdrawal sent three times with one RequestId withdraws once: one mug in the inventory, the same answer each time, and a new request for the row refused")]
    public sealed class ItemRequestRepeat : Scenario<ItemRequestRepeat.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);   // a just-joined client is still receiving its restored items
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "ItemContainerToolbox", "client"));
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(box, "client"));
            await pair.Host.Run(() => Ownership.Give(mug, "client"));
            string id = await pair.Host.Run(() => Owned.IdOf(mug));
            DepositRefused.Deposit deposit = await pair.Client.Run<DepositRefused.Deposit>(s => DepositRefused.InGame.TryDeposit(s, box, mug), 120);
            Require.That(deposit.Accepted, "the mug deposited into the client's toolbox", deposit.ToString());
            Require.Equal(0, await pair.Host.Run(() => InGame.Live(id)), "live copies of the mug while it's in the toolbox");

            Withdrawal w = await pair.Client.Run<Withdrawal>(s => InGame.WithdrawRepeatedly(s, box), 120);
            Report.Note(w.ToString());
            Expect.That(w.Accepted, "the withdrawal is accepted", w.Detail);
            // The second send in the frame is still pending (one answer for both), or, with a withdrawal
            // the server finishes at once (C5: it makes no object), already answered (the same answer twice).
            Expect.That(w.AnswersToTwo is 1 or 2 && w.TwoSame, "the two sends in one frame get one answer, or the same answer twice",
                $"{w.AnswersToTwo} answer(s), {(w.TwoSame ? "the same" : "different")}");
            Expect.That(w.RepeatSame, "the late repeat gets the same answer (accepted, same revision and item)", w.Repeat);
            Expect.Equal(w.AnswersToTwo + 1, w.Answers, "answers in all (one more for the late repeat)");
            Expect.That(w.InInventory, "the withdrawn mug is in the client's inventory");
            Expect.That(!w.FreshAccepted, "a new request for the same row is refused: the row is gone", w.Fresh);

            await Task.Delay(1000);
            Expect.Equal(1, await pair.Host.Run(() => InGame.Live(id)), "live copies of the mug on the host");
            OwnerInfo after = await pair.Host.Run(() => Ownership.ById(id));
            Report.Note($"host: {after}");
            Expect.Equal("PlayerInventory", after.Placement, "the mug's placement on the host");
            Expect.Equal(w.ItemNetId, after.NetId, "the mug the answer named is the one the host has");
            await pair.Client.Run(() => Owned.DropFromInventory(after.NetId), 60);
        }

        public sealed class Withdrawal
        {
            public bool Accepted, RepeatSame, TwoSame, FreshAccepted, InInventory;
            public int AnswersToTwo, Answers;
            public ushort ItemNetId;
            public uint Revision;
            public string Detail = "", Repeat = "", Fresh = "";

            public override string ToString() => $"withdrawal {(Accepted ? "accepted" : "refused " + Detail)} r{Revision} item {ItemNetId}; " +
                $"{AnswersToTwo} answer(s) to two sends, {Answers} in all; the repeat: {Repeat}; a new request: {Fresh}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: how many live copies of the item with this id there are.</summary>
            [Step]
            public static int Live(string id) =>
                Guid.TryParse(id, out Guid wanted) ? LiveItems.GetAll().Count(r => r.PersistentItemId == wanted) : -1;

            /// <summary>
            /// Client: picks up its toolbox, opens it, and withdraws the first mug in it with one
            /// request sent twice in a frame and once more after the answer; then asks again with a
            /// new request. Result: the answers.
            /// </summary>
            [Step]
            public static IEnumerator WithdrawRepeatedly(Step<Withdrawal> step, ushort boxId)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                NetworkedItem box = null;
                yield return Wait.Until(() => NetworkedItem.TryGet(boxId, out box) && box != null, 20f, "the toolbox to arrive");
                var open = inv.ItemContainerRegistry.ActiveContainer;
                if (open != null) { open.ToggleContainerAccess(); for (int i = 0; i < 10; i++) yield return null; }
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(box);
                yield return Hands.Pickup(boxId);
                var c = box.GetComponentInChildren<DV.Items.ItemContainer>(true);
                c.ToggleContainerAccess();
                yield return Wait.Until(() => CUI.Active && CUI.ActiveRowCount > 0, 10f, "the toolbox's rows");
                ItemRow row = CUI.LastView.Rows.FirstOrDefault(r => r.PrefabName == "Mug")
                    ?? throw new Exception("no mug in the toolbox's view");

                var answers = new System.Collections.Generic.List<ClientboundItemResultPacket>();
                ItemRequest request = new()
                {
                    Action = ItemAction.Withdraw,
                    ItemHandle = row.Handle,
                    ExpectedRevision = CUI.ActiveRevision,
                    Slot = inv.GetFirstFreeSlot()
                };
                void Got(ClientboundItemResultPacket p) { if (p.RequestId == request.RequestId) answers.Add(ItemAnswers.Copy(p)); }
                Withdrawal w = new();
                ItemClient.Current.ItemResultReceived += Got;
                try
                {
                    // The same request object: its RequestId, given on the first send, goes each time.
                    ItemClient.Current.SendItemRequest(request);
                    ItemClient.Current.SendItemRequest(request);
                    yield return Wait.Until(() => answers.Count > 0, 15f, "the withdrawal's answer");
                    for (int i = 0; i < 120; i++) yield return null;
                    w.AnswersToTwo = answers.Count;
                    w.TwoSame = answers.All(a => a.Accepted == answers[0].Accepted && a.Refusal == answers[0].Refusal &&
                        a.Revision == answers[0].Revision && a.ItemNetId == answers[0].ItemNetId);
                    ClientboundItemResultPacket first = answers[0];
                    w.Accepted = first.Accepted;
                    w.Detail = first.Detail ?? "";
                    w.Revision = first.Revision;
                    w.ItemNetId = first.ItemNetId;

                    ItemClient.Current.SendItemRequest(request);
                    float t0 = Time.realtimeSinceStartup;
                    while (answers.Count <= w.AnswersToTwo && Time.realtimeSinceStartup - t0 < 10f) yield return null;
                    for (int i = 0; i < 60; i++) yield return null;
                    w.Answers = answers.Count;
                    ClientboundItemResultPacket repeat = answers.Count > w.AnswersToTwo ? answers[w.AnswersToTwo] : null;
                    w.Repeat = repeat == null ? "no answer" : $"{(repeat.Accepted ? "accepted" : "refused " + repeat.Refusal)} r{repeat.Revision} item {repeat.ItemNetId}";
                    w.RepeatSame = repeat != null && repeat.Accepted == first.Accepted && repeat.Refusal == first.Refusal &&
                        repeat.Revision == first.Revision && repeat.ItemNetId == first.ItemNetId;
                }
                finally
                {
                    ItemClient.Current.ItemResultReceived -= Got;
                }

                ClientboundItemResultPacket fresh = null;
                uint freshId = 0;
                void GotFresh(ClientboundItemResultPacket p) { if (p.RequestId == freshId) fresh = ItemAnswers.Copy(p); }
                ItemClient.Current.ItemResultReceived += GotFresh;
                try
                {
                    freshId = ItemClient.Current.SendItemRequest(new ItemRequest
                    {
                        Action = ItemAction.Withdraw, ItemHandle = row.Handle, ExpectedRevision = CUI.ActiveRevision, Slot = -1
                    });
                    yield return Wait.Until(() => fresh != null, 15f, "the new request's answer");
                }
                finally
                {
                    ItemClient.Current.ItemResultReceived -= GotFresh;
                }
                w.FreshAccepted = fresh.Accepted;
                w.Fresh = fresh.Accepted ? "accepted" : $"refused {fresh.Refusal} {fresh.Detail}";

                // On a client the withdrawn mug is a stand-in (D14).
                yield return Wait.Until(() => PlayerInventory.SlotObject(w.ItemNetId) != null, 10f, "the withdrawn mug");
                w.InInventory = PlayerInventory.Slot(w.ItemNetId) >= 0;
                step.Result = w;
                c.ToggleContainerAccess();
                for (int i = 0; i < 10; i++) yield return null;
                yield return Hands.Drop();
            }
        }
    }
}
