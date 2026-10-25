using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A withdrawal into a full inventory (docs/design/items/containers.md, "Withdrawal"; OI-ACC-04).
    /// The server picks the slot, and with none free the item still comes out: the player (WHO) has a
    /// box holding a mug of their own and a mug nobody owns (a row as a load makes one), and fills
    /// their inventory. Their own mug comes out into their Lost and Found (InventoryOverflow); the
    /// unowned one comes out loose at their feet, in no Lost and Found. Both leave the box.
    /// </summary>
    [Scenario("withdraw-full", "A withdrawal into a full inventory: an owned item to its owner's Lost and Found, an unowned one to the player's feet (OI-ACC-04)")]
    [Budget(900)]
    public sealed class WithdrawFull : Scenario<WithdrawFull.Options>
    {
        public sealed class Options
        {
            /// <summary>The container's prefab.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>Who withdraws: client or host.</summary>
            public string Who = "client";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who != "client") throw new HarnessException("WHO=client only: the inventory is filled through the client");
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            List<string> ids = new();
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, o.Who));
            pair.Cleanup(async () =>
            {
                var now = new List<ushort> { box };
                foreach (string id in ids)
                {
                    ushort netId = await pair.Host.Run(() => Owned.NetIdOf(id));
                    if (netId != 0) now.Add(netId);
                }
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, now.ToArray()), 120);
            });

            // A mug of the player's own into the box, and a row nobody owns beside it.
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", o.Who));
            await pair.Host.Run(() => Ownership.Give(mug, o.Who));
            string ownedId = await pair.Host.Run(() => Owned.IdOf(mug));
            ids.Add(ownedId);
            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, box, mug), 120), "the deposit of the player's own mug");
            string unownedId = await pair.Host.Run(() => Boxes.PutUnowned(box, "Mug"));
            ids.Add(unownedId);
            int ownedSlot = await pair.Host.Run(() => Boxes.StoredSlot(ownedId));
            int unownedSlot = await pair.Host.Run(() => Boxes.StoredSlot(unownedId));
            Report.Note($"the box: the player's mug in slot {ownedSlot}, the unowned one in slot {unownedSlot}");
            Require.That(ownedSlot >= 0 && unownedSlot >= 0, "both mugs are in the box");

            // Every slot of the inventory taken, the box down first: held, it takes the selected
            // hotbar slot, which the fill's first pickup frees.
            await pair.Client.Run(() => Items.Drop(), 10);
            int taken = (await pair.Client.Run(() => LeaveFullInventory.InGame.Occupied())).Length;
            await LeaveFullInventory.Fill(pair, ids, LeaveFullInventory.Capacity - taken, false);
            Require.Zero(LeaveFullInventory.Capacity - (await pair.Client.Run(() => LeaveFullInventory.InGame.Occupied())).Length, "free slots in the player's inventory");

            // Their own mug: out, into their Lost and Found.
            string first = await pair.Client.Run<string>(s => Boxes.Withdraw(s, box, ownedSlot), 120);
            Report.Note("the player's own mug: " + first);
            Expect.That(first.StartsWith(Boxes.Accepted), "the withdrawal of the player's own mug into a full inventory", first);
            await Task.Delay(2000);
            OwnerInfo owned = await pair.Host.Run(() => Ownership.ById(ownedId));
            Report.Note("the player's own mug after: " + owned);
            Expect.Equal("LostAndFound", owned.Placement, "the player's own mug, withdrawn with no slot free");
            Expect.Equal(client, owned.Owner, "its owner");
            Expect.Equal("InventoryOverflow", await pair.Host.Run(() => SaveHomes.InGame.LostReason(ownedId)), "why it is in Lost and Found");
            Expect.Equal(-1, await pair.Host.Run(() => Boxes.StoredSlot(ownedId)), "the player's own mug's slot in the box");

            // The unowned mug: out, loose at their feet.
            string second = await pair.Client.Run<string>(s => Boxes.Withdraw(s, box, unownedSlot), 120);
            Report.Note("the unowned mug: " + second);
            Expect.That(second.StartsWith(Boxes.Accepted), "the withdrawal of the unowned mug into a full inventory", second);
            await Task.Delay(2000);
            OwnerInfo unowned = await pair.Host.Run(() => Ownership.ById(unownedId));
            Report.Note("the unowned mug after: " + unowned);
            Expect.Equal("World", unowned.Placement, "the unowned mug, withdrawn with no slot free");
            Expect.That(!unowned.Lost, "the unowned mug is in no Lost and Found");
            float away = await pair.Host.Run(() => InGame.FromPlayer(unownedId, client));
            Report.Note($"the unowned mug is {away:F1} m from the player");
            Expect.That(away >= 0f && away < 3f,"the unowned mug is at the player's feet", $"{away:F1} m");
            Expect.Equal(-1, await pair.Host.Run(() => Boxes.StoredSlot(unownedId)), "the unowned mug's slot in the box");
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph after both withdrawals");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: how far the item is from the player, in metres (-1 when either is gone).</summary>
            [Step]
            public static float FromPlayer(string id, string player)
            {
                Guid wanted = Guid.Parse(id), who = Guid.Parse(player);
                LiveItems.LiveItem record = LiveItems.GetAll().FirstOrDefault(r => r.PersistentItemId == wanted);
                ServerPlayer at = NetworkServer.Instance?.ServerPlayers.FirstOrDefault(p => p.Guid == who);
                return record == null || at == null ? -1f : UnityEngine.Vector3.Distance(record.Position, at.AbsoluteWorldPosition);
            }
        }
    }
}
