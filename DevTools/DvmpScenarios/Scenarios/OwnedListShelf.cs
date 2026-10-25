using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The owned-items list names a shelf (OI-LIST-21; docs/design/items/owned-items.md, "The
    /// owned-items list"). A player carries a mug of their own into the nearest shed (a vanilla
    /// <c>ItemStaticParent</c>, which takes what lands in it) and drops it on the floor (or throws
    /// it, RELEASE=throw). The server's record must have it on that shelf, the ledger's home must be
    /// <c>Shelf</c>, and the owner's row must read "on a shelf". The host's own player does it by
    /// default; WHO=client is <c>item-bug-client-shelf</c> (SH1, fixed in R7: a client's drop didn't reach the
    /// server's shelf); <c>shelf-item</c> follows a shelved mug through a rejoin, a restart and a retrieval.
    /// </summary>
    [Scenario("owned-list-shelf", "An owned item put on a shelf (a shed) is homed there in the ledger and listed \"on a shelf\"")]
    [Needs(Need.PlayerHost)]
    [Budget(180)]
    public sealed class OwnedListShelf : Scenario<OwnedListShelf.Options>
    {
        public sealed class Options
        {
            /// <summary>Whose mug, and who puts it on the shelf: "client", or "host" (the host's own player; needs a player host).</summary>
            public string Who = "host";
            /// <summary>How it goes onto the shelf: "drop" at the player's feet, or "throw" onto the floor.</summary>
            public string Release = "drop";
            /// <summary>Also: the client has its object of the mug on the shelf (brought beside it when the host
            /// drops it): <c>item-bug-shelf-unseen</c> (SH2).</summary>
            public bool Seen = false;
        }

        protected override Task Run(Pair pair, Options o) => Shelve(pair, o);

        internal static async Task<ushort> Shelve(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            if (host && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = host ? pair.Host : pair.Client;
            string owner = host ? await pair.Host.Run(() => Owned.HostIdentity()) : await pair.Host.Run(() => Owned.ClientIdentity());
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", o.Who));
            await pair.Host.Run(() => Ownership.Give(mug, o.Who));
            string id = await pair.Host.Run(() => Owned.IdOf(mug));
            await who.Run<int>(s => Items.Stash(s, mug, true), 60);

            string shed = await who.Run<string>(s => InGame.DropInShed(s, mug, o.Release == "throw"), 90);
            Report.Note(who.Name + ": " + shed);
            Report.Note("host copy: " + await pair.Host.Run(() => InGame.Copy(mug)));
            ItemRecord record = await Until.Value(() => pair.Host.Run(() => Items.Record(mug)),
                r => r.ParentKind == "StaticParent", 30, "the host has the mug on the shelf");
            Report.Note($"host: {record.Placement} on {record.ParentKind}, last move {record.LastMove} {record.LastMoveDetail}");
            LedgerEntry entry = await pair.Host.Run(() => Owned.Entry(id));
            Report.Note("ledger: " + entry);
            Expect.Equal("Shelf", entry.Place, "the mug's ledger home");
            Expect.Equal(owner, entry.Owner, "the mug's owner");
            Expect.Equal("on a shelf", await pair.Host.Run(() => LedgerRows.InGame.RowWhere(owner, id)), $"the {o.Who}'s owned-items row for the mug");
            Expect.Empty(await LedgerSave.SettledAudit(pair, new[] { id }), "what the ledger disagrees with");
            if (!o.Seen)
                return mug;
            // Everyone sees it there: the client too, brought beside it when the host dropped it.
            if (host)
            {
                Abs at = await pair.Host.Run(() => InGame.At(mug));
                await pair.Client.Run(() => InGame.GoTo(at), 60);
            }
            string seen = "";
            for (int i = 0; i < 20; i++)
            {
                seen = await pair.Client.Run(() => InGame.Copy(mug));
                if (seen.Contains("parent OldShed")) break;
                await Task.Delay(1000);
            }
            Report.Note("client copy: " + seen);
            Expect.That(seen.Contains("parent OldShed"), "the client has the mug on the shelf", seen);
            return mug;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: where its object of the item is.</summary>
            [Step]
            public static Abs At(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null
                    ? Positions.Of(item.transform.position) : throw new Exception("no object of item " + netId);

            /// <summary>This peer's player beside a point (absolute).</summary>
            [Step]
            public static IEnumerator GoTo(Abs at) => LocalPlayer.TeleportAbs(new Vector3(at.X + 0.8f, at.Y + 0.1f, at.Z), 2f);

            /// <summary>Where this peer's object of the item is, and its lease.</summary>
            [Step]
            public static string Copy(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null
                    ? $"at {Dev.Abs(item.transform.position)}, active {item.gameObject.activeInHierarchy}, parent {item.transform.parent?.name}, lease {NetworkedItemManager.Instance.HasActiveSpatialState(netId)}"
                    : "no object";

            /// <summary>
            /// Client: carries the held item into the nearest shed (an <c>ItemStaticParent</c> with a
            /// floor) and drops it on the floor. Result: the shed and what the item's parent is then.
            /// </summary>
            [Step]
            public static IEnumerator DropInShed(Step<string> step, ushort netId, bool toss)
            {
                Vector3 me = PlayerManager.PlayerTransform.position;
                var (shelf, floor) = UnityEngine.Object.FindObjectsOfType<ItemStaticParent>()
                    .Select(p => (p, floor: p.GetComponentsInChildren<BoxCollider>().FirstOrDefault(c => !c.isTrigger && c.bounds.extents.y < 0.5f)))
                    .Where(x => x.floor != null && x.p.name.StartsWith("OldShed", StringComparison.Ordinal))
                    .OrderBy(x => Vector3.Distance(x.p.transform.position, me))
                    .FirstOrDefault();
                if (shelf == null) throw new Exception("no shed (an ItemStaticParent with a floor) in this game");
                Vector3 top = Dev.Abs(floor.bounds.center + Vector3.up * floor.bounds.extents.y);
                yield return LocalPlayer.TeleportAbs(top + new Vector3(0.8f, 0.1f, 0f), 2f);
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                if (Hands.Held != item) throw new Exception($"not holding item {netId}");
                if (toss) yield return Hands.Throw(Vector3.down * 0.5f + Vector3.right * 0.3f);
                else yield return Hands.Drop();
                int leased = 0, parentedAt = -1, goneAt = -1;
                for (int i = 0; i < 180; i++)
                {
                    if (NetworkedItemManager.Instance.HasActiveSpatialState(netId)) leased++;
                    // The object can be made again under the same net ID (the server's correction).
                    if (!NetworkedItem.TryGet(netId, out NetworkedItem now) || now == null)
                    {
                        if (goneAt < 0) goneAt = i;
                    }
                    else
                    {
                        if (now != item && goneAt < 0) goneAt = i;
                        item = now;
                        if (parentedAt < 0 && item.GetComponentInParent<ItemStaticParent>() != null) parentedAt = i;
                    }
                    yield return null;
                }
                step.Note($"leased {leased} of 180 frames; on the shelf from frame {parentedAt}" +
                          (goneAt >= 0 ? $"; its object was replaced or gone from frame {goneAt}" : ""));
                if (item == null)
                {
                    step.Result = $"{shelf.name} at {top}; the mug has no object";
                    yield break;
                }
                ItemStaticParent on = item.GetComponentInParent<ItemStaticParent>();
                step.Result = $"{shelf.name} at {top}; the mug at {Dev.Abs(item.transform.position)}, {item.GetItemState()}, parent {item.transform.parent?.name}, on {(on != null ? on.name : "no shelf")}";
            }
        }
    }
}
