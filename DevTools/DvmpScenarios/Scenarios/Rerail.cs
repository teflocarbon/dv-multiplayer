using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.World;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Rerail (playtest 2, FEF-3 L-069): after a rerail the client's copy slid off at 1.5 m/s and was
    /// snapped back and flung every ~1.3 s, forever, while the host had it still. Its followers still
    /// aimed at where the car had been: the rigidbody queue at its derailed pose, the bogie queues at
    /// samples from before the derail. The host derails a car beside the client and shoves it off the
    /// track; once it's at rest the car is rerailed SHIFT metres along its track, by the client
    /// through the host with the comms radio (WHO=client), by the host itself (WHO=host), or by the
    /// host's player with the radio, its request handled in-process (WHO=host-radio). For DURATION
    /// seconds after that the client's car must stay within MAX_OFF metres of the host's and not move.
    /// </summary>
    [Scenario("rerail", "A derailed car at rest is rerailed (by the client's radio, the host, or the host's radio): the client's copy stays where the host has it and still")]
    [Budget(300)]
    public sealed class Rerail : Scenario<Rerail.Options>
    {
        public sealed class Options
        {
            public string Livery = "LocoDE2";
            /// <summary>client (the client's radio), host (the host itself), host-radio (the host player's radio).</summary>
            public string Who = "client";
            /// <summary>Metres along its track from where it stands.</summary>
            public float Shift = 12;
            /// <summary>Seconds the client's car is watched after the rerail.</summary>
            public int Duration = 8;
            /// <summary>Metres the client's car may be from the host's.</summary>
            public float MaxOff = 0.5f;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who is not ("client" or "host" or "host-radio")) throw new ArgumentException("WHO is client, host or host-radio");
            if (o.Who == "host-radio" && pair.Dedicated) throw new HarnessException("WHO=host-radio needs a player host: a dedicated host has no player to use the radio");

            Target t = await pair.Host.Run<Target>(s => InGame.Setup(s, o.Livery, o.Shift));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { t.Car })));
            Report.Note($"spawned {o.Livery} {t.Car} beside the client; it'll be rerailed {t.Shift} m along its track");
            await Task.Delay(3000);

            Report.Note($"the host derails {t.Car} and shoves it off the track");
            await pair.Host.Run(() => InGame.Shove(t.Car));
            await Task.Delay(12000);   // it lands and comes to rest
            Require.That(await pair.Client.Run(() => Cars.Derailed(t.Car)), "derailed on the client");

            Report.Note($"{o.Who} rerails it");
            var sampling = pair.Both.Start<Sample[]>(s => InGame.Sample(s, t.Car, o.Duration + 1), o.Duration + 30);
            await Task.Delay(500);
            if (o.Who == "host") await pair.Host.Run(() => InGame.RerailHere(t.Car, t.Track, t.At, t.Forward));
            else await (o.Who == "client" ? pair.Client : pair.Host).Run(() => InGame.RadioRerail(t.Car, t.Track, t.At, t.Forward));
            PeerResults<Sample[]> samples = await sampling;

            // The host's car is at rest from the rerail on: its last sample is where it is. The
            // client's samples are compared with it once the rerail has had 2.5 s to land.
            Sample[] h = samples.Host, c = samples.Client;
            Sample reference = h.Last();
            double Off(Sample s) => Math.Sqrt(Sq(s.X - reference.X) + Sq(s.Y - reference.Y) + Sq(s.Z - reference.Z));
            Sample[] hs = h.Where(s => s.T >= 2.5f).ToArray(), cs = c.Where(s => s.T >= 2.5f).ToArray();
            double hostMoved = hs.Max(Off), off = cs.Max(Off), v = cs.Max(s => s.V);
            Report.Note($"host: derailed {reference.Derailed}, moved {hostMoved:0.00} m after the rerail landed");
            Report.Note($"client: derailed {c.Last().Derailed}, up to {off:0.00} m from the host and {v:0.00} m/s; placing in {cs.Count(s => s.Placing)}/{cs.Length} samples, kinematic in {cs.Count(s => s.Kinematic)}");
            Report.Note("client distance by second: " + string.Join(" ", Enumerable.Range(0, (int)Math.Ceiling(c.Last().T))
                .Select(k => c.Where(s => Math.Floor(s.T) == k).Select(Off).DefaultIfEmpty(double.NaN).Max())
                .Select(d => double.IsNaN(d) ? "-" : d.ToString("0.00"))));
            Require.That(!reference.Derailed, "the host rerailed the car" + (o.Who == "client" ? " (else the request was refused: money?)" : ""));
            Require.AtMost(hostMoved, 0.2, "the host's car moved after the rerail (m)");
            Expect.That(!c.Last().Derailed, "back on the rails on the client");
            Expect.AtMost(off, o.MaxOff, "the client's car from the host's (m)");
            Expect.AtMost(v, 0.5, "the client's car's speed (m/s)");
        }

        private static double Sq(double v) => v * v;

        public sealed class Target
        {
            public string Car;
            /// <summary>The track's net ID.</summary>
            public ushort Track;
            public Abs At, Forward;
            public float Shift;
        }

        public sealed class Sample
        {
            public float T, X, Y, Z, V;
            public bool Derailed, Kinematic, Placing;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: a car of <paramref name="livery"/> on the track nearest the client's player,
            /// handbrake on, and where to rerail it: <paramref name="shift"/> metres along that track
            /// from where it stands (or the other way, or where it stands, whichever is on the track).
            /// </summary>
            [Step]
            public static IEnumerator Setup(Step<Target> step, string livery, float shift)
            {
                var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First();
                TrainCar car = null;
                foreach (float d in new[] { 20f, 40f, 60f, 80f })
                foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
                    if (car == null)
                        car = CarSpawner.Instance.SpawnCarOnClosestTrack(client.transform.position + dir * d, Cars.Livery(livery), false, true, false);
                if (car == null) throw new Exception("no room to spawn " + livery);
                Cars.Record(new[] { car });
                car.brakeSystem.SetHandbrakePosition(1f);
                for (int i = 0; i < 120; i++) yield return null;
                var track = car.Bogies[0].track;
                Vector3 pos = car.transform.position, fwd = car.transform.forward, target = pos;
                foreach (float s in new[] { shift, -shift, 0f })
                {
                    Vector3 p = pos + fwd * s;
                    var (point, _) = RailTrack.GetClosestPoint(track, p);
                    if (point.HasValue && Vector3.Distance((Vector3)point.Value.position + WorldMover.currentMove, p) < 1.5f) { target = p; shift = s; break; }
                }
                step.Result = new Target
                {
                    Car = car.ID, Track = NetworkedRailTrack.GetFromRailTrack(track).NetId,
                    At = new Abs { X = Dev.Abs(target).x, Y = Dev.Abs(target).y, Z = Dev.Abs(target).z }, Forward = new Abs { X = fwd.x, Y = fwd.y, Z = fwd.z }, Shift = shift
                };
            }

            /// <summary>Host: derails the car and shoves it off the track.</summary>
            [Step]
            public static void Shove(string id)
            {
                var car = Trains.Find(id);
                car.Derail();
                car.rb.AddForce(car.transform.right * 3f + Vector3.up, ForceMode.VelocityChange);
            }

            /// <summary>Host: rerails the car itself, at <paramref name="at"/> (absolute) facing <paramref name="forward"/>.</summary>
            [Step]
            public static void RerailHere(string id, ushort track, Abs at, Abs forward)
            {
                if (!NetworkedRailTrack.TryGet(track, out NetworkedRailTrack t)) throw new Exception("no track " + track);
                Trains.Find(id).Rerail(t.RailTrack, Dev.World(V(at)), V(forward));
            }

            /// <summary>
            /// This game's player rerails the car with the comms radio, confirming a rerail to
            /// <paramref name="at"/> (absolute) facing <paramref name="forward"/>: the radio's own
            /// confirm (RerailController.OnUse), which the mod turns into a request to the host
            /// (RerailControllerPatch), handled in-process on the host's own game.
            /// </summary>
            [Step]
            public static void RadioRerail(string id, ushort track, Abs at, Abs forward)
            {
                var radio = Resources.FindObjectsOfTypeAll<DV.RerailController>().FirstOrDefault(r => r.gameObject.scene.IsValid())
                            ?? throw new Exception("no comms radio rerailer in this game");
                if (!NetworkedRailTrack.TryGet(track, out NetworkedRailTrack t)) throw new Exception("no track " + track);
                radio.carToRerail = Trains.Find(id);
                radio.rerailTrack = t.RailTrack;
                radio.rerailPointWorldAbsPosition = V(at);
                radio.rerailPointWorldForward = V(forward);
                radio.rerailPrice = 0f;   // the host charges for it
                radio.CurrentState = DV.RerailController.State.ConfirmRerail;
                radio.OnUse();
            }

            /// <summary>Either peer: samples the car every 0.1 s: time, absolute position, speed,
            /// derailed, kinematic, and on a client whether its TrainSpeedQueue is placing it.</summary>
            [Step]
            public static IEnumerator Sample(Step<Sample[]> step, string id, float seconds)
            {
                var car = Trains.Find(id);
                var queue = car.GetComponent<Multiplayer.Client.Trains.TrainSpeedQueue>();
                var samples = new List<Sample>();
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds;)
                {
                    var a = Dev.Abs(car.rb.position);
                    samples.Add(new Sample
                    {
                        T = (float)Math.Round(Time.realtimeSinceStartup - t0, 2), X = a.x, Y = a.y, Z = a.z,
                        V = (float)Math.Round(car.rb.velocity.magnitude, 2), Derailed = car.derailed, Kinematic = car.rb.isKinematic,
                        Placing = queue != null && queue.Placing
                    });
                    yield return new WaitForSecondsRealtime(0.1f);
                }
                step.Result = samples.ToArray();
            }

            private static Vector3 V(Abs a) => new Vector3(a.X, a.Y, a.Z);
        }
    }
}
