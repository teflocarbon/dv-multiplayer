using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// One lever, both players in the DE2's cab (#73). The client holds it and turns it: the host's
    /// cab has it blocked, the host can't take it, and gets the client's value. Then the reverse: the
    /// host holds it (its request handled in-process) and turns it, the client's cab has it blocked,
    /// can't take it, and gets the host's value. No lease is left held. Needs the host's player, so
    /// a player host. Was cab-controls MODE=authority.
    /// </summary>
    [Scenario("cab-controls-authority", "One lever, both players in the cab: whoever holds it has its lease, the other's cab sees it blocked, can't take it and gets the holder's value; no lease left held")]
    [Needs(Need.PlayerHost)]
    [Budget(600)]
    public sealed class CabControlsAuthority : Scenario<CabControlsAuthority.Options>
    {
        public sealed class Options
        {
            public string Control = "C_Wipers";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLoco(s, "LocoDE2", true, false));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            pair.Cleanup(async () =>
            {
                await pair.Client.Run(() => Players.TeleportToGround(7190f, 6880f));
                await pair.Host.Run(() => Players.TeleportToGround(7194f, 6873f));
            });
            await pair.Host.Run(() => Players.Board(loco.Id));
            await Task.Delay(2000);   // the cab settles after boarding, as before
            await pair.Client.Run(() => Players.Board(loco.Id));
            await Task.Delay(4000);

            string control = o.Control;
            Task<Lever> Grab(Peer peer, float value = -1) => peer.Run<Lever>(s => InGame.Grab(s, loco.Id, control, value), 30);
            Task<Lever> Release(Peer peer) => peer.Run<Lever>(s => InGame.Release(s, loco.Id, control), 30);
            Task<Holder> Holder() => pair.Host.Run(() => InGame.Holder(loco.Id, control));
            static bool Near(float a, float b) => Math.Abs(a - b) < 0.01;

            Report.Note($"== the client holds {control}");
            Lever c = await Grab(pair.Client, 0.75f);
            Expect.That(c.Owned && !c.Blocked, "client grabs and turns it", c.ToString());
            Holder h = await Holder();
            Expect.That(h.Who != "none" && h.Who != "host" && h.Blocked && Near(h.Value, c.Value),
                "host: the client holds it, blocked in the host's cab, at the client's value", h.ToString());
            Lever hl = await Grab(pair.Host);
            Expect.That(!hl.Owned && hl.Blocked, "host grabs it: no lease", hl.ToString());
            h = await Holder();
            Expect.That(h.Who != "none" && h.Who != "host", "host: still the client's", h.ToString());
            await Release(pair.Host);
            c = await Release(pair.Client);
            Expect.That(!c.Owned, "client lets go", c.ToString());
            h = await Holder();
            Expect.That(h.Who == "none" && !h.Blocked && h.Leases == 0, "host: nobody holds it, not blocked", h.ToString());

            Report.Note($"== the host holds {control}");
            hl = await Grab(pair.Host, 0.25f);
            Expect.That(hl.Owned && !hl.Blocked, "host grabs and turns it", hl.ToString());
            float hostValue = hl.Value;
            h = await Holder();
            Expect.That(h.Who == "host", "host: the host's player holds it", h.ToString());
            c = await Grab(pair.Client);
            Expect.That(!c.Owned && c.Blocked && Near(c.Value, hostValue), "client grabs it: no lease, blocked, at the host's value", c.ToString());
            h = await Holder();
            Expect.That(h.Who == "host", "host: still the host's", h.ToString());
            await Release(pair.Client);
            hl = await Release(pair.Host);
            Expect.That(!hl.Owned, "host lets go", hl.ToString());
            h = await Holder();
            Expect.That(h.Who == "none" && h.Leases == 0, "host: nobody holds it", h.ToString());
            c = await Release(pair.Client);
            Expect.That(!c.Blocked, "client: not blocked", c.ToString());
        }

        public sealed class Lever
        {
            public string Port;
            public float Value;
            /// <summary>This game holds the port's lease.</summary>
            public bool Owned;
            /// <summary>This game sees it blocked by someone else's lease.</summary>
            public bool Blocked;
            public override string ToString() => $"{Port} {Value.ToString(CultureInfo.InvariantCulture)}, owned {Owned}, blocked {Blocked}";
        }

        public sealed class Holder
        {
            /// <summary>"none", "host" for the host's own player, else the player's name.</summary>
            public string Who;
            public float Value;
            public bool Blocked;
            public int Leases;
            public override string ToString() => $"held by {Who}, value {Value.ToString(CultureInfo.InvariantCulture)}, blocked {Blocked}, {Leases} lease(s)";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Either side: grabs a cab control as a hand or the mouse does (the game's Grabbed
            /// event) and, given a <paramref name="value"/> of 0 or more, turns it there, and keeps
            /// holding. Result: its port, this game's value for it, and whether this game holds its lease
            /// and sees it blocked by someone else's.</summary>
            [Step]
            public static IEnumerator Grab(Step<Lever> step, string loco, string control, float value)
            {
                var c = Control(loco, control);
                c.FireGrabbed();
                yield return new WaitForSecondsRealtime(0.5f);
                if (value >= 0)
                {
                    c.SetValue(value);
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                yield return new WaitForSecondsRealtime(1.5f);   // the lease request answered, as before
                step.Result = Read(loco, c);
            }

            /// <summary>Either side: lets go of the control. Result: as <see cref="Grab"/>.</summary>
            [Step]
            public static IEnumerator Release(Step<Lever> step, string loco, string control)
            {
                var c = Control(loco, control);
                c.FireUngrabbed();
                // A sprung control is let go once it has settled (polled every 0.5 s), then the lease goes.
                yield return new WaitForSecondsRealtime(3f);
                step.Result = Read(loco, c);
            }

            /// <summary>Host: who holds the lease on the control's port, the host's value for it,
            /// whether the host's cab sees it blocked, and how many leases the loco has.</summary>
            [Step]
            public static Holder Holder(string loco, string control)
            {
                var ntc = Trains.Find(loco).GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>();
                string port = Control(loco, control).GetComponent<DV.Simulation.Ports.InteractablePortFeeder>().portId;
                uint netId = Multiplayer.Shared.Trains.NetworkedTrainCar.GetPortNetId(port);
                var owner = Multiplayer.Server.Trains.NetworkedTrainCarServer.Of(ntc).LeaseHolder(netId);
                return new Holder
                {
                    Who = owner == null ? "none" : owner == Multiplayer.Server.Session.NetworkServer.Instance.LocalPlayer ? "host" : owner.Username,
                    Value = ntc.simulationFlow.TryGetPort(port, out var p) ? (float)Math.Round(p.Value, 3) : -1,
                    Blocked = Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(ntc).portNetIdToBlockState.TryGetValue(netId, out bool b) && b,
                    Leases = Multiplayer.Server.Trains.NetworkedTrainCarServer.Of(ntc).LeaseCount
                };
            }

            private static DV.CabControls.ControlImplBase Control(string loco, string control) =>
                Trains.Find(loco).loadedInterior.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true).First(x => x.name == control);

            private static Lever Read(string loco, DV.CabControls.ControlImplBase c)
            {
                var ntc = Trains.Find(loco).GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>();
                var client = Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(ntc);
                string port = c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>().portId;
                uint netId = Multiplayer.Shared.Trains.NetworkedTrainCar.GetPortNetId(port);
                return new Lever
                {
                    Port = port,
                    Value = ntc.simulationFlow.TryGetPort(port, out var p) ? (float)Math.Round(p.Value, 3) : -1,
                    Owned = client.locallyOwnedPortAuthorities.Contains(netId),
                    Blocked = client.portNetIdToBlockState.TryGetValue(netId, out bool b) && b
                };
            }
        }
    }
}
