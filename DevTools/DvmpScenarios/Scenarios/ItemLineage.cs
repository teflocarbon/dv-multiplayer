using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.OwnedList;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Lineage (owned-items.md, PR 7). A server step stands in for a swap vanilla makes as an item is
    /// used up (gadget PRs 4, 5 and 9 make those server-side): the client's mug is replaced by a
    /// lantern made where it was. The lantern is the client's, born Replaced, from the mug, and the
    /// mug's entry has ended with replacedBy the lantern. The client's owned-items list has the
    /// lantern and not the mug. A mug destroyed with no replacement ends DestroyedByGame, replaced
    /// by nothing.
    /// </summary>
    [Scenario("item-lineage", "A used-up item's replacement is its owner's, born Replaced, and the used item's entry ends replacedBy it; a plain destroy ends with no replacement; the list shows only the live one")]
    [Budget(300)]
    public sealed class ItemLineage : Scenario<ItemLineage.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(mug), 60);
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));

            Lineage swap = await pair.Host.Run<Lineage>(s => InGame.ReplaceWith(s, mug, "Lantern"));
            Report.Note($"mug {mugId} replaced by {swap.ReplacementId}");
            Require.That(swap.Recorded, "the server recorded the replacement");
            Items.Track(swap.ReplacementNetId);
            Lineage after = await pair.Host.Run(() => InGame.Read(mugId, swap.ReplacementId));
            Expect.That(!after.UsedLive, "the mug's entry has ended");
            Expect.Equal(swap.ReplacementId, after.UsedReplacedBy, "what the mug's entry says replaced it");
            Expect.That(after.ReplacementLive, "the lantern is live");
            Expect.Equal(client, after.ReplacementOwner, "the lantern's owner (the mug's)");
            Expect.Equal("Replaced", after.ReplacementBorn, "how the lantern was born");
            Expect.Equal(mugId, after.ReplacementFrom, "what the lantern was made from");

            string[] listed = await pair.Host.Run(() => InGame.ListedIds(client));
            Expect.That(listed.Contains(swap.ReplacementId), "the client's list has the lantern");
            Expect.That(!listed.Contains(mugId), "the client's list doesn't have the used-up mug");

            ushort plain = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(plain), 60);
            string plainId = await pair.Host.Run(() => Owned.IdOf(plain));
            await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { plain }));
            LedgerEntry gone = await pair.Host.Run(() => Owned.Entry(plainId));
            Expect.Equal("DestroyedByGame", gone.Ended.Split(' ')[0], "how a plainly destroyed mug's entry ended");
            Lineage plainAfter = await pair.Host.Run(() => InGame.Read(plainId, ""));
            Expect.Equal("", plainAfter.UsedReplacedBy, "what replaced the plainly destroyed mug (nothing)");
        }

        public sealed class Lineage
        {
            public bool Recorded, UsedLive, ReplacementLive;
            public ushort ReplacementNetId;
            public string ReplacementId = "", UsedReplacedBy = "", ReplacementOwner = "", ReplacementBorn = "", ReplacementFrom = "";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: makes a <paramref name="prefab"/> where the item is, records it as the item's
            /// replacement, and destroys the item, as a server-side swap would.</summary>
            [Step]
            public static IEnumerator ReplaceWith(Step<Lineage> step, ushort usedNetId, string prefab)
            {
                NetworkedItem.TryGet(usedNetId, out NetworkedItem used);
                var spec = DV.Globals.G.Items.items.First(i => i.ItemPrefabName == prefab);
                GameObject go = UnityEngine.Object.Instantiate(spec.gameObject, used.transform.position + Vector3.up * 0.3f,
                    Quaternion.identity, WorldMover.OriginShiftParent);
                NetworkedItem made = go.GetComponent<NetworkedItem>();
                float t0 = Time.realtimeSinceStartup;
                while ((made == null || made.NetId == 0) && Time.realtimeSinceStartup - t0 < 5f) yield return null;
                bool recorded = OwnedItemsServer.RecordReplacement(used, made, Multiplayer.Core.Items.ChangeCauses.ItemRemovalReason.DestroyedByGame, "item-lineage scenario");
                UnityEngine.Object.Destroy(used.gameObject);
                for (int i = 0; i < 30; i++) yield return null;
                AuthoritativeItemRegistry.TryGet(made.NetId, out var record);
                step.Result = new Lineage { Recorded = recorded, ReplacementNetId = made.NetId, ReplacementId = record?.PersistentItemId.ToString("D") ?? "" };
            }

            /// <summary>Host: the two entries' lineage as the ledger has them.</summary>
            [Step]
            public static Lineage Read(string usedId, string replacementId)
            {
                Lineage l = new();
                if (OwnedItemsServer.Ledger.TryGet(Guid.Parse(usedId), out OwnedItemEntry used))
                {
                    l.UsedLive = used.IsLive;
                    l.UsedReplacedBy = used.Ended != null && used.Ended.ReplacedBy != Guid.Empty ? used.Ended.ReplacedBy.ToString("D") : "";
                }
                if (Guid.TryParse(replacementId, out Guid r) && OwnedItemsServer.Ledger.TryGet(r, out OwnedItemEntry made))
                {
                    l.ReplacementLive = made.IsLive;
                    l.ReplacementOwner = made.Owner.ToString("D");
                    l.ReplacementBorn = made.Born.Reason.ToString();
                    l.ReplacementFrom = made.Born.From.ToString("D");
                }
                return l;
            }

            /// <summary>Host: the ids in the owner's owned-items list.</summary>
            [Step]
            public static string[] ListedIds(string owner)
            {
                ServerPlayer player = NetworkServer.Instance.ServerPlayers.First(p => p.Guid.ToString("D") == owner);
                return OwnedItemRows.For(player).Select(row => row.Id.ToString("D")).ToArray();
            }
        }
    }
}
