using System.Collections.Generic;
using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A box in a box across a restart (R5, docs/design/items/containers.md): the item goes into a
    /// box that goes into another box, the outer box is left on the ground, and the host saves and
    /// both games restart; the boxes are still nested with the item inside, the graph checks whole,
    /// and the player takes the inner box out and the item out of it. WHO=host: the host's own
    /// player does it (a player host only). Split out of containers so only this one restarts games.
    /// </summary>
    [Scenario("containers-nested", "A box in a box with an item inside, across a save and restart: still nested, and both come out keeping their ids")]
    [Needs(Need.RestartsGames)]
    public sealed class ContainerNestedRestart : Scenario<ContainerNestedRestart.Options>
    {
        public sealed class Options
        {
            /// <summary>The container prefab to prefer, if it takes a box of its own kind.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>The item to store.</summary>
            public string Prefab = "Mug";
            /// <summary>Who does it: client or host.</summary>
            public string Who = "client";
        }

        // The item in the inner box, the inner box in the outer one, which is left on the
        // ground; after a save and restart both are where they were, and come out again.
        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            Peer who = o.Who == "host" ? pair.Host : pair.Client;
            string prefab = await pair.Host.Run<string>(s => Boxes.SelfNesting(s, o.Container), 120);
            if (prefab.Length == 0) throw new HarnessException("no container prefab takes a box of its own kind");
            ushort outer = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, o.Who));
            ushort inner = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, o.Who));
            ushort item = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, o.Who));
            string outerId = await pair.Host.Run(() => Owned.IdOf(outer));
            string innerId = await pair.Host.Run(() => Owned.IdOf(inner));
            string itemId = await pair.Host.Run(() => Owned.IdOf(item));
            Report.Note($"boxes {prefab}: outer {outer} ({outerId}), inner {inner} ({innerId}); {o.Prefab} {item} ({itemId})");
            var after = new List<ushort>();
            pair.Cleanup(async () =>
            {
                ushort[] left = await pair.Host.Run(() => Items.Existing(after.ToArray()));
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, left), 120);
            });

            Require.Equal(Boxes.Accepted, await who.Run<string>(s => Boxes.Deposit(s, inner, item), 120), "the item into the inner box");
            Require.Equal(Boxes.Accepted, await who.Run<string>(s => Boxes.Deposit(s, outer, inner), 120), "the inner box into the outer one");
            await who.Run(() => Items.Drop(), 10);
            Expect.Equal(outerId, await pair.Host.Run(() => Boxes.StoredIn(innerId)), "the box the inner box is in, before the restart");
            Expect.Equal(innerId, await pair.Host.Run(() => Boxes.StoredIn(itemId)), "the box the item is in, before the restart");
            int innerSlot = await pair.Host.Run(() => Boxes.StoredSlot(innerId));
            int itemSlot = await pair.Host.Run(() => Boxes.StoredSlot(itemId));

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart();
            await Task.Delay(12000);   // the client receives its items

            ushort outerNow = await pair.Host.Run(() => Owned.NetIdOf(outerId));
            Expect.That(outerNow != 0, "the outer box is in the world after the restart");
            if (outerNow != 0) after.Add(outerNow);
            Expect.Equal(outerId, await pair.Host.Run(() => Boxes.StoredIn(innerId)), "the box the inner box is in, after the restart");
            Expect.Equal(innerSlot, await pair.Host.Run(() => Boxes.StoredSlot(innerId)), "the inner box's slot, after the restart");
            Expect.Equal(innerId, await pair.Host.Run(() => Boxes.StoredIn(itemId)), "the box the item is in, after the restart");
            Expect.Equal(itemSlot, await pair.Host.Run(() => Boxes.StoredSlot(itemId)), "the item's slot, after the restart");
            Expect.Equal("Container", (await pair.Host.Run(() => Owned.Entry(innerId))).Place, "the inner box's ledger home, after the restart");
            Expect.Equal("Container", (await pair.Host.Run(() => Owned.Entry(itemId))).Place, "the item's ledger home, after the restart");
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph after the restart");
            if (outerNow == 0) return;

            string innerOut = await who.Run<string>(s => Boxes.Withdraw(s, outerNow, innerSlot), 120);
            Report.Note("the inner box out: " + innerOut);
            Require.That(innerOut.StartsWith(Boxes.Accepted), "the inner box out of the outer one", innerOut);
            ushort innerNow = ushort.Parse(innerOut.Substring(Boxes.Accepted.Length + 1));
            after.Add(innerNow);
            Expect.Equal(innerId, await pair.Host.Run(() => Owned.IdOf(innerNow)), "the inner box taken out keeps its id");
            string itemOut = await who.Run<string>(s => Boxes.Withdraw(s, innerNow, itemSlot), 120);
            Report.Note("the item out: " + itemOut);
            Require.That(itemOut.StartsWith(Boxes.Accepted), "the item out of the inner box", itemOut);
            ushort itemNow = ushort.Parse(itemOut.Substring(Boxes.Accepted.Length + 1));
            after.Add(itemNow);
            Expect.Equal(itemId, await pair.Host.Run(() => Owned.IdOf(itemNow)), "the item taken out keeps its id");
        }
    }
}
