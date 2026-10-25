using System;
using System.Collections;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A train item that settled before a game got it (docs/design/items/physics.md, "Loose items on
    /// trains"). The client drops a mug in a loco's cab and it settles. MODE:
    /// - rejoin (default): the client leaves and rejoins, so the mug comes to it in a Create, already
    ///   settled. Its copy must be kinematic in the cab, with a committed state, so the client can
    ///   report an impact on it (OI-PHY-06, -12, F34). The host then pushes the loco, and the mug must
    ///   stay where the host has it in the cab. Before R7 it was a live body the car's motion threw
    ///   about;
    /// - convergence: the client's copy is knocked half a metre off and the host sends a convergence
    ///   correction. It must come back to the host's spot and stay kinematic (OI-PHY-06);
    /// - parent-change: the client throws the mug onto a flatcar. While it still flies, the host's
    ///   record must already have it in the car, at a new revision: a parent change mid-flight is
    ///   committed as one, not only at the settlement (OI-PHY-05, F33).
    /// </summary>
    [Scenario("late-join-train-item", "A settled train item a game gets later (a rejoin, a convergence correction) stays kinematic in its car; a flight that lands in a car commits the car at once")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class LateJoinTrainItem : Scenario<LateJoinTrainItem.Options>
    {
        public sealed class Options
        {
            /// <summary>rejoin, convergence or parent-change.</summary>
            public string Mode = "rejoin";
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            if (o.Mode == "parent-change")
            {
                string car = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "Flatbed", "host"));
                pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { car })));
                Running<Flight> watch = pair.Host.Start<Flight>(s => InGame.WatchParent(s, item, 45f), 90);
                await pair.Client.Run(() => ItemRelease.InGame.ThrowOnto(item, car), 90);
                Flight f = await watch;
                Report.Note($"lease seen {f.Leased}; while it flew the record went {f.Placements}; lease ended {(f.Ended.Length > 0 ? f.Ended : "never")}");
                Require.That(f.Leased, "the throw opened a lease");
                Expect.That(f.InCarWhileLeased, "the host's record had it in the car while it still flew", f.Placements);
                Expect.That(f.Ended.StartsWith("settled:"), "it settled", f.Ended);
                return;
            }

            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "host"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, item, "drop", 30), 90);
            await Until.Value(() => pair.Host.Run(() => InGame.Settled(item)), s => s, 30, "the mug to settle in the cab");
            await Task.Delay(1000);

            if (o.Mode == "rejoin")
            {
                await pair.Client.Quit();
                await pair.Rejoin();
                await Until.Value(() => pair.Client.Run(() => InGame.Has(item)), s => s, 60, "the rejoined client to get the mug");
                await Task.Delay(2000);
            }
            else if (o.Mode == "convergence")
            {
                await pair.Client.Run(() => InGame.Knock(item, 0.5f));
                bool sent = await pair.Host.Run(() => InGame.SendConvergence(item));
                Require.That(sent, "the host sent the client a convergence correction");
                await Task.Delay(2000);
            }
            else
                throw new ArgumentException("MODE: rejoin, convergence or parent-change, not " + o.Mode);

            CabSpot before = await pair.Client.Run(() => InGame.Where(item, loco));
            CabSpot host = await pair.Host.Run(() => InGame.Where(item, loco));
            Report.Note($"client: {before}; host: {host}");
            Expect.That(before.InCar, "the client's mug is in the cab", before.ToString());
            Expect.That(before.Kinematic, "the client's mug is kinematic (glued to the car)");
            if (o.Mode == "rejoin")
                Expect.That(before.Committed, "the client has its settled state (to report an impact on it)");
            Expect.AtMost(Peers.Distance(before.Local, host.Local), 0.1, "metres between the client's and the host's spot in the cab");

            await pair.Host.Run(() => Cars.Push(loco, 6f, 6f, 1.5f), 90);
            await Task.Delay(2000);
            CabSpot after = await pair.Client.Run(() => InGame.Where(item, loco));
            CabSpot hostAfter = await pair.Host.Run(() => InGame.Where(item, loco));
            Report.Note($"after the push: client {after}; host {hostAfter}");
            Expect.That(after.InCar, "the client's mug is still in the cab after the push");
            Expect.AtMost(Peers.Distance(after.Local, hostAfter.Local), 0.1, "metres between the client's and the host's spot in the cab after the push");
        }

        public sealed class CabSpot
        {
            public bool Present, InCar, Kinematic, Committed;
            public Abs Local = new();
            public override string ToString() => Present ? $"{(InCar ? "in the cab" : "not in the cab")} at {Local}, kinematic {Kinematic}, committed {Committed}" : "absent";
        }

        public sealed class Flight
        {
            public bool Leased, InCarWhileLeased;
            public string Placements = "", Ended = "";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the item lies settled, its lease ended.</summary>
            [Step]
            public static bool Settled(ushort id) =>
                !NetworkedItemManager.Instance.HasActiveSpatialState(id) && LiveItems.TryGet(id, out LiveItems.LiveItem r) &&
                r.Placement == ItemPlacementKind.TrainInterior;

            [Step]
            public static bool Has(ushort id) => NetworkedItem.TryGet(id, out NetworkedItem item) && item != null && item.gameObject.activeInHierarchy;

            /// <summary>Where the item is in the car, and whether this game holds it glued there.</summary>
            [Step]
            public static CabSpot Where(ushort id, string carId)
            {
                if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) return new CabSpot();
                TrainCar car = Trains.Find(carId);
                Transform anchor = car.interior ?? car.transform;
                Rigidbody body = item.Item?.ItemRigidbody;
                return new CabSpot
                {
                    Present = true,
                    InCar = item.transform.IsChildOf(anchor),
                    Kinematic = body == null || body.isKinematic,
                    Committed = NetworkedItemManager.Instance.Spatial.TryGetCommitted(id, out _),
                    Local = Local(anchor.InverseTransformPoint(item.transform.position))
                };
            }

            private static Abs Local(Vector3 v) => new Abs { X = v.x, Y = v.y, Z = v.z };

            /// <summary>Client: moves its copy of the item off by some metres, as a drift would.</summary>
            [Step]
            public static bool Knock(ushort id, float metres)
            {
                if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) return false;
                item.transform.position += Vector3.up * metres;
                Rigidbody body = item.Item?.ItemRigidbody;
                if (body != null) { body.isKinematic = false; body.WakeUp(); }
                return true;
            }

            /// <summary>Host: sends every remote player a convergence correction for the item, as the host's sweep does.</summary>
            [Step]
            public static bool SendConvergence(ushort id)
            {
                if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) return false;
                if (!Multiplayer.Server.Items.Projection.ServerItemManager.TryCreateConvergenceSnapshot(item, out ItemUpdate snapshot) || snapshot == null)
                    return false;
                var server = Multiplayer.Server.Session.NetworkServer.Instance;
                foreach (var player in server.ServerPlayers)
                    if (player != server.LocalPlayer && player.PlayerId != 0)
                        server.SendItemUpdate(snapshot, player);
                return true;
            }

            /// <summary>
            /// Host: watches the item's lease and its record's placement until the lease ends:
            /// whether the record had it in a car while the lease was still open.
            /// </summary>
            [Step]
            public static IEnumerator WatchParent(Step<Flight> step, ushort id, float seconds)
            {
                var manager = NetworkedItemManager.Instance;
                Flight f = new();
                string endedBefore = manager.Spatial.LastLeaseEnd(id), last = "";
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < seconds)
                {
                    yield return null;
                    bool leased = manager.HasActiveSpatialState(id);
                    if (!LiveItems.TryGet(id, out LiveItems.LiveItem record)) break;
                    if (leased)
                    {
                        f.Leased = true;
                        string now = $"{record.Placement} r{record.Revision}";
                        if (now != last) f.Placements += (f.Placements.Length > 0 ? ", " : "") + (last = now);
                        f.InCarWhileLeased |= record.Placement == ItemPlacementKind.TrainInterior;
                        continue;
                    }
                    string ended = manager.Spatial.LastLeaseEnd(id);
                    if (f.Leased && ended != endedBefore)
                    {
                        f.Ended = ended;
                        break;
                    }
                }
                step.Result = f;
            }
        }
    }
}
