using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Cab controls from a client (#73). For each loco: every control that feeds a sim port is
    /// hooked, so the client can take its lease; every toggle switch flipped with Use() (what a VR
    /// hand does, it never grabs) reaches the host, and flipping back does too; on the DE2 a grabbed
    /// wipers turn reaches the host. On the S282 and the DE6, valves and windows set with no grab
    /// and no Use() (a VR hand turning a valve, a click on a window) reach the host too: they take no
    /// lease. No control lease is left held afterwards. LOCOS="LocoS282A LocoDE6" covers the controls
    /// set without a grab. Was cab-controls/run.sh; its other modes are cab-controls-authority,
    /// cab-controls-no-grab and cab-reload.
    /// </summary>
    [Scenario("cab-controls", "The client's cab controls reach the host: every port-fed control hooked, toggles flipped with Use() and back, valves and windows set without a grab, a grabbed lever turn; no lease left held")]
    [Budget(900)]
    public sealed class CabControls : Scenario<CabControls.Options>
    {
        public sealed class Options
        {
            public string[] Locos = { "LocoDE2", "LocoDM3" };
        }

        protected override async Task Run(Pair pair, Options o)
        {
            foreach (string type in o.Locos)
            {
                Report.Note("== " + type);
                // Held still: a loco left free rolls away on a grade and leaves the client too far
                // away for the host to take its changes.
                HeldLoco loco = await pair.Host.Run<HeldLoco>(s => Cars.SpawnHeldLoco(s, type), 150);
                bool deleted = false;
                Func<Task> delete = async () =>
                {
                    if (deleted) return;
                    deleted = true;
                    await pair.Client.Run(() => Players.TeleportToGround(7190f, 6880f));
                    await pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars));
                };
                pair.Cleanup(delete);
                await pair.Client.Run(() => Players.Board(loco.Loco));
                await Task.Delay(4000);   // the cab settles after boarding, as before

                CabReload.Hooked h = await pair.Client.Run(() => CabReload.InGame.Hooked(loco.Loco));
                Report.Note($"{h.Feeders} port-fed controls");
                Expect.Empty(h.Unhooked, $"{type}: unhooked controls");

                PortValue[] on = await pair.Client.Run<PortValue[]>(s => InGame.Toggle(s, loco.Loco), 60);
                await Cabs.ExpectHostAgrees(pair, loco.Loco, on, $"{type}: toggle switches flipped");
                PortValue[] off = await pair.Client.Run<PortValue[]>(s => InGame.Toggle(s, loco.Loco), 60);
                await Cabs.ExpectHostAgrees(pair, loco.Loco, off, $"{type}: and flipped back");

                // Controls that never ask for a lease; the host refused them all before 2026-10-17.
                string[] unleased = type switch
                {
                    "LocoS282A" => new[] { "compressorControl.EXT_IN", "dynamoControl.EXT_IN", "cylinderCock.EXT_IN", "lubricatorControl.EXT_IN" },
                    "LocoDE6" => new[] { "windowL1.EXT_IN", "windowR1.EXT_IN" },
                    _ => new string[0]
                };
                if (unleased.Length > 0)
                {
                    PortValue[] set = await pair.Client.Run<PortValue[]>(s => InGame.Set(s, loco.Loco, unleased, 1f), 30);
                    await Cabs.ExpectHostAgrees(pair, loco.Loco, set, $"{type}: set without a grab");
                    set = await pair.Client.Run<PortValue[]>(s => InGame.Set(s, loco.Loco, unleased, 0f), 30);
                    await Cabs.ExpectHostAgrees(pair, loco.Loco, set, $"{type}: and set back");
                }

                if (type == "LocoDE2")
                {
                    PortValue[] wipers = await pair.Client.Run<PortValue[]>(s => InGame.GrabTurn(s, loco.Loco, "C_Wipers", 0.75f), 30);
                    await Cabs.ExpectHostAgrees(pair, loco.Loco, wipers, $"{type}: wipers grabbed and turned");
                }

                await delete();
            }
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: flips every toggle switch in the cab with Use(), which is all a VR hand
            /// does (it never raises Grabbed). Result: every port the switches feed (one switch can feed
            /// several) and the client's value. Run twice to flip back.</summary>
            [Step]
            public static IEnumerator Toggle(Step<PortValue[]> step, string loco)
            {
                TrainCar car = Trains.Find(loco);
                var switches = car.loadedInterior.GetComponentsInChildren<DV.CabControls.ToggleSwitchBase>(true)
                    .Select(s => (control: s, ports: s.GetComponents<DV.Simulation.Ports.InteractablePortFeeder>().Select(f => f.portId).Where(id => !string.IsNullOrEmpty(id)).ToList()))
                    .Where(s => s.ports.Count > 0).ToList();
                foreach (var s in switches)
                {
                    s.control.Use();
                    yield return new WaitForSecondsRealtime(0.1f);
                }
                yield return new WaitForSecondsRealtime(2f);   // settling, as before
                step.Result = Cabs.Read(car, switches.SelectMany(s => s.ports));
            }

            /// <summary>
            /// Client: sets the cab controls feeding these ports straight to a value, with no grab and
            /// no Use(): all the mod sees of a VR hand turning a valve, or of a click on a cab window.
            /// One control per port: the cab's if it has one, else the car body's (the DE6's windows,
            /// the S282's oiling points: the external interactables, which are under the car's interior
            /// transform, not under the car), never their dummy copy. Setting a second control of a
            /// port the first already moved is no change, so nothing would be sent for it, and the port
            /// could end up moved by the one the player doesn't use. Result: each port and the client's
            /// value once it has settled.
            /// </summary>
            [Step]
            public static IEnumerator Set(Step<PortValue[]> step, string loco, string[] ports, float value)
            {
                TrainCar car = Trains.Find(loco);
                var controls = new[] { car.loadedInterior, car.loadedExternalInteractables, car.gameObject }.Where(root => root != null)
                    .SelectMany(root => root.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true))
                    .Where(c => car.loadedDummyExternalInteractables == null || !c.transform.IsChildOf(car.loadedDummyExternalInteractables.transform))
                    .Select(c => (control: c, port: c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>()?.portId))
                    .Where(x => x.port != null && ports.Contains(x.port)).GroupBy(x => x.port).Select(g => g.First()).ToList();
                if (controls.Count == 0) throw new Exception("no cab control feeds " + string.Join(",", ports));
                foreach (var x in controls)
                {
                    x.control.SetValue(value);
                    yield return new WaitForSecondsRealtime(0.2f);
                }
                yield return new WaitForSecondsRealtime(2.5f);   // settling, as before
                step.Result = Cabs.Read(car, controls.Select(x => x.port));
            }

            /// <summary>Client: grabs a cab control (the game's own Grabbed event, as a hand or the mouse
            /// raises it), turns it to a value, lets go. Result: its port and the client's value once
            /// it has settled.</summary>
            [Step]
            public static IEnumerator GrabTurn(Step<PortValue[]> step, string loco, string control, float value)
            {
                TrainCar car = Trains.Find(loco);
                var c = car.loadedInterior.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true).First(x => x.name == control);
                c.FireGrabbed();
                yield return new WaitForSecondsRealtime(0.3f);
                c.SetValue(value);
                yield return new WaitForSecondsRealtime(0.5f);
                c.FireUngrabbed();
                yield return new WaitForSecondsRealtime(2.5f);   // released and settled, as before
                step.Result = Cabs.Read(car, new[] { c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>().portId });
            }
        }
    }
}
