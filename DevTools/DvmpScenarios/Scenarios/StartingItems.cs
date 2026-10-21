using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Server.Chat;
using Multiplayer.Server.Items;
using Multiplayer.Shared.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The server makes a player's starting items (owned-items.md, PR 4). After every rejoin the
    /// client has each of DV's essentials once, the same ids as before: nothing was made by its own
    /// game, nothing duplicated. A destroyed comms radio is made again at the next rejoin, under a
    /// new id, born Issued, and the old one's ledger entry has ended. A radio in the client's Lost and
    /// Found counts as had: no new one. With the client's starter-kit marker forgotten (as for a
    /// first join), the rejoin brings the seven kit items, born StarterKit, into its inventory, and
    /// the marker is set again.
    /// </summary>
    [Scenario("starting-items", "The server makes the client's essentials and starter kit: each once across rejoins, a destroyed radio made again, one in Lost and Found counts, the kit once per career")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class StartingItems : Scenario<StartingItems.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            string[] essentials = await pair.Host.Run(() => InGame.Essentials());
            Require.That(essentials.Length > 0, "DV lists essentials for this career", string.Join(", ", essentials));
            string radio = essentials.FirstOrDefault(p => p.StartsWith("CommsRadio", StringComparison.Ordinal)) ?? essentials[0];
            Report.Note($"essentials: {string.Join(", ", essentials)}; the one destroyed: {radio}");

            Have before = await pair.Host.Run(() => InGame.Owned(client, essentials));
            Report.Note("before: " + before);
            ExpectOnceEach(before, essentials, "before the first rejoin");

            await pair.Rejoin();
            await Task.Delay(10000);
            Have again = await pair.Host.Run(() => InGame.Owned(client, essentials));
            ExpectOnceEach(again, essentials, "after a rejoin");
            Expect.Equal(before.Ids(), again.Ids(), "the essentials' ids after a rejoin (none made again)");

            // A destroyed radio is made again.
            string oldRadio = again.Id(radio);
            ushort oldNet = await pair.Host.Run(() => Owned.NetIdOf(oldRadio));
            Require.That(oldNet != 0, $"the client's {radio} exists", oldRadio);
            await pair.Client.Run(() => Owned.DropFromInventory(oldNet), 60);
            Require.That(await pair.Host.Run<bool>(s => InGame.Destroy(s, oldNet)), $"the host destroyed the {radio}");
            await pair.Rejoin();
            Have issued = await Until.Value(() => pair.Host.Run(() => InGame.Owned(client, essentials)), h => h.Count(radio) > 0, 30,
                $"a new {radio} made for the client after the rejoin");
            ExpectOnceEach(issued, essentials, "after the radio was destroyed and the client rejoined");
            string newRadio = issued.Id(radio);
            Expect.That(newRadio.Length > 0 && newRadio != oldRadio, $"a new {radio}, under a new id", $"{oldRadio} -> {newRadio}");
            LedgerEntry made = await pair.Host.Run(() => Owned.Entry(newRadio));
            Expect.Equal("Issued", made.Born.Split(' ')[0], $"how the new {radio} was born");
            Expect.Equal("With/Inventory", made.Place + "/" + made.Hold, $"where the new {radio} is");
            LedgerEntry gone = await pair.Host.Run(() => Owned.Entry(oldRadio));
            Expect.That(!gone.Live, $"the destroyed {radio}'s entry has ended", gone.Ended);

            // One in Lost and Found counts as had.
            ushort newNet = await pair.Host.Run(() => Owned.NetIdOf(newRadio));
            await pair.Client.Run(() => Owned.DropFromInventory(newNet), 60);
            Require.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, newNet, "SaveRecovery")), $"the new {radio} collected into the client's Lost and Found");
            await pair.Rejoin();
            await Task.Delay(10000);
            Have lost = await pair.Host.Run(() => InGame.Owned(client, essentials));
            ExpectOnceEach(lost, essentials, "after a rejoin with the radio in Lost and Found");
            Expect.Equal(newRadio, lost.Id(radio), $"the client's one {radio} (the one in Lost and Found)");
            int slot = await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, newNet), 60);
            Report.Note($"retrieved the {radio} into slot {slot}");

            // The starter kit, once per career.
            Require.That(await pair.Host.Run(() => InGame.ForgetStarterKit(client)), "the client had its starter kit");
            await pair.Rejoin();
            await Task.Delay(10000);
            Kit kit = await pair.Host.Run(() => InGame.KitSince(client));
            Report.Note("starter kit: " + string.Join(", ", kit.Items));
            await pair.Host.Run(() => InGame.Track(kit.NetIds));
            Expect.Equal(7, kit.Items.Length, "the starter kit's items made at the rejoin");
            Expect.That(kit.Items.All(i => i.EndsWith("With/Inventory", StringComparison.Ordinal)), "the starter kit is in the client's inventory", string.Join(", ", kit.Items));
            Expect.That(kit.Marked, "the client's starter-kit marker is set again");
            Have after = await pair.Host.Run(() => InGame.Owned(client, essentials));
            ExpectOnceEach(after, essentials, "after the starter kit");
        }

        private static void ExpectOnceEach(Have have, string[] prefabs, string when)
        {
            foreach (string prefab in prefabs)
                Expect.Equal(1, have.Count(prefab), $"the client's live {prefab}s {when}");
        }

        public sealed class Have
        {
            public string[] Prefabs = new string[0], ItemIds = new string[0];

            public int Count(string prefab) => Prefabs.Count(p => p == prefab);
            public string Id(string prefab) => Prefabs.Zip(ItemIds, (p, i) => (p, i)).FirstOrDefault(x => x.p == prefab).i ?? "";
            public string Ids() => string.Join(",", Prefabs.Zip(ItemIds, (p, i) => p + "=" + i).OrderBy(s => s));
            public override string ToString() => string.Join(", ", Prefabs.Zip(ItemIds, (p, i) => p + " " + i.Substring(0, 8)));
        }

        public sealed class Kit
        {
            public string[] Items = new string[0];
            public ushort[] NetIds = new ushort[0];
            public bool Marked;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: DV's essentials for this career, by prefab.</summary>
            [Step]
            public static string[] Essentials() => AdminItemGrants.StartingItemPrefabs().Distinct().ToArray();

            /// <summary>Host: the owner's live ledger entries of these prefabs (a prefab twice is two items).</summary>
            [Step]
            public static Have Owned(string owner, string[] prefabs)
            {
                Guid who = Guid.Parse(owner);
                var live = OwnedItemsServer.Ledger.LiveOwnedBy(who).Where(e => prefabs.Contains(e.Prefab)).OrderBy(e => e.Prefab).ToList();
                return new Have { Prefabs = live.Select(e => e.Prefab).ToArray(), ItemIds = live.Select(e => e.Id.ToString("D")).ToArray() };
            }

            /// <summary>Host: destroys the item, as the game would. Result: whether it's gone 30 frames later.</summary>
            [Step]
            public static IEnumerator Destroy(Step<bool> step, ushort netId)
            {
                if (NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null)
                    UnityEngine.Object.Destroy(item.gameObject);
                for (int i = 0; i < 30; i++) yield return null;
                step.Result = !NetworkedItem.TryGet(netId, out NetworkedItem left) || left == null;
            }

            /// <summary>Host: forgets the owner's starter-kit marker, as for a first join. Result: whether they had it.</summary>
            [Step]
            public static bool ForgetStarterKit(string owner) => OwnedItemsServer.Ledger.ForgetStarterKit(Guid.Parse(owner));

            /// <summary>Host: the owner's live items born StarterKit, where they are, and whether their marker is set.</summary>
            [Step]
            public static Kit KitSince(string owner)
            {
                Guid who = Guid.Parse(owner);
                var kit = OwnedItemsServer.Ledger.LiveOwnedBy(who).Where(e => e.Born.Reason == Multiplayer.Core.Items.OwnedItemBirth.StarterKit).ToList();
                var nets = new List<ushort>();
                foreach (var e in kit)
                {
                    var record = AuthoritativeItemRegistry.GetAll().FirstOrDefault(r => r.PersistentItemId == e.Id);
                    if (record != null) nets.Add(record.NetId);
                }
                return new Kit
                {
                    Items = kit.Select(e => e.Prefab + " " + e.Where.Place + "/" + e.Where.Hold).ToArray(),
                    NetIds = nets.ToArray(),
                    Marked = OwnedItemsServer.Ledger.HasStarterKit(who)
                };
            }

            /// <summary>Host: hands the items to the run's cleanup, which destroys them.</summary>
            [Step]
            public static void Track(ushort[] netIds)
            {
                foreach (ushort id in netIds) Items.Track(id);
            }
        }
    }
}
