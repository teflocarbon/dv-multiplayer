using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using HarmonyLib;
using Multiplayer.Server.Items;
using Multiplayer.Shared.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// What happens to an item once it leaves a player's hand (playtest 2: VR hand swaps dropping
    /// items, thrown lanterns snapping back, jittering for others, and vanishing when thrown onto a
    /// train). MODE:
    /// - regrab (default): the client lets go of a mug and takes it again the next frame, as a VR
    ///   hand swap does: it must stay in the hand, and no drop reaches the server;
    /// - regrab-late: the client takes a lantern again 1 s after letting go, while the server's
    ///   copy's flame keeps changing: the pickup must still be taken;
    /// - throw-flatcar: the client throws a lantern from the ground onto a flatcar while its flame
    ///   changes on the server; the host then pushes the car. The lantern must be on the car, at
    ///   the same spot, in both games;
    /// - object-state: the client changes the wick of a lantern lying in the world: nothing is
    ///   sent, so the server corrects nothing;
    /// - interact: the client turns the knob of a lantern lying in the world (grabbing it, as a VR
    ///   hand does): the server takes the new wick, knob included.
    /// Every mode fails if the server sent the client a rejection correction for the item.
    /// </summary>
    [Scenario("item-release", "An item leaving a hand: a quick or late re-grab keeps it, a lantern thrown onto a flatcar stays on it in both games, a non-holder's wick change isn't taken, a knob turn is; no rejection corrections")]
    public sealed class ItemRelease : Scenario<ItemRelease.Options>
    {
        public sealed class Options
        {
            /// <summary>regrab, regrab-late, throw-flatcar, object-state or interact.</summary>
            public string Mode = "regrab";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await pair.Host.Run(() => InGame.WatchCorrections(true));
            pair.Cleanup(() => pair.Host.Run(() => InGame.WatchCorrections(false)));
            int clientId = await pair.Client.Run(() => Items.LocalPlayerId());
            ushort item;
            switch (o.Mode)
            {
                case "regrab":
                case "regrab-late":
                {
                    bool quick = o.Mode == "regrab";
                    item = await pair.Host.Run<ushort>(s => Items.Spawn(s, quick ? "Mug" : "Lantern", "client"));
                    Running<Flickered> flicker = quick ? null : pair.Host.Start<Flickered>(s => InGame.Flicker(s, item, 3f), 60);
                    Regrabbed r = await pair.Client.Run<Regrabbed>(s => InGame.Regrab(s, item, quick ? 0f : 1f), 90);
                    if (flicker != null) await flicker;
                    ItemRecord rec = await pair.Host.Run(() => Items.Record(item));
                    Report.Note($"client held it at r{r.HeldRevision}, still holding {r.Held}; server {rec}");
                    Expect.That(r.Held, "the client still holds it");
                    Expect.That(rec.Placement == "PlayerHand" && rec.PlacementPlayer == clientId, "server has it in the client's hand", $"{rec.Placement} (player {rec.PlacementPlayer}, the client is {clientId})");
                    if (quick) Expect.Equal(r.HeldRevision, rec.Revision, "server revision (the one held: another is the release sent as a drop)");
                    break;
                }
                case "throw-flatcar":
                {
                    // Around the host's player, as the bash scenario did (on a dedicated host, its player
                    // transform follows the client).
                    string car = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "Flatbed", "host"));
                    pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { car })));
                    item = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Lantern", "client"));
                    Report.Note($"client throws lantern {item} onto {car}");
                    var flicker = pair.Host.Start<Flickered>(s => InGame.Flicker(s, item, 3f), 60);
                    await pair.Client.Run(() => InGame.ThrowOnto(item, car), 90);
                    await flicker;
                    await CheckOnCar(pair, item, car, "landed");
                    await pair.Host.Run(() => Cars.Push(car, 5f, 4f, 3f), 90);
                    await Task.Delay(2000);   // the car's last movement reaches the client
                    await CheckOnCar(pair, item, car, "moved");
                    break;
                }
                case "object-state":
                {
                    item = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Lantern", "client"));
                    await Task.Delay(3000);   // its projection settles on the client
                    uint before = (await pair.Host.Run(() => Items.Record(item))).Revision;
                    Report.Note($"client changes the wick of lantern {item} lying in the world");
                    await pair.Client.Run(() => InGame.Nudge(item), 60);
                    uint after = (await pair.Host.Run(() => Items.Record(item))).Revision;
                    Expect.Equal(before, after, "server revision after a non-holder's wick change (a new one is its object state taken)");
                    break;
                }
                case "interact":
                {
                    item = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Lantern", "client"));
                    await Task.Delay(3000);   // its projection settles on the client
                    float wick = (await pair.Host.Run(() => InGame.ReadLantern(item))).Wick < 0.5f ? 0.85f : 0.15f;
                    Report.Note($"client turns the knob of lantern {item} lying in the world to {wick}");
                    LanternState c = await pair.Client.Run<LanternState>(s => InGame.Interact(s, item, wick), 60);
                    LanternState h = await pair.Host.Run(() => InGame.ReadLantern(item));
                    Report.Note($"client wick {c.Wick:0.##} ({c.State}); server wick {h.Wick:0.##}, knob {h.Knob:0.##}");
                    Expect.AtMost(Off(c.Wick, wick), 0.05, $"the client's own wick off {wick} (set back?)");
                    Expect.AtMost(Off(h.Wick, wick), 0.05, $"the server's wick off {wick}");
                    Expect.AtMost(Off(h.Knob, wick), 0.05, $"the server's knob off {wick} (its next movement would undo the wick)");
                    break;
                }
                default:
                    throw new ArgumentException("unknown MODE " + o.Mode);
            }
            Corrections n = await pair.Host.Run(() => InGame.ReadCorrections(item));
            Expect.Zero(n.Count, $"rejection corrections the server sent for the item ({n.Reasons})");
        }

        // How far a wick or knob is from the target, to the millimetre of travel: the knob moves in
        // steps of 0.1, and in floats 0.15 - 0.1 is 0.050000004, over the 0.05 a step off is allowed
        // (the bash version compared the decimals JSON printed).
        private static double Off(float value, float target) => Math.Round(Math.Abs(value - target), 3);

        private static async Task CheckOnCar(Pair pair, ushort item, string car, string when)
        {
            PeerResults<ItemOnCar> on = await pair.Both.Run(() => Items.OnCar(item, car));
            ItemRecord rec = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"{when}: host {on.Host.Local} client {on.Client.Local} record {rec.Placement}");
            foreach (var (peer, v) in on)
            {
                if (!Expect.That(v.Present, $"{when}: {peer}: has the lantern")) continue;
                Expect.That(v.InCar, $"{when}: {peer}: lantern on the car", v.State);
                Expect.AtLeast(v.Local.Y, 0.5, $"{when}: {peer}: lantern's height above the car's origin (m), below is under the deck");
            }
            if (on.Host.Present && on.Client.Present)
                Expect.AtMost(Point.Distance(on.Host.Local, on.Client.Local), 0.3, $"{when}: host/client positions apart (m)");
            Expect.That(rec.Placement == "TrainInterior" && rec.ParentNetId == on.Host.CarNetId, $"{when}: server record on the car", $"{rec} parent {rec.ParentKind} {rec.ParentNetId} (car {on.Host.CarNetId})");
        }

        public sealed class Regrabbed
        {
            public uint HeldRevision;
            public bool Held;
        }

        public sealed class Flickered
        {
            public int Changes;
            public uint Revisions;
        }

        public sealed class LanternState
        {
            public float Wick, Knob;
            public string State;
        }

        public sealed class Corrections
        {
            public int Count;
            public string Reasons;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string HarmonyId = "dvmp.item-release.corrections";
            private static readonly Dictionary<ushort, List<string>> corrections = new();

            /// <summary>Host: counts (true) or stops counting (false) the server's rejection
            /// corrections per item, by reason.</summary>
            [Step]
            public static void WatchCorrections(bool on)
            {
                var harmony = new Harmony(HarmonyId);
                harmony.UnpatchAll(HarmonyId);
                lock (corrections) corrections.Clear();
                if (on)
                    harmony.Patch(AccessTools.Method(typeof(NetworkedItemServer), nameof(NetworkedItemServer.SendCanonicalRejectionCorrection)),
                        postfix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(OnCorrection))));
            }

            // NetworkedItemServer.SendCanonicalRejectionCorrection(this NetworkedItem item, ServerPlayer, string)
            public static void OnCorrection(NetworkedItem item, string rejectionReason)
            {
                lock (corrections)
                {
                    if (!corrections.TryGetValue(item.NetId, out var reasons)) corrections[item.NetId] = reasons = new List<string>();
                    reasons.Add(rejectionReason ?? string.Empty);
                }
            }

            [Step]
            public static Corrections ReadCorrections(ushort id)
            {
                lock (corrections)
                {
                    var reasons = corrections.TryGetValue(id, out var r) ? r.ToList() : new List<string>();
                    return new Corrections { Count = reasons.Count, Reasons = string.Join(",", reasons.Distinct()) };
                }
            }

            /// <summary>
            /// Host: once the item is let go, keeps changing its lantern's wick on the server's copy
            /// for <paramref name="seconds"/>, as a lantern's flame changes on its own. Each change is
            /// the server's object state and advances the item's revision while the item is in
            /// flight or lying in the world.
            /// </summary>
            [Step]
            public static IEnumerator Flicker(Step<Flickered> step, ushort id, float seconds)
            {
                NetworkedItem.TryGet(id, out NetworkedItem item);
                var lantern = item?.GetComponent<Lantern>() ?? throw new Exception("item " + id + " is not a lantern");
                bool Held() => AuthoritativeItemRegistry.TryGet(id, out var r) && r.Placement == Multiplayer.Shared.Protocol.Data.Items.ItemPlacementKind.PlayerHand;
                float deadline = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < deadline && !Held()) yield return null;   // picked up
                while (Time.realtimeSinceStartup < deadline && Held()) yield return null;    // and let go
                if (Time.realtimeSinceStartup >= deadline) throw new Exception("item " + id + " was never picked up and let go");
                uint from = item.AuthorityRevision;
                int changes = 0;
                for (float end = Time.realtimeSinceStartup + seconds; Time.realtimeSinceStartup < end; changes++)
                {
                    lantern.UpdateWickRelatedLogic(changes % 2 == 0 ? 0.3f : 0.7f);
                    yield return new WaitForSecondsRealtime(0.1f);
                }
                step.Result = new Flickered { Changes = changes, Revisions = item.AuthorityRevision - from };
            }

            /// <summary>Client: picks the item up, lets go, and takes it again <paramref name="delay"/>
            /// seconds later (0: the next frame, by force-hold, as a VR hand swap does). Reports the
            /// server revision seen while held before letting go.</summary>
            [Step]
            public static IEnumerator Regrab(Step<Regrabbed> step, ushort id, float delay)
            {
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                yield return new WaitForSecondsRealtime(1.5f);   // the pickup is acknowledged
                uint heldRevision = item.AuthorityRevision;
                yield return Hands.Drop();
                if (delay <= 0f)
                {
                    var handler = item.GetComponent<DV.Interaction.GrabHandlerItem>();
                    Hands.Grabber.GetComponent<DV.Interaction.GrabberInteractionHandlerDV>().RequestForceHold(handler);
                    yield return null;
                }
                else
                {
                    yield return new WaitForSecondsRealtime(delay);
                    yield return Hands.Pickup(id);
                }
                yield return new WaitForSecondsRealtime(2.5f);
                step.Result = new Regrabbed { HeldRevision = heldRevision, Held = Hands.Held != null && Hands.Held.NetId == id };
            }

            /// <summary>
            /// Client: picks the lantern up, stands on the ground beside the car and throws it onto
            /// the deck. Hands.Throw does the real release (a Thrown transition; this game simulates
            /// the flight), then the body is put over the deck so it lands there whatever DV's throw
            /// force is.
            /// </summary>
            [Step]
            public static IEnumerator ThrowOnto(ushort id, string carId)
            {
                var car = Trains.Find(carId);
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                Vector3 side = Dev.Abs(car.transform.position + car.transform.right * 3.5f);
                yield return LocalPlayer.TeleportToGround(side.x, side.z);
                yield return new WaitForSecondsRealtime(1f);
                Vector3 deck = Trains.FloorPoint(car);
                yield return Hands.Throw((deck + Vector3.up * 2f - PlayerManager.ActiveCamera.transform.position).normalized);
                Rigidbody body = item.Item.ItemRigidbody;
                body.position = deck + Vector3.up * 1.2f;
                body.velocity = Vector3.down * 0.5f;
                body.angularVelocity = Vector3.zero;
                yield return new WaitForSecondsRealtime(5f);
            }

            /// <summary>Client: changes the wick of a lantern lying in the world, without picking it
            /// up. Only its holder may change an item's object state, so nothing should reach the server.</summary>
            [Step]
            public static IEnumerator Nudge(ushort id)
            {
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                var lantern = item.GetComponent<Lantern>() ?? throw new Exception("item " + id + " is not a lantern");
                yield return new WaitForSecondsRealtime(2f);   // its projection has settled
                for (int i = 0; i < 10; i++)
                {
                    lantern.UpdateWickRelatedLogic(i % 2 == 0 ? 0.2f : 0.8f);
                    yield return new WaitForSecondsRealtime(0.1f);
                }
                yield return new WaitForSecondsRealtime(2f);
            }

            /// <summary>Client: turns the knob of a lantern lying in the world, as a VR hand does:
            /// grabs the knob (DV's own grab event, which opens the interaction), sets it, lets go.</summary>
            [Step]
            public static IEnumerator Interact(Step<LanternState> step, ushort id, float wick)
            {
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                var lantern = item.GetComponent<Lantern>() ?? throw new Exception("item " + id + " is not a lantern");
                yield return new WaitForSecondsRealtime(2f);   // its projection has settled
                var knob = lantern.knob ?? throw new Exception("lantern " + id + " has no knob");
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                typeof(DV.CabControls.ControlImplBase).GetMethod("FireGrabbed", flags).Invoke(knob, null);
                for (int i = 0; i < 5; i++)
                {
                    knob.SetValue(Mathf.Lerp(lantern.wickSize, wick, 0.5f));
                    yield return new WaitForSecondsRealtime(0.1f);
                }
                knob.SetValue(wick);
                yield return new WaitForSecondsRealtime(0.2f);
                typeof(DV.CabControls.ControlImplBase).GetMethod("FireUngrabbed", flags).Invoke(knob, null);
                yield return new WaitForSecondsRealtime(3f);
                step.Result = new LanternState { Wick = lantern.wickSize, State = item.GetItemState().ToString() };
            }

            /// <summary>This game's lantern: its wick and where its knob is.</summary>
            [Step]
            public static LanternState ReadLantern(ushort id)
            {
                NetworkedItem.TryGet(id, out NetworkedItem item);
                var lantern = item.GetComponent<Lantern>();
                return new LanternState { Wick = lantern.wickSize, Knob = lantern.knob.Value, State = item.GetItemState().ToString() };
            }
        }
    }
}
