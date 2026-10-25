using System;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A shelf item's whole life (R7; docs/design/items/world-items.md, "Shelves"). The client drops a
    /// mug of its own on the floor of the nearest shed, as <c>owned-list-shelf</c> does: the server has
    /// it on that shelf and the client sees it there (SH1). The client then leaves and joins again, a
    /// late joiner, and is shown the mug on the shelf (SH2, OI-SYNC-04). After a save and a restart the
    /// mug is still on the shelf in both games, the shelf found by its key in each (OI-SYNC-09), and
    /// the client retrieves it from its owned-items list into its Lost and Found (OI-LIST-09).
    /// <c>MODE=twin</c>: with the mug on the shelf, each game is given a second shelf of the same key and
    /// must still resolve the mug's shelf (A1): one as a newly loaded tile far away, which a key already
    /// known must still find, and one whose pivot is nearer the mug, which nearest-pivot would pick.
    /// </summary>
    [Scenario("shelf-item", "A client's mug on a shelf (a shed): on the server's shelf, shown to the client after it joins again, still there after a restart, and retrieved from the owned-items list")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class ShelfItem : Scenario<ShelfItem.Options>
    {
        public sealed class Options
        {
            /// <summary>"drop" at the client's feet, or "throw" onto the floor.</summary>
            public string Release = "drop";

            /// <summary>"twin": a same-keyed second shelf, then stop.</summary>
            public string Mode = "";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort mug = await OwnedListShelf.Shelve(pair, new OwnedListShelf.Options { Who = "client", Release = o.Release, Seen = true });
            string id = await pair.Host.Run(() => Owned.IdOf(mug));
            if (o.Mode == "twin")
            {
                await Twin(pair, mug);
                return;
            }

            // A late joiner: the client leaves the mug on the shelf and joins again.
            await KeepUncollected(pair);
            await pair.Client.Quit();
            await pair.Rejoin();
            await Task.Delay(12000);
            await SeenOnShelf(pair, mug, "after the client joins again");

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart();
            await Task.Delay(12000);   // the client receives its items
            await KeepUncollected(pair);
            mug = await pair.Host.Run(() => Owned.NetIdOf(id));
            Require.That(mug != 0, "the mug is in the world after the restart");
            ItemRecord record = await pair.Host.Run(() => Items.Record(mug));
            Report.Note($"host after the restart: {record.Placement} on {record.ParentKind}");
            Expect.Equal("StaticParent", record.ParentKind, "what the mug is on after the restart");
            Expect.Equal("Shelf", (await pair.Host.Run(() => Owned.Entry(id))).Place, "the mug's ledger home after the restart");
            Expect.Equal("on a shelf", await pair.Host.Run(() => LedgerRows.InGame.RowWhere(client, id)), "the client's owned-items row after the restart");
            await SeenOnShelf(pair, mug, "after the restart");

            OwnedRetrieve.Result r = await Retrieve(pair, client, id);
            Expect.That(r.Accepted, "the shelved mug's retrieval is accepted", r.Reason);
            OwnerInfo now = await pair.Host.Run(() => Ownership.ById(id));
            Expect.That(now.Lost && now.LostEntry == id, "the mug is in the client's Lost and Found", now.ToString());
        }

        // Nothing goes to Lost and Found while its owner is away, or after the restart resets it.
        private static async Task KeepUncollected(Pair pair)
        {
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 5000, Nearby = 5000, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
        }

        private static async Task SeenOnShelf(Pair pair, ushort mug, string when)
        {
            Abs at = await pair.Host.Run(() => OwnedListShelf.InGame.At(mug));
            await pair.Client.Run(() => OwnedListShelf.InGame.GoTo(at), 60);
            string seen = "";
            for (int i = 0; i < 20; i++)
            {
                seen = await pair.Client.Run(() => OwnedListShelf.InGame.Copy(mug));
                if (seen.Contains("parent OldShed")) break;
                await Task.Delay(1000);
            }
            Report.Note($"client copy {when}: {seen}");
            Expect.That(seen.Contains("parent OldShed"), $"the client has the mug on the shelf {when}", seen);
        }

        private static async Task Twin(Pair pair, ushort mug)
        {
            Abs at = await pair.Host.Run(() => OwnedListShelf.InGame.At(mug));
            await pair.Client.Run(() => OwnedListShelf.InGame.GoTo(at), 60);
            await Task.Delay(3000);
            foreach ((string name, Peer peer) in new[] { ("host", pair.Host), ("client", pair.Client) })
            {
                string seen = await peer.Run(() => InGame.TwinShelves(mug));
                Report.Note($"{name}: {seen}");
                Expect.That(seen.StartsWith("ok", StringComparison.Ordinal), $"the {name} resolves the mug's shelf beside a same-keyed twin", seen);
            }
        }

        private static async Task<OwnedRetrieve.Result> Retrieve(Pair pair, string client, string itemId)
        {
            uint handle = await pair.Host.Run(() => OwnedRetrieve.InGame.HandleOf(client, itemId));
            if (handle == 0) return new OwnedRetrieve.Result { Reason = "not in the server's list" };
            return await pair.Client.Run<OwnedRetrieve.Result>(s => OwnedRetrieve.InGame.Retrieve(s, handle), 30);
        }

        public static class InGame
        {
            /// <summary>
            /// This game: beside the shelf the mug is on, a shelf of the same key, as a tile loaded far away
            /// (a pose on it must resolve to it, though the key was already known) and then one whose pivot
            /// is at the mug (the mug's own pose must still resolve to its shelf). Both are removed after.
            /// </summary>
            [Step]
            public static string TwinShelves(ushort netId)
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null) return "no object";
                ItemStaticParent shelf = item.GetComponentInParent<ItemStaticParent>();
                if (shelf == null) return "the mug is not on a shelf here";
                Transform mine = shelf.transform;
                string key = WorldItemStableIdentity.CaptureStaticParent(mine);
                Vector3 local = mine.InverseTransformPoint(item.transform.position);
                Vector3 abs = Dev.Abs(item.transform.position);
                if ((item.transform.position - mine.position).sqrMagnitude < 0.05f * 0.05f)
                    return "the mug is at its shelf's pivot, so a twin there can't be told apart";

                WorldItemStaticParentRegistry.Rebuild();
                if (!WorldItemStaticParentRegistry.TryResolve(key, abs, local, out Transform first) || first != mine)
                    return $"the shelf alone resolves to {first?.name ?? "nothing"}";

                GameObject far = Twin(mine, mine.position + Vector3.right * 300f);
                GameObject near = null;
                try
                {
                    if (WorldItemStableIdentity.CaptureStaticParent(far.transform) != key) return "the twin's key differs";
                    WorldItemStaticParentRegistry.scenesChanged = true;   // as a tile loading
                    WorldItemStaticParentRegistry.TryResolve(key, Dev.Abs(far.transform.TransformPoint(local)), local, out Transform onFar);
                    if (onFar != far.transform) return $"a pose on the far twin resolved to {Where(onFar, mine, far.transform)}";

                    near = Twin(mine, item.transform.position);
                    WorldItemStaticParentRegistry.scenesChanged = true;
                    WorldItemStaticParentRegistry.TryResolve(key, abs, local, out Transform onMine);
                    if (onMine != mine) return $"the mug's pose resolved to {Where(onMine, mine, far.transform)} (a twin's pivot is at the mug)";
                    return "ok: the far twin and the mug's shelf each resolved";
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(far);
                    if (near != null) UnityEngine.Object.DestroyImmediate(near);
                    WorldItemStaticParentRegistry.scenesChanged = true;
                }
            }

            private static GameObject Twin(Transform shelf, Vector3 at)
            {
                GameObject twin = new(shelf.name);
                twin.transform.SetParent(shelf.parent, false);
                twin.transform.SetPositionAndRotation(at, shelf.rotation);
                twin.AddComponent<ItemStaticParent>();
                return twin;
            }

            private static string Where(Transform got, Transform mine, Transform far) =>
                got == null ? "nothing" : got == mine ? "the mug's shelf" : got == far ? "the far twin" : "the near twin";
        }
    }
}
