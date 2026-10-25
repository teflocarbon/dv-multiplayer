using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.Physics;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// OI-SYNC-05 and OI-SYNC-06 (projection.md, "Cells", "Interest" and "Leaving interest"): the
    /// server re-indexes an item for interest at every change to its copy, and a player's interest
    /// takes an item in within 1 cell of theirs (3x3) and lets it go only beyond 2 (5x5), cells
    /// being 128 m squares of absolute X and Z. An unowned mug lies on the ground beside the client,
    /// kinematic on the host so nothing but the run moves it, and the server puts it, a committed
    /// pose each time (<c>FallRescueServer.PutAt</c>, <c>ServerItemSpatial.CommitServerPose</c>, as a
    /// fall rescue does), mid-cell 1, 2, 3, 2 and 1 cells east of the client's cell, while the client
    /// stands still. At each the server must index it in that cell, and the client must have it, keep
    /// it, lose it, still not have it, and have it again: in the server's interest and projections for
    /// the client (<c>NearbyItems</c>, <c>KnownItems</c>) and in the client's own game. Nobody moves, so
    /// only the item's re-index can change the client's interest. It fails if a server move left the
    /// item in its old cell, or if either radius changed (cells of 128 m are this scenario's own
    /// numbers, not read from the mod). Both host kinds.
    /// </summary>
    [Scenario("interest-cells", "A mug the server moves 1, 2, 3, 2 and 1 cells from a still client: re-indexed each time, it enters at 1 cell, stays at 2, leaves at 3, and comes back only at 1")]
    [Budget(300)]
    public sealed class InterestCellsMoves : Scenario<InterestCellsMoves.Options>
    {
        // The rule's own numbers (projection.md, "Cells"): a change to the mod's is a failure here.
        private const float CellSize = 128f;

        public sealed class Options
        {
            /// <summary>The item's prefab.</summary>
            public string Prefab = "Mug";
            /// <summary>Seconds a step that changes nothing is watched before it's checked.</summary>
            public int Hold = 4;
            /// <summary>Seconds a step that changes the client's copy may take.</summary>
            public int Within = 15;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            // The client stands mid-cell, so its cell is the same throughout and the mug's cells are the counted ones.
            Cell c = await pair.Host.Run(() => InGame.ClientCell(CellSize));
            Report.Note($"the client at {c.At}, cell {c.X},{c.Z}, {c.Margin:0} m from its cell's nearest edge");
            if (c.Margin < 12)
            {
                await pair.Client.Run<Abs>(s => Walk.Away(s, c.InwardX, c.InwardZ));
                await Task.Delay(2000);
                c = await pair.Host.Run(() => InGame.ClientCell(CellSize));
                Report.Note($"moved inward: the client at {c.At}, cell {c.X},{c.Z}, {c.Margin:0} m from its nearest edge");
            }
            Require.AtLeast(c.Margin, 8, "the client from its cell's nearest edge (m)");

            ushort mug = await pair.Host.Run<ushort>(s => ClientFallRescue.InGame.SpawnKinematic(s, o.Prefab));
            ItemRecord record = await pair.Host.Run(() => Items.Record(mug));
            Report.Note($"unowned {o.Prefab} {mug} beside the client: {record}");
            Require.Equal("World", record.Placement, "the mug's placement (on the ground)");
            await Until.Value(() => See(pair, mug), s => s.All, o.Within, "the client has the mug beside it");

            // Cells east of the client's, and whether the client has the mug there: in at 1 (3x3), kept at 2 (5x5), out at 3, not back at 2, in again at 1.
            (int Cells, bool Has, string What)[] steps =
            {
                (1, true, "1 cell away (in the 3x3)"),
                (2, true, "2 cells away, already had (kept in the 5x5)"),
                (3, false, "3 cells away (outside the 5x5: left)"),
                (2, false, "back at 2 cells, not had (the 5x5 keeps, it doesn't take in)"),
                (1, true, "back at 1 cell (in the 3x3 again)")
            };
            bool had = true;
            foreach (var (cells, has, what) in steps)
            {
                int cellX = c.X + cells;
                Put put = await pair.Host.Run(() => InGame.Put(mug, (cellX + 0.5f) * CellSize, (c.Z + 0.5f) * CellSize, CellSize));
                Report.Note($"{what}: put at {put.At} (cell {put.CellX},{put.CellZ}), r{put.Before} to r{put.After}; the server indexes it in {put.Indexed}");
                Require.That(put.After > put.Before, $"{what}: the server's put is a committed change (a new revision)", $"r{put.Before} to r{put.After}");
                Require.Equal(cellX, put.CellX, $"{what}: the cell the mug is put in");
                // OI-SYNC-05: the commit re-indexed it where it now is.
                Expect.Equal($"{cellX},{c.Z}", put.Indexed, $"{what}: the cell the server indexes the mug in");

                Seen seen;
                if (has == had)
                {
                    await Task.Delay(o.Hold * 1000);   // nothing is to change: give it the time a change would take
                    seen = await See(pair, mug);
                }
                else
                    seen = await Until.Value(() => See(pair, mug), s => has ? s.All : s.None, o.Within, $"{what}: the client {(has ? "has" : "loses")} the mug");
                Report.Note($"  {seen}");
                Expect.Equal(has, seen.Nearby, $"{what}: in the client's interest on the server (NearbyItems)");
                Expect.Equal(has, seen.Known, $"{what}: projected to the client (KnownItems)");
                Expect.Equal(has, seen.Client, $"{what}: the client's game has the mug");
                had = has;
            }
        }

        private static async Task<Seen> See(Pair pair, ushort mug)
        {
            Seen seen = await pair.Host.Run(() => InGame.ServerView(mug));
            seen.Client = await pair.Client.Run(() => InGame.Has(mug));
            return seen;
        }

        /// <summary>The client's cell as the server has it (InGame.ClientCell).</summary>
        public sealed class Cell
        {
            public int X, Z;
            public Abs At;
            /// <summary>Metres to the cell's nearest edge, and a step towards its middle.</summary>
            public float Margin, InwardX, InwardZ;
        }

        /// <summary>The server's put of the mug (InGame.Put).</summary>
        public sealed class Put
        {
            public Abs At;
            public int CellX, CellZ;
            public uint Before, After;
            /// <summary>The cell the server's index has the mug in ("x,z"), or why none.</summary>
            public string Indexed = "";
        }

        /// <summary>Whether the client has the mug: the server's interest and projection, and its own game.</summary>
        public sealed class Seen
        {
            public bool Nearby, Known, Client;
            public bool All => Nearby && Known && Client;
            public bool None => !Nearby && !Known && !Client;
            public override string ToString() => $"server: nearby {Nearby}, known {Known}; client's game: {(Client ? "has it" : "hasn't")}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static ServerPlayer Client()
            {
                NetworkServer server = NetworkServer.Instance;
                return server.ServerPlayers.FirstOrDefault(p => p != null && p.Peer != server.SelfPeer && p != server.LocalPlayer)
                       ?? throw new Exception("no client connected");
            }

            /// <summary>Host: the client's cell (floor of its absolute X and Z over <paramref name="size"/>), and how far it is from the cell's edges.</summary>
            [Step]
            public static Cell ClientCell(float size)
            {
                Vector3 at = Client().AbsoluteWorldPosition;
                int x = Mathf.FloorToInt(at.x / size), z = Mathf.FloorToInt(at.z / size);
                float inX = at.x - x * size, inZ = at.z - z * size;
                return new Cell
                {
                    X = x, Z = z,
                    At = new Abs { X = at.x, Y = at.y, Z = at.z },
                    Margin = Mathf.Min(Mathf.Min(inX, size - inX), Mathf.Min(inZ, size - inZ)),
                    InwardX = inX < size / 2 ? 25f : -25f,
                    InwardZ = inZ < size / 2 ? 25f : -25f
                };
            }

            /// <summary>
            /// Host: puts the mug at absolute <paramref name="x"/>, <paramref name="z"/>, on the host's
            /// ground there if it has any (else at its height now), kinematic and at rest, as a
            /// committed change everyone who has it is sent (the fall rescue's put). Result: where,
            /// in which cell, the record's revisions, and the cell the server's index has it in.
            /// </summary>
            [Step]
            public static Put Put(ushort netId, float x, float z, float size)
            {
                NetworkedItem item = Items.Find(netId);
                if (!LiveItems.TryGet(netId, out LiveItems.LiveItem record)) throw new Exception("no record of item " + netId);
                Rigidbody body = item.Item?.ItemRigidbody;
                if (body != null) body.isKinematic = true;   // nothing but the run moves it
                float y = Dev.Abs(item.transform.position).y;
                if (ItemFallRescue.BelowTerrain(Dev.World(new Vector3(x, y, z)), out float surface) != null)
                    y = surface + 0.3f;
                Vector3 target = new(x, y, z);
                uint before = record.Revision;
                ServerItemManager.Instance.FallRescue.PutAt(item, record, target,
                    ItemChangeLog.Move(ItemMoveReason.HostCorrection, detail: "interest-cells scenario"));
                LiveItems.TryGet(netId, out record);
                ServerItemManager manager = ServerItemManager.Instance;
                string indexed = manager.HostCellByItem.TryGetValue(item, out WorldItemCellCoord cell) ? cell.ToString()
                    : manager.HostNonSpatialItems.Contains(item) ? "non-spatial" : "no cell";
                return new Put
                {
                    At = new Abs { X = target.x, Y = target.y, Z = target.z },
                    CellX = Mathf.FloorToInt(x / size), CellZ = Mathf.FloorToInt(z / size),
                    Before = before, After = record?.Revision ?? 0,
                    Indexed = indexed
                };
            }

            /// <summary>Host: whether the item is in the client's interest (NearbyItems) and projected to it (KnownItems).</summary>
            [Step]
            public static Seen ServerView(ushort netId)
            {
                ServerPlayer client = Client();
                return new Seen
                {
                    Nearby = client.NearbyItems.Keys.Any(i => i != null && i.NetId == netId),
                    Known = client.KnownItems.Keys.Any(i => i != null && i.NetId == netId)
                };
            }

            /// <summary>Client: whether this game has the item.</summary>
            [Step]
            public static bool Has(ushort netId) => NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null;
        }
    }
}
