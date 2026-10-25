using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A claim the ledger moves reaches its owner's game when the item is data only on the server
    /// (OI-ESS-01; docs/design/items/essentials.md, "Claim slots"; OI-COLD-06). The player has two
    /// essentials in their own slots, each its claim. They put the second (B) on the belt, where it
    /// keeps its claim, and move the first (A) into B's claim slot: the ledger gives A that claim and
    /// moves B's to A's old one (<c>ItemLedger.FollowClaim</c>, a <c>ClaimSlot</c> change). B is in the
    /// player's inventory, so the server keeps it as data with no object, and its owner's stand-in must
    /// show the new claim within a few seconds (<c>PlayerItems.SendClaim</c>). A moved back gives both
    /// claims back, which B's stand-in must show too; B goes back to its slot. WHO=host does it with the
    /// host's own player (a player host only), whose stand-in is told in process. Without the fix
    /// (<c>LiveItems.PushClaimSlotCorrection</c> returning when there is no object) the owner's game
    /// keeps B's old claim and the run fails at the first claim check.
    /// </summary>
    [Scenario("essential-claim-moved", "A claim the ledger moves for a data-only essential reaches its owner's stand-in (OI-ESS-01)")]
    [Budget(240)]
    public sealed class EssentialClaimMoved : Scenario<EssentialClaimMoved.Options>
    {
        public sealed class Options
        {
            /// <summary>Whose essentials: "client", or "host" (the host's own player; needs a player host).</summary>
            public string Who = "client";
            /// <summary>How long the owner's game has to show a moved claim, in seconds.</summary>
            public int Seconds = 5;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            if (host && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = host ? pair.Host : pair.Client;
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            if (!host) await Recall.EssentialToInventory(pair);

            // Two essentials, each in its own claim slot.
            ushort[] essentials = await who.Run(() => InGame.Essentials());
            ushort a = 0, b = 0;
            int claimA = -1, claimB = -1;
            foreach (ushort e in essentials)
            {
                ServerSlot s = await pair.Host.Run(() => Ledger.Where(e));
                Report.Note($"essential {e}: {s}");
                if (s.Slot < 0 || s.Claim != s.Slot || s.LedgerSlot != s.Slot) continue;
                if (b == 0) { b = e; claimB = s.Claim; }
                else if (a == 0) { a = e; claimA = s.Claim; }
            }
            if (a == 0 || b == 0)
                throw new HarnessException($"the {o.Who} needs two essentials in their own claim slots; found {essentials.Length} essential(s) in its inventory");
            Require.That(await pair.Host.Run(() => InGame.DataOnly(b)), $"essential {b} is data only on the server (OI-COLD-06)");
            Report.Note($"A {a} in slot {claimA}, B {b} in slot {claimB}");
            int belt = await who.Run(() => InGame.FreeBeltSlot());
            pair.Cleanup(async () =>
            {
                // Best effort: both back in their own slots.
                if (await who.Run(() => Ledger.LocalSlot(a)) != claimA) await who.Run<bool>(s => InGame.MoveOrAsk(s, a, claimA));
                if (await who.Run(() => Ledger.LocalSlot(b)) != claimB) await who.Run<bool>(s => InGame.MoveOrAsk(s, b, claimB));
            });

            // B on the belt keeps its claim.
            await who.Run<bool>(s => InGame.MoveOrAsk(s, b, belt));
            ServerSlot onBelt = await Until.Value(() => pair.Host.Run(() => Ledger.Where(b)), v => v.Slot == belt, 10, $"B on the belt slot {belt} on the server");
            Require.Equal(claimB, onBelt.Claim, "B's claim while it's on the belt (the ledger)");

            // A into B's claim slot: the ledger moves B's claim to A's old slot.
            await Moved(pair, who, a, b, claimB, claimA, o.Seconds, "A into B's claim slot");
            // A back into its own: B's claim comes back too.
            await Moved(pair, who, a, b, claimA, claimB, o.Seconds, "A back into its own slot");

            await who.Run<bool>(s => InGame.MoveOrAsk(s, b, claimB));
            ServerSlot back = await Until.Value(() => pair.Host.Run(() => Ledger.Where(b)), v => v.Slot == claimB, 10, $"B back in slot {claimB} on the server");
            Expect.Equal(claimB, back.Claim, "B's claim, back in its slot");
        }

        // A moves to slot `to`, which B claims; B's claim must become `bNext` in the ledger and on the owner's game.
        private static async Task Moved(Pair pair, Peer who, ushort a, ushort b, int to, int bNext, int seconds, string what)
        {
            bool dragged = await who.Run<bool>(s => InGame.MoveOrAsk(s, a, to));
            Report.Note($"{what}: {(dragged ? "dragged in the game" : "asked as a MoveSlot request")}");
            ServerSlot sa = await Until.Value(() => pair.Host.Run(() => Ledger.Where(a)), v => v.Slot == to, 10, $"{what}: A in slot {to} on the server");
            Expect.Equal(to, sa.Claim, $"{what}: A's claim (the ledger)");
            ServerSlot sb = await pair.Host.Run(() => Ledger.Where(b));
            Require.Equal(bNext, sb.Claim, $"{what}: B's claim (the ledger)");
            Require.That(await pair.Host.Run(() => InGame.DataOnly(b)), $"{what}: B is still data only on the server");
            int shown = -1;
            for (int i = 0; i < seconds * 4; i++)
            {
                shown = await who.Run(() => InGame.ClaimShown(b));
                if (shown == bNext) break;
                await Task.Delay(250);
            }
            Expect.Equal(bNext, shown, $"{what}: B's claim on the {who.Name}'s stand-in within {seconds} s");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>This game: the player's essentials stored in its inventory off the belt, by NetId.</summary>
            [Step]
            public static ushort[] Essentials() => PlayerInventory.Stored()
                .Where(e => e.netId != 0 && e.slot < Multiplayer.Core.Items.Inventory.InventorySlots.FirstBeltSlot && PlayerInventory.IsEssential(e.go))
                .OrderBy(e => e.slot).Select(e => e.netId).ToArray();

            /// <summary>This game: a free belt slot.</summary>
            [Step]
            public static int FreeBeltSlot()
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                for (int s = Multiplayer.Core.Items.Inventory.InventorySlots.FirstBeltSlot; s < inv.Capacity; s++)
                    if (inv.PeekItemAtSlot(s, true) == null) return s;
                throw new Exception("no free belt slot");
            }

            /// <summary>Host: whether the server keeps the item as data, with no object.</summary>
            [Step]
            public static bool DataOnly(ushort netId) => PlayerItems.IsDataOnly(netId);

            /// <summary>This game: the claim slot its stand-in of the item shows (-2 for no stand-in).</summary>
            [Step]
            public static int ClaimShown(ushort netId) =>
                InventoryStandIn.TryGet(netId, out InventoryStandIn standIn) ? standIn.ClaimSlot : -2;

            /// <summary>
            /// This game: moves the item to the slot as dragging it there does, or, if the game won't,
            /// asks the server to (a MoveSlot request, as the drag sends). Result: whether the game moved it.
            /// </summary>
            [Step]
            public static IEnumerator MoveOrAsk(Step<bool> step, ushort netId, int slot)
            {
                step.Result = PlayerInventory.MoveTo(netId, slot);
                if (!step.Result)
                {
                    step.Note($"the game didn't move {netId} to slot {slot}: asked the server");
                    ItemClient.Current?.SendItemRequest(new Multiplayer.Shared.Protocol.Data.Items.ItemRequest
                    {
                        ItemNetId = netId,
                        ExpectedRevision = PlayerInventory.Revision(netId),
                        Action = Multiplayer.Shared.Protocol.Data.Items.ItemAction.MoveSlot,
                        Slot = slot
                    });
                }
                yield return new WaitForSeconds(1.5f);
            }
        }
    }
}
