using System;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.Physics;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A train item that falls under the map is rescued back into its car, as a committed change
    /// (docs/design/items/physics.md, "Fall rescue"; OI-PHY-11, F38). The client drops a mug in a
    /// loco's cab and it settles; the host's copy is then put 4 m under the ground below the car,
    /// still in the car, as when it slips through the floor. The server must rescue it: its record
    /// a below-map rescue, still in the car, at a new revision the client is sent, and both games
    /// with it back in the cab where it lay. Before R7 the rescue looked at world items only and
    /// moved the host's copy without a revision, so a train item stayed under the map.
    /// </summary>
    [Scenario("fall-rescue-interior", "A train item under the map is rescued back into its car, at a new revision both games have")]
    [Budget(400)]
    public sealed class FallRescueInterior : Scenario<FallRescueInterior.Options>
    {
        public sealed class Options
        {
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "host"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, item, "drop", 30), 90);
            await Until.Value(() => pair.Host.Run(() => LateJoinTrainItem.InGame.Settled(item)), s => s, 30,
                "the mug to settle in the cab");
            await Task.Delay(3000);   // the server's scan remembers where it lay safe

            LateJoinTrainItem.CabSpot lay = await pair.Host.Run(() => LateJoinTrainItem.InGame.Where(item, loco));
            ItemRecord before = await pair.Host.Run(() => Items.Record(item));
            Require.That(lay.InCar, "the host's mug is in the cab", lay.ToString());
            Sunk sunk = await pair.Host.Run(() => InGame.Sink(item));
            Report.Note($"settled at {lay.Local} in the cab, revision {before.Revision}; sunk {sunk.Depth:0.#} m under the ground, still in the car: {sunk.InCar}");
            Require.That(sunk.InCar, "the sunk mug is still in the car");

            ItemRecord r = await Until.Value(() => pair.Host.Run(() => Items.Record(item)), v => v.LastMove == "RescuedFromBelowMap", 30,
                "the server rescues it from below the map (before R7 it looked at world items only)");
            Report.Note($"rescued: {r.LastMoveDetail}; revision {r.Revision}");
            await Task.Delay(3000);   // the rescue reaches the client

            PeerResults<ItemPose> pose = await pair.Both.Run(() => Items.Pose(item));
            LateJoinTrainItem.CabSpot host = await pair.Host.Run(() => LateJoinTrainItem.InGame.Where(item, loco));
            LateJoinTrainItem.CabSpot client = await pair.Client.Run(() => LateJoinTrainItem.InGame.Where(item, loco));
            Report.Note($"host {host}; client {client}");
            Expect.Equal("TrainInterior", r.Placement, "record placement");
            Expect.AtLeast(r.Revision, before.Revision + 1, "record revision, against the settled one plus 1");
            Expect.That(host.InCar, "the host's mug is back in the cab", host.ToString());
            Expect.That(client.InCar, "the client's mug is in the cab", client.ToString());
            Expect.AtMost(Peers.Distance(host.Local, lay.Local), 0.3, "metres from where it lay in the cab");
            Expect.AtMost(Peers.Distance(client.Local, host.Local), 0.1, "metres between the client's and the host's spot in the cab");
            Expect.Equal(r.Revision, pose.Client.Revision, "the client's revision (the rescue was sent as a commit)");
            foreach (var (peer, v) in pose)
                if (v.HasTerrain)
                    Expect.AtMost(v.Depth, 0.2, $"{peer}: depth under its terrain (m)");
        }

        public sealed class Sunk
        {
            public float Depth;
            public bool InCar;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: puts its copy of the item 4 m under the ground below where it lies,
            /// kinematic and still in its car, as when it slips through the car's floor.</summary>
            [Step]
            public static Sunk Sink(ushort id)
            {
                if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) throw new Exception("no item " + id);
                if (ItemFallRescue.BelowTerrain(item.transform.position, out float surface) == null)
                    throw new Exception("no terrain loaded under the car in the host's game");
                Transform parent = item.transform.parent;
                Vector3 abs = Dev.Abs(item.transform.position);
                Vector3 under = Dev.World(new Vector3(abs.x, surface - 4f, abs.z));
                Rigidbody body = item.Item.ItemRigidbody;
                if (body != null) { body.isKinematic = true; body.position = under; }
                item.transform.position = under;
                return new Sunk
                {
                    Depth = ItemFallRescue.BelowTerrain(item.transform.position, out _) ?? -999f,
                    InCar = parent != null && item.transform.parent == parent &&
                            LiveItems.TryGet(id, out LiveItems.LiveItem r) && r.Placement == ItemPlacementKind.TrainInterior
                };
            }
        }
    }
}
