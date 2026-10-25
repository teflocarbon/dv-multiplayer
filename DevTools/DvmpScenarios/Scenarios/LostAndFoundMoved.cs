using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// OI-LF-18: Lost and Found measures its distances from where the item is now on the server, its
    /// object's position (lost-and-found.md, "The collection"; <c>LostAndFoundServer.BuildCandidate</c>),
    /// not from where it was dropped or from a position its record stored. A player who isn't here
    /// owns a mug lying in a loco's cab next to the client: an away owner's items are collected from
    /// train interiors too, but the client within the nearby-player radius protects it, so for some
    /// seconds nothing is collected. Then the host moves the loco, the mug still in its cab, to a
    /// track far from every player, and the client stays where it is, by the spot the mug was
    /// dropped (the host's player too, on a player host: it never comes near the new track). The
    /// mug must then be collected into its owner's Lost and Found. Distances read from the drop spot
    /// or from a stale stored position would keep the client protecting it, and it would never go.
    /// Both host kinds: on a dedicated host the client is the only player.
    /// </summary>
    [Scenario("lost-and-found-moved", "An absent owner's mug in a cab next to the client stays; the loco moved far away with the mug, the client staying put, it's collected: distance is from the item's live position")]
    [Budget(300)]
    public sealed class LostAndFoundMoved : Scenario<LostAndFoundMoved.Options>
    {
        public sealed class Options
        {
            /// <summary>The loco the mug lies in.</summary>
            public string Livery = "LocoDE2";
            /// <summary>The policy's nearby-player protection radius for the run, m.</summary>
            public float Nearby = 100;
            /// <summary>At least how far from every player the loco is moved, m.</summary>
            public float Far = 450;
            /// <summary>Seconds the mug must stay uncollected with the client near it.</summary>
            public int Hold = 8;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            // A short grace, so the only thing holding a collection back is the client being near.
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = o.Nearby, Grace = 1 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, o.Livery, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));

            // The absent player's mug is theirs from the start; the client carries it into the cab and steps off.
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            AbsentOwner.Absent absent = await pair.Host.Run(() => AbsentOwner.InGame.GiveToAbsent(mug));
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, mug, "drop", 90), 90);
            await pair.Client.Run<Abs>(s => Walk.Away(s, 0, 8));   // off the train, onto the ground beside it
            await Task.Delay(2000);   // the drop settles in the cab
            ItemRecord record = await pair.Host.Run(() => Items.Record(mug));
            Report.Note($"the absent player's mug {mug} ({absent.Id}) in {loco}'s cab: {record}");
            Require.Equal("TrainInterior", record.Placement, "the mug's placement (in the cab)");
            Require.That(!absent.OwnerConnected, "the mug's owner has no session (an away owner: collected from a train interior)");

            Where near = await pair.Host.Run(() => InGame.Measure(mug, loco, null));
            Abs drop = near.Item;
            Report.Note($"near: {near}");
            Require.AtMost(near.NearestToItem, o.Nearby - 10, "the nearest player to the mug in the cab (m): within the protection radius");
            await Task.Delay(o.Hold * 1000);   // many grace periods, with the client by the mug
            Expect.That(!(await pair.Host.Run(() => Items.Record(mug))).Lost, $"with the client within {o.Nearby} m the mug isn't collected");

            // The loco goes far away, the mug with it; nobody moves. A long grace meanwhile, so the mug
            // is still an object to measure (a collected one is data), then the short one again.
            await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = o.Nearby, Grace = 900 }));
            Moved moved = await pair.Host.Run<Moved>(s => InGame.MoveFar(s, loco, o.Far), 60);
            Report.Note($"moved {loco} {moved.Distance:0} m, from {moved.From} to {moved.To} (track {moved.Track})");
            Where far = await pair.Host.Run(() => InGame.Measure(mug, loco, drop));
            Report.Note($"after the move: {far}");
            Require.AtMost(far.ItemFromCar, 15, "the mug from its loco after the move (m): it rode along in the cab");
            Require.AtLeast(far.ItemFromDrop, o.Far - 50, "the mug from where it was dropped (m)");
            Require.AtLeast(far.NearestToItem, o.Nearby + 50, "the nearest player to the mug (m): nobody near it now");
            // What makes the check mean something: a player still protects the drop spot. Measured
            // from there (or from a position the record kept), the mug would never be collected.
            Require.AtMost(far.NearestToDrop, o.Nearby - 10, "the nearest player to where the mug was dropped (m)");
            await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = o.Nearby, Grace = 1 }));

            await Until.Value(() => pair.Host.Run(() => Items.Record(mug)), r => r.Lost, 30,
                "the mug collected once its loco is far from every player (else distance came from somewhere other than where it is)");
            Expect.Equal(absent.Id, await pair.Host.Run(() => Owned.LostEntryId(mug)), "the mug's Lost and Found entry (its own id, its owner's)");
        }

        /// <summary>Where the server has the mug and how far it is from the players (InGame.Measure).</summary>
        public sealed class Where
        {
            public Abs Item, Record;
            public float NearestToItem = -1, NearestToDrop = -1, ItemFromDrop = -1, ItemFromCar = -1, RecordFromItem = -1;
            public int Players;
            public override string ToString() =>
                $"mug at {Item} ({ItemFromCar:0.#} m from its loco), nearest of {Players} player(s) {NearestToItem:0} m; " +
                $"from the drop spot {ItemFromDrop:0} m, its nearest player {NearestToDrop:0} m; the record's position {Record}, {RecordFromItem:0} m from the mug";
        }

        /// <summary>A car moved to another track (InGame.MoveFar).</summary>
        public sealed class Moved
        {
            public Abs From, To;
            public float Distance;
            public string Track = "";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: where the server's copy of the mug is (its object, what Lost and Found reads),
            /// how far the nearest ready player is from it, from <paramref name="drop"/> (absolute,
            /// null for none) and from its car, and where its record says it is.
            /// </summary>
            [Step]
            public static Where Measure(ushort netId, string car, Abs drop)
            {
                NetworkedItem item = Items.Find(netId);
                Vector3 at = item.transform.position;
                Vector3[] players = NetworkServer.Instance.ServerPlayers
                    .Where(p => p != null && p.LoadingState >= PlayerLoadingState.ReadyForItems)
                    .Select(p => p.WorldPosition).ToArray();
                float Nearest(Vector3 to) => players.Length == 0 ? float.MaxValue : players.Min(p => Vector3.Distance(p, to));
                var where = new Where
                {
                    Item = Positions.Of(at),
                    Players = players.Length,
                    NearestToItem = Nearest(at),
                    ItemFromCar = Vector3.Distance(at, Trains.Find(car).transform.position)
                };
                if (drop != null)
                {
                    Vector3 dropWorld = Dev.World(new Vector3(drop.X, drop.Y, drop.Z));
                    where.NearestToDrop = Nearest(dropWorld);
                    where.ItemFromDrop = Vector3.Distance(at, dropWorld);
                }
                if (LiveItems.TryGet(netId, out LiveItems.LiveItem record))
                {
                    where.Record = new Abs { X = record.Position.x, Y = record.Position.y, Z = record.Position.z };
                    where.RecordFromItem = Vector3.Distance(Dev.Abs(at), record.Position);
                }
                return where;
            }

            /// <summary>
            /// Host: moves the car to a free track at least <paramref name="far"/> metres from every
            /// player, the way the game's own car move does (<c>TrainCar.MoveToTrackWithCarUncouple</c>,
            /// which the mod sends to the clients), and waits for it and its interior to be there.
            /// </summary>
            [Step]
            public static IEnumerator MoveFar(Step<Moved> step, string id, float far)
            {
                TrainCar car = Trains.Find(id);
                Vector3 from = car.transform.position;
                Vector3[] players = NetworkServer.Instance.ServerPlayers.Where(p => p != null).Select(p => p.WorldPosition).ToArray();
                foreach (float d in new[] { far + 100f, far + 300f, far + 600f, far + 1000f })
                for (int k = 0; k < 8; k++)
                {
                    Vector3 dir = Quaternion.Euler(0f, 45f * k, 0f) * Vector3.forward;
                    var (track, point) = RailTrack.GetClosest(from + dir * d, 30f);
                    if (track == null || !point.HasValue || Cars.Length(track) < 60f) continue;
                    Vector3 to = (Vector3)point.Value.position + WorldMover.currentMove;
                    if (players.Any(p => Vector3.Distance(p, to) < far)) continue;
                    if (CarSpawner.Instance.AllCars.Any(c => c != null && c != car && Vector3.Distance(c.transform.position, to) < 50f)) continue;
                    car.MoveToTrackWithCarUncouple(track, to, point.Value.forward);
                    yield return Wait.Until(() => Vector3.Distance(car.transform.position, to) < 20f, 10f, "the car on its new track");
                    for (int i = 0; i < 30; i++) yield return null;
                    // The interior follows a moving car each LateUpdate; a car that was standing still
                    // is put there once, as the mod's own spawner does after placing a car.
                    if (car.interior != null && Vector3.Distance(car.interior.position, car.transform.position) > 1f)
                        car.GetComponent<TrainCarInteriorPhysics>()?.SyncPosition();
                    yield return new WaitForSecondsRealtime(1f);
                    step.Result = new Moved
                    {
                        From = Positions.Of(from), To = Positions.Of(car.transform.position),
                        Distance = Vector3.Distance(from, car.transform.position), Track = track.name
                    };
                    yield break;
                }
                throw new Exception($"no free track at least {far} m from every player");
            }
        }
    }
}
