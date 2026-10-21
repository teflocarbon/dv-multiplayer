using System;
using System.Collections;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Shared.Items;
using UnityEngine;
using CUI = Multiplayer.Client.Items.ColdContainerUiIntegration;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A container takes only its depositor's own items (owned-items.md, PR 1c: the deposit rule is
    /// ItemAccess's now). The client holds its own toolbox and someone else's mug: a player who isn't
    /// here (OWNER=absent) or the host's own player (OWNER=host, a player host only). Its deposit must
    /// be refused with "deposit-item-not-owned", and the mug must stay in the client's inventory,
    /// still its owner's. Then the client's own mug deposits as before (the control).
    /// </summary>
    [Scenario("deposit-refused", "The client can't deposit someone else's mug into its toolbox (deposit-item-not-owned); the mug stays in its inventory, its owner's; its own mug deposits")]
    public sealed class DepositRefused : Scenario<DepositRefused.Options>
    {
        public sealed class Options
        {
            /// <summary>Whose the mug is: absent (a player who isn't here) or host (the host's own player).</summary>
            public string Owner = "absent";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Owner == "host" && pair.Dedicated) throw new HarnessException("OWNER=host needs a player host: a dedicated host has no player to own the mug");
            await Task.Delay(5000);   // a just-joined client is still receiving its restored items
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "ItemContainerToolbox", "client"));
            ushort theirs = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            OwnerInfo given = await pair.Host.Run(() => Ownership.Give(theirs, o.Owner));
            ushort mine = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            Report.Note($"toolbox {box}; {o.Owner}'s mug {given}; the client's own mug {mine}");

            Deposit refused = await pair.Client.Run<Deposit>(s => InGame.TryDeposit(s, box, theirs), 120);
            Report.Note($"someone else's mug: {refused}");
            Expect.That(!refused.Accepted, "the deposit of someone else's mug is refused");
            Expect.Equal("deposit-item-not-owned", refused.Reason, "why it was refused");
            Expect.That(refused.InInventory, "the refused mug is still in the client's inventory");
            await Task.Delay(1000);
            OwnerInfo after = await pair.Host.Run(() => Ownership.Read(theirs));
            Expect.Equal(given.Owner, after.Owner, "the refused mug's owner");
            Expect.Equal("PlayerInventory", after.Placement, "the refused mug's placement on the host");
            Expect.That(after.Exists && !after.Lost, "the refused mug exists and isn't in Lost and Found", after.ToString());

            Deposit own = await pair.Client.Run<Deposit>(s => InGame.TryDeposit(s, box, mine), 120);
            Report.Note($"the client's own mug: {own}");
            Expect.That(own.Accepted, "the deposit of the client's own mug is accepted", own.Reason);
            await pair.Client.Run(() => Owned.DropFromInventory(theirs), 60);
        }

        public sealed class Deposit
        {
            public bool Accepted, InInventory;
            public string Reason = "";

            public override string ToString() => (Accepted ? "accepted" : "refused " + Reason) + (InInventory ? ", in the inventory" : ", not in the inventory");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Puts the item in the inventory, picks up the container, opens it and asks to deposit
            /// the item, then closes the container and puts it down. Result: the server's answer and
            /// whether the item is still in the inventory.
            /// </summary>
            [Step]
            public static IEnumerator TryDeposit(Step<Deposit> step, ushort boxId, ushort itemId)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                NetworkedItem Find(ushort id) => NetworkedItem.TryGet(id, out NetworkedItem n) ? n : null;
                NetworkedItem box = null, item = null;
                yield return Wait.Until(() => (box = Find(boxId)) != null && (item = Find(itemId)) != null, 20f, "the container and the item to arrive");
                var open = inv.ItemContainerRegistry.ActiveContainer;   // leftovers from an earlier run
                if (open != null) { open.ToggleContainerAccess(); for (int i = 0; i < 10; i++) yield return null; }
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(itemId);
                inv.AddItemToInventory(item.gameObject);
                for (int i = 0; i < 60; i++) yield return null;
                if (inv.IndexOf(item.gameObject) < 0) throw new Exception("the item did not go into the inventory: " + item.GetItemState());
                yield return LocalPlayer.GoTo(box);
                yield return Hands.Pickup(boxId);
                var c = box.GetComponentInChildren<DV.Items.ItemContainer>(true);
                c.ToggleContainerAccess();
                yield return Wait.Until(() => CUI.Active, 10f, "the container view");

                uint seq = CUI.MutationSequence;
                if (c.AddItem(item.gameObject, -1)) throw new Exception("the deposit was not intercepted");
                float t0 = Time.realtimeSinceStartup;
                while (CUI.MutationSequence == seq && Time.realtimeSinceStartup - t0 < 10f) yield return null;
                var r = CUI.LastMutation;
                if (CUI.MutationSequence == seq || r == null) throw new Exception("the deposit got no result");
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = new Deposit
                {
                    Accepted = r.Accepted,
                    Reason = r.RejectionReason ?? "",
                    InInventory = item != null && inv.IndexOf(item.gameObject) >= 0
                };
                c.ToggleContainerAccess();
                for (int i = 0; i < 10; i++) yield return null;
                yield return Hands.Drop();
            }
        }
    }
}
