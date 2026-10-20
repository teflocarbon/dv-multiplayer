using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The comms radio's car tools as a player uses them, through the game's own radio modes
    /// (CommsRadioCarDeleter, CommsRadioCrewVehicle) and so through the mod's patches on them: by the
    /// client, or by the host's own player (radio-tools-host: its requests are handled in-process,
    /// docs/design/host-local-session.md). A car spawned near that player is deleted with the radio: it's
    /// gone on both games. Then a work train is summoned: an invalid request (no such track) is
    /// answered, not left to time out, and a valid one where the deleted car stood is answered Success
    /// and the car is there on both games. The radio refuses a track that doesn't exist before it
    /// sends anything, so the invalid request is sent from the client's own NetworkClient, always the
    /// client's, also in radio-tools-host.
    /// </summary>
    [Scenario("radio-tools", "The comms radio deletes a car (gone on both games) and summons a work train: a request for a track that doesn't exist is answered, a valid one is answered Success and the car is on both games")]
    [Budget(300)]
    public class RadioTools : Scenario<RadioTools.Options>
    {
        public sealed class Options
        {
            /// <summary>Who uses the radio: client or host.</summary>
            public string Who = "client";
            /// <summary>The car deleted.</summary>
            public string Livery = "LocoDM3";
            /// <summary>auto: a livery with no car in the world and no garage, locos first, no longer
            /// than LIVERY: the summon spawns one, for free.</summary>
            public string WorkLivery = "auto";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Peer who = o.Who == "host" ? pair.Host : o.Who == "client" ? pair.Client : throw new ArgumentException("WHO is client or host, not " + o.Who);
            // Near the radio's user: the server refuses a summon further than the radio's range
            // (100 m) from the requester, and a dedicated host's own player is a parked body that
            // can be anywhere from the client.
            Loco car = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, false, o.Who));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, car.Cars)));
            Spot spot = await pair.Host.Run(() => InGame.Under(car.Id));
            Report.Note($"== spawned {o.Livery} {car.Id} (net {car.NetId}) on track {spot.Track}, point {spot.Index}");
            await Task.Delay(2000);   // the spawn reaches the client, as before
            Require.Equal(1, (await pair.Client.Run(() => InGame.CarsOf(o.Livery, car.Id))).Length, $"the client has {car.Id}");

            Report.Note($"== {o.Who} deletes it with the radio");
            await who.Run(() => InGame.Delete(car.Id));
            await Task.Delay(3000);
            foreach (var (peer, left) in await pair.Both.Run(() => InGame.CarsOf(o.Livery, car.Id)))
                Expect.Zero(left.Length, $"{peer}: {car.Id} still there");

            string work = await who.Run(() => InGame.PickWorkLivery(o.WorkLivery, o.Livery));
            pair.Cleanup(async () =>
            {
                string[] ids = await pair.Host.Run(() => InGame.CarsOf(work, ""));
                if (ids.Length > 0) await pair.Host.Run<string[]>(s => Cars.Delete(s, ids));
            });

            Report.Note("== the client summons a work train on a track that doesn't exist");
            Answer bad = await pair.Client.Run<Answer>(s => InGame.SummonInvalid(s, work, 10f), 30);
            Report.Note($"answer: {bad.Response} after {bad.Seconds} s");
            Expect.That(bad.Response != "timeout", "a summon on a track that doesn't exist is answered", bad.Response);

            Report.Note($"== {o.Who} summons a work train where {car.Id} stood");
            Answer good = await who.Run<Answer>(s => InGame.Summon(s, work, spot.Track, spot.Index, 10f), 30);
            Report.Note($"{work}: {good.Response} after {good.Seconds} s");
            Expect.Equal("Success", good.Response, "the summon's answer");
            await Task.Delay(3000);
            foreach (var (peer, ids) in await pair.Both.Run(() => InGame.CarsOf(work, "")))
                Expect.Equal(1, ids.Length, $"{peer}: {work} cars ({string.Join(" ", ids)})");
        }

        public sealed class Spot
        {
            /// <summary>The track's NetworkedRailTrack net ID.</summary>
            public int Track;
            /// <summary>The kinked point nearest the car.</summary>
            public int Index;
        }

        public sealed class Answer
        {
            /// <summary>The SpawnResponse's type, "timeout", or the other response's type name.</summary>
            public string Response;
            public float Seconds;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the track the car stands on and its kinked point nearest the car: where a
            /// work train can be summoned once the car is gone.</summary>
            [Step]
            public static Spot Under(string car)
            {
                TrainCar c = Trains.Find(car);
                RailTrack track = c.Bogies[0].track;
                var points = track.GetKinkedPointSet().points;
                Vector3 abs = c.transform.position - WorldMover.currentMove;
                return new Spot
                {
                    Track = Multiplayer.Shared.World.NetworkedRailTrack.GetFromRailTrack(track).NetId,
                    Index = Enumerable.Range(0, points.Length).OrderBy(i => ((Vector3)points[i].position - abs).sqrMagnitude).First()
                };
            }

            /// <summary>The IDs of this game's cars of a livery (and that ID, if not empty).</summary>
            [Step]
            public static string[] CarsOf(string livery, string id) =>
                UnityEngine.Object.FindObjectsOfType<TrainCar>().Where(c => c.carLivery.id == livery && (id == "" || c.ID == id)).Select(c => c.ID).ToArray();

            /// <summary>
            /// Either side: the radio's car deleter confirming a car, as a player's second click does:
            /// the car pointed at and selected (its price as vanilla's first click sets it), then used.
            /// The mod's patch sends the delete request.
            /// </summary>
            [Step]
            public static void Delete(string car)
            {
                TrainCar c = Trains.Find(car);
                var deleter = Radio<CommsRadioCarDeleter>();
                deleter.carToDelete = c;
                deleter.removePrice = c.playerSpawnedCar ? 0f : Mathf.RoundToInt(Globals.G.GameParams.DeleteCarMaxPrice);
                deleter.CurrentState = CommsRadioCarDeleter.State.ConfirmDelete;
                deleter.OnUse();
            }

            /// <summary>
            /// The livery to summon: <paramref name="livery"/>, or with auto one the summon spawns for
            /// free: no car of it in the world, and no garage (a career can have every loco's garage,
            /// which charges and summons the garage's own car). Locos first, then anything else; no
            /// longer than the <paramref name="fits"/> livery, whose car stood where the summon goes.
            /// </summary>
            [Step]
            public static string PickWorkLivery(string livery, string fits)
            {
                if (livery != "auto") return livery;
                var present = new HashSet<string>(UnityEngine.Object.FindObjectsOfType<TrainCar>().Select(c => c.carLivery.id));
                var lengths = CarSpawner.Instance.carLiveryToCarLength;
                var fitting = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == fits);
                float room = fitting != null && lengths.TryGetValue(fitting, out float fitsLength) ? fitsLength : float.MaxValue;
                return DV.Globals.G.Types.Liveries
                    .Where(l => l.prefab != null && !l.isHidden && !present.Contains(l.id) &&
                                !global::GarageCarSpawner.Spawners.ContainsKey(l) &&
                                lengths.TryGetValue(l, out float length) && length <= room)
                    .OrderByDescending(l => DV.ThingTypes.CarTypes.IsLocomotive(l))
                    .ThenBy(l => lengths[l])
                    .FirstOrDefault()?.id
                    ?? throw new Exception($"no livery to summon: every one no longer than {fits} has a car in the world or a garage");
            }

            /// <summary>
            /// Either side: the radio's work-train summon confirming, as a player's last click does: the
            /// livery picked and the destination (the track's point <paramref name="index"/>) chosen,
            /// then used. The mod's patch sends the request with an RPC ticket. Result: the ticket's
            /// answer and how long it took, read alongside the patch's own handling of it.
            /// </summary>
            [Step]
            public static IEnumerator Summon(Step<Answer> step, string livery, int track, int index, float timeout)
            {
                if (!Multiplayer.Shared.World.NetworkedRailTrack.TryGet((ushort)track, out RailTrack rail)) throw new Exception("no track with net ID " + track);
                var radio = Radio<CommsRadioCrewVehicle>();
                radio.selectedCar = new CommsRadioCrewVehicle.SelectedCar(DV.Globals.G.Types.Liveries.First(l => l.id == livery), global::GarageCarSpawner.Spawners);
                radio.destinationTrack = rail;
                radio.closestPointOnDestinationTrack = rail.GetKinkedPointSet().points[index];
                radio.canMoveToPoint = true;
                radio.CurrentState = CommsRadioCrewVehicle.State.ConfirmSummon;
                var tickets = Multiplayer.Client.Session.RpcManager.Instance;
                uint first = tickets.nextTicketId;
                radio.OnUse();
                var ticket = tickets.activeTickets.Where(kv => kv.Key >= first).Select(kv => kv.Value).FirstOrDefault()
                    ?? throw new Exception("the radio sent no summon request (no RPC ticket)");
                yield return Await(step, ticket, timeout);
            }

            /// <summary>
            /// Client: a work-train summon for a track that doesn't exist (net ID 0), sent as the radio
            /// sends one: the radio itself refuses such a track before sending, so this goes through the
            /// client's own NetworkClient. Result: the answer, or "timeout".
            /// </summary>
            [Step, ClientOnly]
            public static IEnumerator SummonInvalid(Step<Answer> step, string livery, float timeout)
            {
                var ticket = Multiplayer.Client.Session.RpcManager.Instance.CreateTicket(timeout);
                Multiplayer.Client.Session.NetworkClient.Instance.SendWorkTrainRequest(ticket.TicketId, livery, 0, 0, true);
                yield return Await(step, ticket, timeout);
            }

            // The ticket's answer, read before the radio's own callbacks (which update its display and
            // may throw on a radio that was never opened).
            private static IEnumerator Await(Step<Answer> step, Multiplayer.Shared.Protocol.Data.RPCs.RpcTicket ticket, float timeout)
            {
                string response = null;
                float t0 = Time.realtimeSinceStartup;
                ticket.onResolve = (Action<Multiplayer.Shared.Protocol.Data.RPCs.IRpcResponse>)(r =>
                    response = (r as Multiplayer.Shared.Protocol.Data.RPCs.SpawnResponse)?.Response.ToString() ?? r.GetType().Name) + ticket.onResolve;
                ticket.onTimeout = (Action)(() => response = "timeout") + ticket.onTimeout;
                while (response == null && Time.realtimeSinceStartup - t0 < timeout + 5f)
                    yield return null;
                step.Result = new Answer { Response = response ?? "timeout", Seconds = (float)Math.Round(Time.realtimeSinceStartup - t0, 2) };
            }

            private static T Radio<T>() where T : MonoBehaviour =>
                Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(r => r.gameObject.scene.IsValid())
                ?? throw new Exception("no comms radio " + typeof(T).Name + " in this game");
        }
    }

    /// <summary>radio-tools with the host's own player on the radio: a player host only.</summary>
    [Scenario("radio-tools-host", "radio-tools by the host's own player (its requests handled in-process); the invalid-track summon is still the client's")]
    [Needs(Need.PlayerHost)]
    [Budget(300)]
    public sealed class RadioToolsHost : RadioTools
    {
        protected override Task Run(Pair pair, Options o)
        {
            o.Who = "host";
            return base.Run(pair, o);
        }
    }
}
