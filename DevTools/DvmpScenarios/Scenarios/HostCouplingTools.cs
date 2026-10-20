using System;
using System.Collections;
using System.Threading.Tasks;
using DV.HUD;
using DV.MultipleUnit;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The host's own player works a coupling between two DE2s, a client watching: the MU cable on
    /// and off, both cocks closed, the hose parted and joined, the cocks opened, then the coupling
    /// menu (vanilla's UICouplingHelper) uncouples and couples, plain and advanced. After each, the
    /// client has the coupling as the host does: coupled or not, the hose, both cocks, the MU cable
    /// and whether the chain is tight. The server commits the host's hoses, cocks, MU cables and
    /// chains by watching its own world (Server/Trains/TopologyObservationPatch,
    /// NetworkedTrainCarServer.ChainStateChanged); before that the host's own client reported each
    /// as a command, which a host with no client (stage 3) doesn't have. A player
    /// host only: a dedicated host has no player to work the coupling.
    /// </summary>
    [Scenario("host-coupling-tools", "The host's player works hoses, cocks, an MU cable and the coupling menu between two DE2s: the client has the same coupling after each")]
    [Needs(Need.PlayerHost)]
    [Budget(300)]
    public sealed class HostCouplingTools : Scenario<HostCouplingTools.Options>
    {
        public sealed class Options
        {
            public string Loco = "LocoDE2";
            /// <summary>Longest wait for the client to match the host after a step, in seconds.</summary>
            public float Settle = 5f;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Train train = await pair.Host.Run<Train>(s => Cars.SpawnTrain(s, o.Loco, o.Loco, 1, 40f), 150);
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, train.Cars)));
            string a = train.Loco, b = train.Last;
            Ends ends = await pair.Host.Run(() => InGame.FacingEnds(a, b));
            Abs at = await pair.Host.Run(() => Cars.Position(a));
            await pair.Client.Run(() => Players.TeleportToGround(at.X + 4f, at.Z));
            await pair.Host.Run(() => Players.TeleportToGround(at.X - 4f, at.Z));
            await Task.Delay(2000);

            Coupling start = await Agree(pair, a, b, ends, o, "spawned coupled");
            Require.That(start.Coupled && start.Hose, "the DE2s start coupled with the hose joined", start.ToString());

            foreach (string action in new[] { "mu-on", "mu-off", "cocks-close", "hose-off", "hose-on", "cocks-open",
                         "menu-plain", "menu-advanced", "menu-advanced", "menu-plain" })
            {
                string did = await pair.Host.Run(() => InGame.Act(a, b, ends.AFront, ends.BFront, action));
                Report.Note($"host: {action}: {did}");
                Coupling c = await Agree(pair, a, b, ends, o, action);
                // The menu couples tight on every game (the mod puts right vanilla's loose chain).
                if (action.StartsWith("menu", StringComparison.Ordinal) && c.Coupled)
                    Expect.That(c.Tight, $"{action}: the chain is tight", c.ToString());
            }
        }

        // Waits for the client to have the host's coupling; one check, the last reading's.
        private static async Task<Coupling> Agree(Pair pair, string a, string b, Ends ends, Options o, string after)
        {
            Coupling host = null, client = null;
            for (float waited = 0; waited <= o.Settle; waited += 0.5f)
            {
                await Task.Delay(500);
                host = await pair.Host.Run(() => InGame.Read(a, b, ends.AFront, ends.BFront));
                client = await pair.Client.Run(() => InGame.Read(a, b, ends.AFront, ends.BFront));
                if (host.ToString() == client.ToString())
                    break;
            }
            Expect.Equal(host.ToString(), client.ToString(), $"after {after}: the client's coupling (host's expected)");
            return host;
        }

        public sealed class Ends
        {
            public bool AFront, BFront;
        }

        public sealed class Coupling
        {
            public bool Coupled, Hose, CockA, CockB, Mu, Tight;

            public override string ToString() =>
                $"coupled {Coupled}, hose {Hose}, cocks {(CockA ? "open" : "shut")}/{(CockB ? "open" : "shut")}, MU {Mu}, tight {Tight}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: which end of each car faces the other.</summary>
            [Step]
            public static Ends FacingEnds(string a, string b)
            {
                TrainCar ca = Trains.Find(a), cb = Trains.Find(b);
                Coupler end = ca.frontCoupler.coupledTo != null && ca.frontCoupler.coupledTo.train == cb ? ca.frontCoupler
                    : ca.rearCoupler.coupledTo != null && ca.rearCoupler.coupledTo.train == cb ? ca.rearCoupler
                    : throw new Exception($"{a} isn't coupled to {b}");
                return new Ends { AFront = end.isFrontCoupler, BFront = end.coupledTo.isFrontCoupler };
            }

            private static Coupler End(string car, bool front)
            {
                TrainCar c = Trains.Find(car);
                return front ? c.frontCoupler : c.rearCoupler;
            }

            /// <summary>Either game: the coupling between the two ends as this game has it.</summary>
            [Step]
            public static Coupling Read(string a, string b, bool aFront, bool bFront)
            {
                Coupler ca = End(a, aFront), cb = End(b, bFront);
                return new Coupling
                {
                    Coupled = ca.coupledTo == cb,
                    Hose = ca.GetAirHoseConnectedTo() == cb,
                    CockA = ca.IsCockOpen,
                    CockB = cb.IsCockOpen,
                    Mu = MultipleUnitModule.IsMultipleUnitCableConnected(ca.train, ca.isFrontCoupler),
                    Tight = ca.state == ChainCouplerInteraction.State.Attached_Tight ||
                            cb.state == ChainCouplerInteraction.State.Attached_Tight
                };
            }

            /// <summary>
            /// Host: one thing a player does to the coupling, through the calls vanilla makes for it
            /// (the hands on the hose, the cock handles, the MU plug, the coupling menu). Result:
            /// what it did.
            /// </summary>
            [Step]
            public static string Act(string a, string b, bool aFront, bool bFront, string action)
            {
                Coupler ca = End(a, aFront), cb = End(b, bFront);
                switch (action)
                {
                    case "mu-on":
                        MultipleUnitModule.ConnectCablesOfConnectedCouplersIfMultipleUnitSupported(ca, cb);
                        break;
                    case "mu-off":
                        MultipleUnitModule.DisconnectCablesIfMultipleUnitSupported(ca.train, ca.isFrontCoupler, !ca.isFrontCoupler);
                        break;
                    case "cocks-close":
                        ca.IsCockOpen = false;
                        cb.IsCockOpen = false;
                        break;
                    case "cocks-open":
                        ca.IsCockOpen = true;
                        cb.IsCockOpen = true;
                        break;
                    case "hose-off":
                        ca.DisconnectAirHose(true);
                        break;
                    case "hose-on":
                        ca.ConnectAirHose(cb, true);
                        break;
                    case "menu-plain":
                    case "menu-advanced":
                        // As the car's coupling menu has it: both ends set, values cached, no auto handbrake.
                        UICouplingHelper menu = new() { trainCar = ca.train, shouldAutoHandbrake = () => false };
                        menu.SetCoupler(ca.train.frontCoupler, true);
                        menu.SetCoupler(ca.train.rearCoupler, false);
                        menu.CacheValues();
                        if (ca.visualCoupler == null)
                            throw new Exception($"{a}'s coupler has no visual coupler loaded: the menu does nothing without one");
                        menu.HandleCoupling(ca, action == "menu-advanced");
                        break;
                    default:
                        throw new ArgumentException("unknown action " + action);
                }
                return $"coupled {ca.coupledTo == cb}, hose {ca.GetAirHoseConnectedTo() == cb}, " +
                       $"MU {MultipleUnitModule.IsMultipleUnitCableConnected(ca.train, ca.isFrontCoupler)}";
            }
        }
    }
}
