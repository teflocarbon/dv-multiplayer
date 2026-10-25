using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A station office's stock is tidied only where no player could see it (docs/design/items/
    /// world-items.md, "Office stock"). MODE:
    /// - loading (default): the client takes an office prop and leaves it lying, an unowned stock
    ///   fork, and everyone goes far away. While the client counts as still loading, the fork must
    ///   stay: a loading player's place isn't known, so it protects everything (OI-SYNC-10, F30).
    ///   Once it is ready again the fork is cleared (an abandoned fork, 30 s), as before;
    /// - knocked: an office prop is knocked 450 m out of its office, and the client stands beside
    ///   it, nobody at the office. It must stay where it lies; before R7 only the office was checked,
    ///   and it vanished from beside the client (OI-SYNC-13). Once the client leaves, it is put back.
    /// - knocked-edge: as knocked, but the client then stands two cells from the prop: out of the 3x3
    ///   cells new items enter a player's interest by, inside the 5x5 they stay in it by, so its game
    ///   still has the prop. It must stay; before the R7 fixes only 3x3 was checked, and it was put
    ///   back in front of the client (B4).
    /// The fork cleanup's other guards (owned, claimed, in Lost and Found, leased, grabbed:
    /// OI-SYNC-11) are Core's OfficeStockRules tests.
    /// </summary>
    [Scenario("office-stock", ("An office prop left lying isn't cleared while a player is still loading; one knocked out of its office isn't put back while a player is beside it or still has it"))]
    [Budget(300)]
    public sealed class OfficeStock : Scenario<OfficeStock.Options>
    {
        public sealed class Options
        {
            /// <summary>loading, knocked or knocked-edge.</summary>
            public string Mode = "loading";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);   // a just-joined client is still receiving its items
            Prop prop = await pair.Host.Run(() => InGame.NearestProp());
            Require.That(prop.NetId != 0, "an office prop in the world");
            Report.Note($"office prop {prop.NetId} {prop.Prefab} at {prop.At}");
            await pair.Client.Run(() => OwnedListShelf.InGame.GoTo(prop.At), 60);
            if (o.Mode == "loading")
                await Loading(pair, prop);
            else if (o.Mode is "knocked" or "knocked-edge")
                await Knocked(pair, prop, edge: o.Mode == "knocked-edge");
            else
                throw new ArgumentException("MODE: loading, knocked or knocked-edge, not " + o.Mode);
        }

        private static async Task Loading(Pair pair, Prop prop)
        {
            await pair.Client.Run(() => Owned.GoTo(prop.NetId), 60);
            await pair.Client.Run(() => Items.Pickup(prop.NetId), 60);
            await pair.Client.Run(() => Items.Drop(), 30);
            ItemRecord fork = await Until.Value(() => pair.Host.Run(() => Items.Record(prop.NetId)),
                r => r.Known && r.Placement == "World", 15, "the prop lying in the world");
            Report.Note("the fork: " + fork);
            Require.That(await pair.Host.Run(() => InGame.IsFork(prop.NetId)), "the prop is an unowned stock fork");

            await pair.Client.Run<Abs>(s => Walk.Away(s, 900, 0), 60);
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, -900, 0), 60);
            await pair.Host.Run(() => InGame.Loading(true));
            pair.Cleanup(() => pair.Host.Run(() => InGame.Loading(false)));
            await Task.Delay(40000);   // the cleanup clears a fork nobody is near after 30 s
            Expect.That(await pair.Host.Run(() => InGame.Exists(prop.NetId)), "the fork is still there while the client is loading");

            await pair.Host.Run(() => InGame.Loading(false));
            bool exists = await Until.Value(() => pair.Host.Run(() => InGame.Exists(prop.NetId)), still => !still, 50,
                "the abandoned fork cleared once the client is ready");
            Expect.That(!exists, "the fork is cleared once nobody is loading or near");
        }

        private static async Task Knocked(Pair pair, Prop prop, bool edge)
        {
            Abs far = await pair.Client.Run<Abs>(s => Walk.Away(s, 450, 0), 60);
            await pair.Host.Run(() => InGame.Knock(prop.NetId, far));
            pair.Cleanup(() => pair.Host.Run(() => InGame.Release(prop.NetId)));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, -900, 0), 60);
            string where = "beside it";
            if (edge)
            {
                await Task.Delay(5000);   // the client's game has the prop
                // The middle of the cell two east of the prop's (cells are 128 m).
                float dx = ((float)Math.Floor(far.X / 128f) + 2f) * 128f + 64f - far.X;
                await pair.Client.Run<Abs>(s => Walk.Away(s, dx, 0), 60);
                where = $"{dx:0} m east of it, two cells";
            }
            await Task.Delay(25000);   // a knocked prop nobody is near is put back after 15 s
            float fromHome = await pair.Host.Run(() => InGame.FromBaseline(prop.NetId));
            Report.Note($"after 25 s {where}: {fromHome:0} m from its place");
            Expect.AtLeast(fromHome, 100, $"metres the prop is from its place while the client is {where}");

            await pair.Client.Run<Abs>(s => Walk.Away(s, 900, 0), 60);
            float back = await Until.Value(() => pair.Host.Run(() => InGame.FromBaseline(prop.NetId)), m => m < 0.5f, 40,
                "the prop put back once nobody is near it");
            Expect.AtMost(back, 0.5, "metres the prop is from its place once the client left");
        }

        public sealed class Prop
        {
            public ushort NetId;
            public string Prefab = "";
            public Abs At;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static PlayerLoadingState? before;

            /// <summary>Host: the office prop nearest the client, lying in its place.</summary>
            [Step]
            public static Prop NearestProp()
            {
                NetworkServer server = NetworkServer.Instance;
                ServerPlayer client = server.ServerPlayers.First(p => p.Peer != server.SelfPeer && p != server.LocalPlayer);
                NetworkedItem nearest = NetworkedItem.GetAll()
                    .Where(i => i != null && AuthoredItems.IsEligibleOfficeProp(i) &&
                                LiveItems.TryGet(i.NetId, out var r) && r.Placement == ItemPlacementKind.World &&
                                (r.Position - r.BaselinePosition).sqrMagnitude < 0.01f)
                    .OrderBy(i => (i.transform.position - WorldMover.currentMove - client.AbsoluteWorldPosition).sqrMagnitude)
                    .FirstOrDefault();
                return nearest == null ? new Prop()
                    : new Prop { NetId = nearest.NetId, Prefab = nearest.name, At = Positions.Of(nearest.transform.position) };
            }

            [Step]
            public static bool IsFork(ushort netId) =>
                LiveItems.TryGet(netId, out var record) && record.IsReplenishableStockFork && record.Owner == Guid.Empty;

            [Step]
            public static bool Exists(ushort netId) =>
                LiveItems.TryGet(netId, out _) && NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null;

            /// <summary>Host: the client counts as still loading (true), or back as it was.</summary>
            [Step]
            public static bool Loading(bool loading)
            {
                NetworkServer server = NetworkServer.Instance;
                ServerPlayer client = server.ServerPlayers.FirstOrDefault(p => p.Peer != server.SelfPeer && p != server.LocalPlayer);
                if (client == null) return false;
                if (loading)
                {
                    before ??= client.LoadingState;
                    client.LoadingState = PlayerLoadingState.ReadyForCustomizers;
                }
                else if (before.HasValue)
                {
                    client.LoadingState = before.Value;
                    before = null;
                }
                return true;
            }

            /// <summary>Host: knocks the prop to a point (absolute), held there so it doesn't fall.</summary>
            [Step]
            public static IEnumerator Knock(ushort netId, Abs at)
            {
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                if (item == null) throw new Exception("no object of prop " + netId);
                Rigidbody body = item.Item?.ItemRigidbody;
                if (body != null) body.isKinematic = true;
                item.transform.position = new Vector3(at.X + 1f, at.Y + 0.3f, at.Z) + WorldMover.currentMove;
                yield return null;
            }

            [Step]
            public static bool Release(ushort netId)
            {
                if (NetworkedItem.TryGet(netId, out NetworkedItem item) && item?.Item?.ItemRigidbody is { } body)
                    body.isKinematic = false;
                return true;
            }

            /// <summary>Host: how far the prop is from its place, m.</summary>
            [Step]
            public static float FromBaseline(ushort netId) =>
                LiveItems.TryGet(netId, out var record) && NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null
                    ? Vector3.Distance(item.transform.position - WorldMover.currentMove, record.BaselinePosition)
                    : -1f;
        }
    }
}
