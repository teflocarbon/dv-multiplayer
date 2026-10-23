using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Job overviews appear while both players are at the station office, and the client can pick
    /// them up (playtest 2). The client acknowledges each overview it creates at the revision it got.
    /// Every game near a new overview sends an update as it appears, so the overview's revision moves
    /// on while that acknowledgement is in flight. A server that took it only at the exact revision
    /// dropped it, and then refused that client's pickups (the overview jumped back to the table).
    /// 1. Both players go 2.6 km away so DV drops the station's jobs. Then the client (with LAG ms of
    ///    receive lag) and the host come back to the office together, and new overviews generate.
    /// 2. While they appear, the host nudges each overview the client has been sent but hasn't
    ///    acknowledged yet. That stands in for the other games' updates (NUDGE=0 relies on those
    ///    happening by themselves).
    /// 3. Every overview the client has must be acknowledged on the server, and the client picks up
    ///    PICKS of them: each must stay in its hand, recorded as the client's on the host.
    /// </summary>
    [Scenario("overview-pickup", "New job overviews appear while the client's acknowledgements are in flight: the server takes every one, and the client's pickups of them hold")]
    [Budget(600)]
    public sealed class OverviewPickup : Scenario<OverviewPickup.Options>
    {
        public sealed class Options
        {
            /// <summary>The station's yard ID.</summary>
            public string Station = "FM";
            /// <summary>The client's receive lag on the way back, ms.</summary>
            public int Lag = 250;
            /// <summary>How many overviews the client picks up.</summary>
            public int Picks = 3;
            /// <summary>The host nudges each new overview before the client's acknowledgement arrives.</summary>
            public bool Nudge = true;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            pair.Cleanup(() => pair.Client.Run(() => Players.EmptyHands()));
            // Debts make DV refuse jobs; a job left taken keeps the station from making new ones until
            // every player is far away (the jobs scenario).
            await pair.Host.Run(() => Jobs.ClearDebts());
            Report.Note($"both players 2.6 km from {o.Station} so its jobs expire");
            await Places.Away(pair);

            using (await pair.Client.Lag(o.Lag))
            {
                Running<Nudged> nudger = o.Nudge ? pair.Host.Start<Nudged>(s => InGame.Nudge(s), 120) : null;
                Report.Note($"both come back to the office (client receive lag {o.Lag} ms)");
                Task client = pair.Client.Run(() => Players.Visit(Places.OfficeX - 4f, Places.OfficeZ, 1f), 90);
                if (!pair.Dedicated) await pair.Host.Run(() => Players.Visit(Places.OfficeX, Places.OfficeZ, 1f), 90);
                await client;
                await Task.Delay(20_000);   // the overviews print while the acknowledgements are in flight
                if (nudger != null)
                {
                    Nudged n = await nudger;
                    Report.Note($"host nudged {n.Count} overview(s) before the client's acknowledgement came; {n.Moved} moved on a revision");
                }
            }

            Acks a = await pair.Host.Run<Acks>(s => InGame.Acks(s, o.Station), 40);
            Report.Note($"overviews at {o.Station}: {a.Overviews}, the client has {a.Known}, acknowledged {a.Acked}");
            Require.That(a.Overviews != 0, $"job overviews at {o.Station}", "none");
            Expect.Empty(a.Unacked, "overviews the client has but the server never took its acknowledgement for");

            int picked = 0;
            foreach (ushort id in a.KnownIds.Take(o.Picks))
            {
                picked++;
                Picked c = await pair.Client.Run<Picked>(s => InGame.Pick(s, id));
                Recorded h = await pair.Host.Run(() => InGame.Recorded(id));
                Report.Note($"overview {id}: client holds it {c.Held} (still after 3 s: {c.Kept}{(c.Error != null ? ", " + c.Error : "")}); host records {h.Placement}, the client's: {h.Mine}");
                Expect.That(c.Kept, $"overview {id}: the client's pickup held");
                Expect.That(h.Mine, $"overview {id}: the host has it in the client's hand");
                await pair.Client.Run(() => InGame.Drop());
            }
            Require.That(picked > 0, "the client has an overview to pick up", "none");
        }

        public sealed class Nudged
        {
            public int Count;
            /// <summary>Overviews whose revision had moved on from the nudge by the end.</summary>
            public int Moved;
        }

        public sealed class Acks
        {
            public int Overviews, Known, Acked;
            public ushort[] Unacked, KnownIds;
        }

        public sealed class Picked
        {
            public bool Held, Kept;
            public string Error;
        }

        public sealed class Recorded
        {
            public string Placement;
            public bool Mine;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static Multiplayer.Server.Session.ServerPlayer Client()
            {
                var server = Multiplayer.Server.Session.NetworkServer.Instance;
                return server.ServerPlayers.First(p => p.PlayerId != server.SelfId);
            }

            /// <summary>
            /// Host: while new job overviews appear, nudges each one the client has been sent (it's in
            /// the client's known items) but hasn't acknowledged yet, so its revision moves on before the
            /// client's acknowledgement arrives. Stands in for another game's update to a new overview.
            /// Runs until no new overview has turned up for 15 s (at most 90 s).
            /// </summary>
            [Step]
            public static IEnumerator Nudge(Step<Nudged> step)
            {
                var client = Client();
                var nudged = new Dictionary<ushort, uint>();
                float start = Time.realtimeSinceStartup, last = start;
                while (Time.realtimeSinceStartup - start < 90f && (nudged.Count == 0 || Time.realtimeSinceStartup - last < 15f))
                {
                    foreach (var item in client.KnownItems.Keys.ToList())
                    {
                        if (item == null || nudged.ContainsKey(item.NetId) || item.GetTrackedItem<JobOverview>() == null ||
                            client.AcknowledgedWorldItems.Contains(item.NetId) || !LiveItems.TryGet(item.NetId, out var record))
                            continue;
                        nudged[item.NetId] = record.Revision;
                        item.transform.position += Vector3.up * 0.05f;
                        last = Time.realtimeSinceStartup;
                    }
                    yield return null;
                }
                step.Result = new Nudged
                {
                    Count = nudged.Count,
                    Moved = nudged.Count(kv => LiveItems.TryGet(kv.Key, out var r) && r.Revision > kv.Value)
                };
            }

            /// <summary>Host: the station's job overviews, which of them the client has (the server sent it
            /// the item) and which the server took its acknowledgement for. Waits up to 15 s for
            /// acknowledgements.</summary>
            [Step]
            public static IEnumerator Acks(Step<Acks> step, string station)
            {
                var client = Client();
                var st = StationController.allStations.First(s => s.stationInfo.YardID == station);
                List<NetworkedItem> overviews = null, known = null;
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 15f;)
                {
                    overviews = st.logicStation.availableJobs
                        .Select(j => NetworkedJob.TryGetFromJob(j, out var nj) ? nj.JobOverview : null)
                        .Where(o => o != null && o.GetItemState() == ItemState.Dropped).ToList();
                    known = overviews.Where(o => client.KnownItems.ContainsKey(o)).ToList();
                    if (known.Count > 0 && known.All(o => client.AcknowledgedWorldItems.Contains(o.NetId)))
                        break;
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                var acked = known.Where(o => client.AcknowledgedWorldItems.Contains(o.NetId)).ToList();
                step.Result = new Acks
                {
                    Overviews = overviews.Count, Known = known.Count, Acked = acked.Count,
                    Unacked = known.Except(acked).Select(o => o.NetId).ToArray(),
                    KnownIds = known.Select(o => o.NetId).ToArray()
                };
            }

            /// <summary>Client: picks up a job overview with the real grabber and sees whether it stays in
            /// the hand (a pickup the server refuses jumps back to the table).</summary>
            [Step]
            public static IEnumerator Pick(Step<Picked> step, ushort id)
            {
                NetworkedItem item = null;
                for (float t0 = Time.realtimeSinceStartup; (!NetworkedItem.TryGet(id, out item) || item == null) && Time.realtimeSinceStartup - t0 < 10f;) yield return null;
                if (item == null) throw new Exception("overview " + id + " isn't in the client's game");
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                string error = null;
                yield return Steps.Try(Hands.Pickup(id), e => error = e.Message);
                bool held = Hands.Held != null && Hands.Held.NetId == id;
                yield return new WaitForSecondsRealtime(3f);
                step.Result = new Picked { Held = held, Kept = Hands.Held != null && Hands.Held.NetId == id && item.GetItemState() == ItemState.InHand, Error = error };
            }

            /// <summary>Host: how the server records the item, and whether in the client's hand.</summary>
            [Step]
            public static Recorded Recorded(ushort id)
            {
                LiveItems.TryGet(id, out var r);
                return new Recorded { Placement = r?.Placement.ToString(), Mine = r != null && r.PlacementPlayerId == Client().PlayerId };
            }

            [Step]
            public static IEnumerator Drop()
            {
                yield return Hands.Drop();
                yield return new WaitForSeconds(1);
            }
        }
    }
}
