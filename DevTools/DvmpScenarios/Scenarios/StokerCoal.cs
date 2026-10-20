using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Only a player's shovelling is reported to the host as coal added (playtest 2: stoker locos'
    /// fireboxes sat overfull and their coal flickered). Coal added used to be reported by watching
    /// the firebox's coal input port, which a mechanical stoker writes every simulation tick on every
    /// game (Custom Car Loader's SteamMechanicalStoker: the S484, the 2901). Every game reported its
    /// own stoker, the host's own client too, and the host added it all on top of its own stoker.
    /// Coal taken from the tender (TenderCoal) was reported the same way, by watching its coal pile's
    /// consume port, which the stoker writes too: the host's own client sent itself two a tick per
    /// stoker tender all night (340,000 log lines before the server's 01:08 crash).
    /// 1. Stoker feed, counting the AddCoal and TenderCoal packets the server gets, from the host's
    ///    own player and from the client:
    ///    - with a Custom Car Loader stoker loco installed (STOKER=auto finds one, or names the
    ///      livery), the host starts it and opens its stoker, and the stoker runs on both games;
    ///    - without one (DV has no stoker of its own), the firebox's coal input and the tender's coal
    ///      pile are written the way a stoker writes them, on both games, on an S282.
    ///    No AddCoal or TenderCoal may be sent.
    /// 2. The client shovels coal into the firebox (FireboxSimController.TransferCoal, as the shovel
    ///    does): exactly one AddCoal is sent, from the client.
    /// 3. The client shovels coal out of the tender (CoalPileSimController.TryRemoveCoal, as the
    ///    shovel does): exactly one TenderCoal is sent, from the client.
    /// The loco comes with its tender: spawning one car never brings it, and without it there's no
    /// coal pile. A dedicated host works too.
    /// </summary>
    [Scenario("stoker-coal", "A stoker's feed sends no AddCoal or TenderCoal from any game; one shovelful into the firebox or out of the tender sends exactly one, from the client")]
    [Budget(300)]
    public sealed class StokerCoal : Scenario<StokerCoal.Options>
    {
        public sealed class Options
        {
            /// <summary>auto (the first stoker loco installed), none, or a livery id.</summary>
            public string Stoker = "auto";
            /// <summary>Seconds of stoker feed.</summary>
            public int Feed = 10;
        }

        private const string AddCoal = "ServerboundAddCoalPacket", TenderCoal = "ServerboundTenderCoalPacket";

        protected override async Task Run(Pair pair, Options o)
        {
            string livery = await pair.Host.Run(() => InGame.Pick(o.Stoker));
            bool real = livery != "none";
            if (!real) livery = "LocoS282A";
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLoco(s, livery, true, true));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            pair.Cleanup(() => pair.Client.Run(() => Players.TeleportToGround(7190f, 6880f)));
            Report.Note($"== {livery} {loco.Id} (a real stoker: {real}); the client boards it");
            await pair.Client.Run(() => Players.Board(loco.Id));
            await Task.Delay(4000);   // the cab settles after boarding, as before
            await pair.Host.Run(() => Received.Count(new[] { AddCoal, TenderCoal }));

            // Packets so far from one sender: AddCoal for the loco, TenderCoal for any car (the coal
            // pile is the tender's, not the loco's).
            async Task<(int Host, int Client)> Sent(string packet)
            {
                PacketCount[] counts = await pair.Host.Run(() => Received.Counts(new[] { packet }));
                int From(string who) => counts.Where(c => c.From == who && (packet == TenderCoal || c.Car == loco.NetId)).Sum(c => c.N);
                return (From("host"), From("client"));
            }

            if (real)
            {
                Report.Note($"== the host fires it up and opens the stoker for {o.Feed} s");
                Fed f = await pair.Host.Run<Fed>(s => InGame.Feed(s, loco.Id, o.Feed, true), o.Feed + 60);
                Fed c = await pair.Client.Run(() => InGame.State(loco.Id));
                Report.Note($"stoker rate (normalised) host {f.Stoking}, client {c.Stoking}; firebox host {f.Firebox}, client {c.Firebox}");
                Require.That(f.Ran, "the stoker ran on the host", "never (no steam pressure?)");
            }
            else
            {
                Report.Note($"== no stoker loco: both games write the firebox's coal input and the tender's coal pile the way a stoker does, for {o.Feed} s");
                await pair.Both.Run<Fed>(s => InGame.Feed(s, loco.Id, o.Feed, false), o.Feed + 30);
            }
            var add = await Sent(AddCoal);
            Require.That(add.Host == 0 && add.Client == 0, "no stoker feed sent as coal added", $"AddCoal from the host's own player {add.Host}, client {add.Client}");
            var tender = await Sent(TenderCoal);
            Require.That(tender.Host == 0 && tender.Client == 0, "no stoker feed sent as coal taken from the tender", $"TenderCoal from the host's own player {tender.Host}, client {tender.Client}");

            Report.Note("== the client shovels coal in");
            await pair.Client.Run(() => InGame.Shovel(loco.Id, 2f));
            await Task.Delay(2000);   // the packet reaches the host, as before
            add = await Sent(AddCoal);
            Expect.Equal(1, add.Client, "AddCoal from the client for one shovelful");
            Expect.Zero(add.Host, "AddCoal from the host's own player");

            Report.Note("== the client shovels coal out of the tender");
            float taken = await pair.Client.Run(() => InGame.TakeFromTender(loco.Id, 2f));
            await Task.Delay(2000);
            tender = await Sent(TenderCoal);
            Report.Note($"took {taken}");
            Expect.Equal(1, tender.Client, "TenderCoal from the client for one shovelful from the tender");
            Expect.Zero(tender.Host, "TenderCoal from the host's own player");
        }

        public sealed class Fed
        {
            /// <summary>The stoker ran: its normalised rate went above 0.01.</summary>
            public bool Ran;
            /// <summary>Its normalised rate at the end; -1 without a stoker.</summary>
            public float Stoking;
            public float Firebox;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the livery of a loco with a Custom Car Loader mechanical stoker, or "none".
            /// <paramref name="stoker"/>: auto (the first one installed), none, or a livery id.</summary>
            [Step]
            public static string Pick(string stoker)
            {
                if (stoker != "auto") return stoker;
                var found = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.prefab != null &&
                    l.prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(m => m != null && m.GetType().Name.StartsWith("SteamMechanicalStokerDefinition")));
                return found?.id ?? "none";
            }

            /// <summary>
            /// Either peer: feeds the loco's firebox for <paramref name="feed"/> seconds without a shovel.
            /// <paramref name="real"/> (host): fires the loco up (DV's StartupHelper: coal, fire, boiler
            /// pressure) and opens its Custom Car Loader stoker (its CONTROL port to 1); the stoker then
            /// runs on every game. Otherwise writes the firebox's coal input port and the tender's coal
            /// pile port each physics step, the way a stoker does. Result: whether the stoker ran, its
            /// rate and the firebox's contents at the end.
            /// </summary>
            [Step]
            public static IEnumerator Feed(Step<Fed> step, string loco, float feed, bool real)
            {
                TrainCar car = Trains.Find(loco);
                var firebox = car.SimController.firebox ?? throw new Exception(car.ID + " has no firebox");
                var coalPile = car.trainset.cars.Select(c => c.SimController?.coalPile).FirstOrDefault(p => p != null)
                    ?? throw new Exception(car.ID + "'s trainset has no coal pile");
                object stoker = Stoker(car);
                float maxRate = 0f;
                if (real)
                {
                    if (stoker == null) throw new Exception(car.ID + " has no mechanical stoker");
                    DV.Simulation.Controllers.StartupHelper.Startup(car);
                    yield return new WaitForSeconds(2f);
                    StokerPort(stoker, "Control").ExternalValueUpdate(1f);
                    for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < feed;)
                    {
                        maxRate = Mathf.Max(maxRate, StokerPort(stoker, "StokingNormalizedReadout").Value);
                        yield return null;
                    }
                }
                else
                {
                    int i = 0;
                    for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < feed; i++)
                    {
                        firebox.fireboxCoalControlPort.Value = i % 2 == 0 ? 0.01f : 0.02f;
                        coalPile.coalConsumePort.Value = i % 2 == 0 ? 0.01f : 0.02f;
                        yield return new WaitForFixedUpdate();
                    }
                }
                Fed now = State(loco);
                now.Ran = maxRate > 0.01f;
                step.Result = now;
            }

            /// <summary>Either peer: the loco's stoker rate (normalised; -1 without a stoker) and firebox contents.</summary>
            [Step]
            public static Fed State(string loco)
            {
                TrainCar car = Trains.Find(loco);
                object stoker = Stoker(car);
                return new Fed
                {
                    Stoking = stoker == null ? -1 : (float)Math.Round(StokerPort(stoker, "StokingNormalizedReadout").Value, 3),
                    Firebox = (float)Math.Round(car.SimController.firebox.FireboxContents, 2)
                };
            }

            /// <summary>Client: shovels coal into the firebox, as the shovel does.</summary>
            [Step]
            public static void Shovel(string loco, float coal) => Trains.Find(loco).SimController.firebox.TransferCoal(coal);

            /// <summary>Client: shovels coal out of the tender, as the shovel does. Result: what it took.</summary>
            [Step]
            public static float TakeFromTender(string loco, float coal) =>
                Trains.Find(loco).trainset.cars.Select(c => c.SimController?.coalPile).First(p => p != null).TryRemoveCoal(coal);

            private static object Stoker(TrainCar car) =>
                car.SimController.SimulationFlow.OrderedSimComps.FirstOrDefault(c => c.GetType().Name == "SteamMechanicalStoker");

            // A Custom Car Loader stoker's port, by field name: the port itself or a reference holding it.
            private static LocoSim.Implementations.Port StokerPort(object stoker, string name)
            {
                object member = stoker.GetType().GetField(name).GetValue(stoker);
                return member as LocoSim.Implementations.Port
                    ?? (LocoSim.Implementations.Port)member.GetType().GetField("port", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(member);
            }
        }
    }
}
