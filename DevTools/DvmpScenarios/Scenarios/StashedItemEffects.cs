using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using DV.TimeKeeping;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// What a stashed item still does as a row (docs/design/items/inventory.md, "Stashed items that
    /// act"; D14, OI-COLD-11 and OI-COLD-12). A player sets an alarm clock of theirs in the hand and
    /// stashes it: it's a stand-in, the HUD still counts it as the player's clock, and when the
    /// host skips the world's clock past its time it rings (a copy of its ring sound plays at the
    /// player) and is unset in the stand-in's row and in the server's. Taken out, the clock is unset
    /// too. The player then turns a boombox on in radio mode at a volume of its own (not vanilla's
    /// default) and stashes it: its radio plays from the row at that volume, stops as it's taken out,
    /// and the boombox's own volume is still the row's two seconds later. WHO=host does it with the
    /// host's own player (a player host only). The items are destroyed at the end.
    /// </summary>
    [Scenario("stashed-items", "A stashed alarm clock keeps the HUD time and rings from its row; a stashed boombox plays its radio from its row; both come back as they were")]
    [Budget(400)]
    public sealed class StashedItemEffects : Scenario<StashedItemEffects.Options>
    {
        public sealed class Options
        {
            public string Clock = "Stopwatch";
            public string Boombox = "Boombox";
            /// <summary>The boombox's volume, one vanilla's default (0.4) isn't.</summary>
            public float Volume = 0.3f;
            /// <summary>Whose inventory: "client", or "host" (the host's own player; needs a player host).</summary>
            public string Who = "client";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            if (host && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = host ? pair.Host : pair.Client;
            ushort clock = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Clock, o.Who));
            ushort boombox = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Boombox, o.Who));
            pair.Cleanup(async () => await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { clock, boombox }), 30));

            Clock set = await who.Run<Clock>(s => InGame.SetAlarmAndStash(s, clock), 90);
            Report.Note($"alarm clock {clock}: {set}");
            Expect.That(set.StandIn, "the alarm clock is a stand-in in its slot");
            Expect.That(set.StandInState.Contains("\"alarmSet\":true"), $"its row has the alarm set: {set.StandInState}");
            Expect.That(set.Mine, "the stashed clock is the player's");
            Expect.That(set.HasClock, $"the HUD counts the player's clock with it stashed (clock objects registered: {set.ClockObjects})");

            await pair.Host.Run(() => InGame.SkipTime(set.DueInMinutes + 2));
            Clock rang = await who.Run<Clock>(s => InGame.AfterRing(s, clock), 60);
            Report.Note($"after the skip: {rang}");
            Expect.That(rang.StandIn, "still a stand-in after it rang");
            Expect.That(rang.StandInState.Contains("\"alarmSet\":false"), $"its row has the alarm unset: {rang.StandInState}");
            Expect.That(rang.RingPlayed, "its ring played at the player");
            string serverRow = await pair.Host.Run(() => InGame.ServerRowState(clock));
            Expect.That(serverRow.Contains("\"alarmSet\":false"), $"the server's row has the alarm unset: {serverRow}");
            Clock taken = await who.Run<Clock>(s => InGame.TakeOutClock(s, clock), 60);
            Report.Note($"taken out: {taken}");
            Expect.That(!taken.AlarmSetOnObject, "taken out, the clock's alarm is unset (it doesn't ring again)");

            Radio radio = await who.Run<Radio>(s => InGame.RadioOnAndStash(s, boombox, o.Volume), 90);
            Report.Note($"boombox {boombox}: {radio}");
            Expect.That(radio.StandIn, "the boombox is a stand-in in its slot");
            Expect.That(radio.StashedRadioPlaying, "its radio plays from the row");
            Expect.That(Math.Abs(o.Volume - radio.StashedVolume) < 0.001f, "the stashed radio's volume", $"{radio.StashedVolume}, expected {o.Volume}");
            Radio outRadio = await who.Run<Radio>(s => InGame.TakeOutRadio(s, boombox), 60);
            Report.Note($"boombox taken out: {outRadio}");
            Expect.That(!outRadio.StashedRadioPlaying, "the row's radio stops as the boombox is taken out");
            Expect.That(outRadio.ObjectOn, "the boombox is on in radio mode in the hand");
            Expect.That(Math.Abs(o.Volume - outRadio.ObjectVolume) < 0.001f, "the boombox's volume two seconds after the take-out", $"{outRadio.ObjectVolume}, expected {o.Volume}");
        }

        public sealed class Clock
        {
            public bool StandIn, Mine, HasClock, RingPlayed, AlarmSetOnObject;
            public int ClockObjects;
            public int DueInMinutes;
            public string StandInState = "";

            public override string ToString() =>
                $"stand-in {StandIn}, mine {Mine}, HUD clock {HasClock} ({ClockObjects} objects), due in {DueInMinutes} min, " +
                $"ring played {RingPlayed}, alarm set on the object {AlarmSetOnObject}, row {StandInState}";
        }

        public sealed class Radio
        {
            public bool StandIn, StashedRadioPlaying, ObjectOn;
            public float StashedVolume = -1f, ObjectVolume = -1f;

            public override string ToString() =>
                $"stand-in {StandIn}, stashed radio {StashedRadioPlaying} at {StashedVolume}, object on {ObjectOn} at {ObjectVolume}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string SoundRoot = "[MP] Stashed item sounds";

            /// <summary>Picks the clock up, sets its alarm a little ahead and stashes it.</summary>
            [Step]
            public static IEnumerator SetAlarmAndStash(Step<Clock> step, ushort id)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                AnalogAlarmClock alarm = item.GetComponentInChildren<AnalogAlarmClock>(true)
                    ?? throw new Exception($"item {id} isn't an alarm clock");
                for (int i = 0; i < 10; i++) yield return null;   // its listeners start at the end of its first frame
                DateTime now = AlarmRows.Now ?? throw new Exception("the world's clock isn't running");
                // The next 12-minute mark at least 13 minutes on (the alarm's step), on the clock's face.
                int face = now.Hour % 12 * 60 + now.Minute;
                alarm.alarmTimeInMinutes = (face + 13 + 11) / 12 * 12 % 720;
                alarm.SetAlarmTime();
                if (!alarm.alarmSet) alarm.OnButtonPressed();
                int due = (int)Math.Ceiling((alarm.alarmTime - now).TotalMinutes);
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
                yield return Wait.Until(() => PlayerInventory.Holds(id), 5f, "the stash");
                yield return new WaitForSecondsRealtime(1.5f);
                Hands.ReleaseControl();
                step.Result = Read(id, due);
            }

            /// <summary>Host: skips the world's clock on, for every game (as /time does).</summary>
            [Step]
            public static void SkipTime(int minutes) =>
                Multiplayer.Server.Session.NetworkServer.Instance.SendTimeAdvance(minutes * 60f);

            /// <summary>Waits for the clock's row to ring, and reads it.</summary>
            [Step]
            public static IEnumerator AfterRing(Step<Clock> step, ushort id)
            {
                bool played = false;
                float until = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < until && !(played && StandInState(id).Contains("\"alarmSet\":false")))
                {
                    GameObject root = GameObject.Find(SoundRoot);
                    played |= root != null && root.GetComponentsInChildren<AudioSource>().Any(source => source.isPlaying);
                    yield return null;
                }
                Clock clock = Read(id, 0);
                clock.RingPlayed = played;
                step.Result = clock;
            }

            /// <summary>Host: the server's row of a data-only item's state, "" for none.</summary>
            [Step]
            public static string ServerRowState(ushort id) =>
                LiveItems.TryGet(id, out LiveItems.LiveItem record) && ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow row) && row.State != null
                    ? row.State.ToString(Newtonsoft.Json.Formatting.None) : "";

            /// <summary>Takes the clock out to the hand and reads its alarm, then stashes it again.</summary>
            [Step]
            public static IEnumerator TakeOutClock(Step<Clock> step, ushort id)
            {
                yield return PlayerInventory.TakeOut(id);
                for (int i = 0; i < 10; i++) yield return null;
                NetworkedItem.TryGet(id, out NetworkedItem item);
                Clock clock = Read(id, 0);
                clock.AlarmSetOnObject = item.GetComponentInChildren<AnalogAlarmClock>(true).alarmSet;
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
                yield return Wait.Until(() => PlayerInventory.Holds(id), 5f, "the stash");
                Hands.ReleaseControl();
                step.Result = clock;
            }

            /// <summary>Picks the boombox up, turns it on in radio mode at a volume, and stashes it.</summary>
            [Step]
            public static IEnumerator RadioOnAndStash(Step<Radio> step, ushort id, float volume)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                BoomboxInteractionController boombox = item.GetComponent<BoomboxInteractionController>();
                yield return Wait.Until(() => boombox.initialized, 5f, "the boombox to start");
                // As its knobs do.
                boombox.modeKnobControl.SetValue(1f);
                boombox.volumeKnobControl.SetValue(volume);
                boombox.powerKnobControl.SetValue(1f);
                yield return new WaitForSecondsRealtime(1.5f);
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
                yield return Wait.Until(() => PlayerInventory.Holds(id), 5f, "the stash");
                yield return new WaitForSecondsRealtime(1.5f);
                Hands.ReleaseControl();
                step.Result = ReadRadio(id);
            }

            /// <summary>Takes the boombox out to the hand, and reads it two seconds later.</summary>
            [Step]
            public static IEnumerator TakeOutRadio(Step<Radio> step, ushort id)
            {
                yield return PlayerInventory.TakeOut(id);
                yield return new WaitForSecondsRealtime(2f);
                Radio radio = ReadRadio(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                BoomboxInteractionController boombox = item.GetComponent<BoomboxInteractionController>();
                radio.ObjectOn = boombox.IsPoweredOn && boombox.IsInRadioMode;
                radio.ObjectVolume = boombox.volumeAndInterferenceController.GetVolume();
                Hands.ReleaseControl();
                step.Result = radio;
            }

            private static string StandInState(ushort id) =>
                InventoryStandIn.TryGet(id, out InventoryStandIn standIn) ? standIn.State : "";

            private static Clock Read(ushort id, int due)
            {
                InventoryStandIn.TryGet(id, out InventoryStandIn standIn);
                WorldClockController world = WorldClockController.Instance;
                return new Clock
                {
                    StandIn = standIn != null,
                    Mine = standIn != null && standIn.Relation == ItemRelation.Mine,
                    HasClock = world != null && world.PlayerHasClock,
                    ClockObjects = world != null ? world.playerOwnedClocks.Count : -1,
                    DueInMinutes = due,
                    StandInState = StandInState(id)
                };
            }

            private static Radio ReadRadio(ushort id)
            {
                GameObject stashed = GameObject.Find($"{SoundRoot}/[MP] Stashed boombox {id}");
                AudioSource source = stashed != null ? stashed.GetComponent<AudioSource>() : null;
                return new Radio
                {
                    StandIn = InventoryStandIn.TryGet(id, out _),
                    StashedRadioPlaying = source != null,
                    StashedVolume = source != null ? source.volume : -1f
                };
            }
        }
    }
}
