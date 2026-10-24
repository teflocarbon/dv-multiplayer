using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using DV.Items;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A stashed boombox plays on from its row, and the players near its holder hear it
    /// (docs/design/items/inventory.md, "Stashed items that act"; OI-CON-16, R5 C10). A player loads
    /// a cassette into a boombox, turns it on in cassette mode at a volume of its own, plays it and
    /// stashes it: the boombox is a stand-in with the cassette nested in its row, the row keeps where
    /// the cassette had got to, and the stand-in's music plays on. The other player comes near the
    /// holder: their game plays the holder's stashed boombox at the holder's avatar. The holder takes
    /// it out a few seconds later: the row's music stops, and the boombox plays its cassette on from
    /// the same track, no earlier than the row's player had got to; the other game's copy of the
    /// boombox plays too. MODE=radio does it in radio mode (no cassette, no position). WHO=host does it
    /// with the host's own player (a player host only). A dedicated host has no player to hear a
    /// client's boombox, so there it checks the holder's half only.
    /// </summary>
    [Scenario("stashed-cassette", "A stashed boombox plays its cassette on from its row, a nearby player hears it, and a take-out resumes where it had got to")]
    [Budget(300)]
    public sealed class StashedCassettePlays : Scenario<StashedCassettePlays.Options>
    {
        public sealed class Options
        {
            public string Boombox = "Boombox";
            /// <summary>"cassette", or "radio".</summary>
            public string Mode = "cassette";
            /// <summary>The boombox's volume, one vanilla's default (0.4) isn't.</summary>
            public float Volume = 0.3f;
            /// <summary>How long it plays stashed before the take-out, in seconds.</summary>
            public int Stashed = 6;
            /// <summary>Whose inventory: "client", or "host" (the host's own player; needs a player host).</summary>
            public string Who = "client";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            bool radio = o.Mode == "radio";
            if (host && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = host ? pair.Host : pair.Client;
            // The player who should hear it: the other one. A dedicated host has none.
            Peer listener = host ? pair.Client : pair.Dedicated ? null : pair.Host;
            string cassettePrefab = (await pair.Host.Run(() => StashState.InGame.Magazines()))
                .Select(m => m.Split('/')).Where(m => m[0] == o.Boombox).Select(m => m[1]).FirstOrDefault()
                ?? throw new HarnessException($"no cassette fits {o.Boombox}");
            ushort boombox = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Boombox, o.Who));
            ushort cassette = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, cassettePrefab, o.Who));
            pair.Cleanup(async () => await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { boombox, cassette }), 30));

            Music stashed = await who.Run<Music>(s => InGame.PlayAndStash(s, boombox, cassette, radio, o.Volume), 120);
            Report.Note($"boombox {boombox} with {cassettePrefab} {cassette}, stashed: {stashed}");
            Expect.That(stashed.StandIn, "the boombox is a stand-in in its slot");
            Expect.That(stashed.RowPlaying, "its music plays from the row");
            Expect.That(Math.Abs(o.Volume - stashed.Volume) < 0.001f, "the row's music plays at the boombox's volume", $"{stashed.Volume}, expected {o.Volume}");
            if (!radio)
            {
                Expect.That(stashed.Nested, $"the cassette is nested in its row: {stashed.State}");
                Expect.That(stashed.State.Contains("\"cassettePlaying\":true") && stashed.State.Contains("\"mpCassetteSeek\""),
                    $"its row keeps the cassette playing, and where it had got to: {stashed.State}");
            }

            if (listener != null)
            {
                bool heard = await listener.Run<bool>(s => InGame.HearNear(s, stashed.HolderAt, boombox, stashed.Holder), 60);
                Expect.That(heard, "the other player near the holder hears the stashed boombox at the holder");
            }
            else
                Report.Note("a dedicated host has no player to hear a client's stashed boombox: the hearing checks are skipped");

            await Task.Delay(o.Stashed * 1000);
            Music taken = await who.Run<Music>(s => InGame.TakeOut(s, boombox), 60);
            Report.Note($"taken out: {taken}");
            Expect.That(!taken.RowPlaying, "the row's music stops as the boombox is taken out");
            Expect.That(taken.ObjectPlaying, radio ? "the boombox plays its radio in the hand" : "the boombox plays its cassette in the hand");
            if (!radio)
            {
                Expect.Equal(taken.RowTrack, taken.ObjectTrack, "the cassette resumes on the track the row's player was on");
                Expect.That(taken.ObjectSeek >= taken.RowSeek, "the cassette resumes no earlier than the row's player had got to",
                    $"{taken.ObjectSeek}, the row's {taken.RowSeek}");
            }
            if (listener != null)
            {
                bool copy = await listener.Run<bool>(s => InGame.CopyPlays(s, boombox, radio), 60);
                Expect.That(copy, "the other player's copy of the boombox plays once it's out");
            }
        }

        public sealed class Music
        {
            public bool StandIn, Nested, RowPlaying, ObjectPlaying;
            public float Volume = -1f;
            public int RowTrack = -1, ObjectTrack = -1;
            public long RowSeek = -1, ObjectSeek = -1;
            public byte Holder;
            public Abs HolderAt;
            public string State = "";

            public override string ToString() =>
                $"stand-in {StandIn}, nested {Nested}, row's music {RowPlaying} at {Volume} (track {RowTrack}, {RowSeek}), " +
                $"object's {ObjectPlaying} (track {ObjectTrack}, {ObjectSeek}), holder P{Holder} at {HolderAt}, row {State}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string SoundRoot = "[MP] Stashed item sounds";

            /// <summary>
            /// Loads the cassette into the boombox (cassette mode), picks the boombox up, turns it on
            /// at a volume, plays it and stashes it once its music plays.
            /// </summary>
            [Step]
            public static IEnumerator PlayAndStash(Step<Music> step, ushort boomboxId, ushort cassetteId, bool radio, float volume)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                yield return Items.Arrive(cassetteId);
                yield return Items.Arrive(boomboxId);
                NetworkedItem.TryGet(boomboxId, out NetworkedItem item);
                NetworkedItem.TryGet(cassetteId, out NetworkedItem tape);
                ItemMagazine magazine = item.GetComponentInChildren<ItemMagazine>(true);
                if (!radio)
                {
                    yield return LocalPlayer.GoTo(tape);
                    yield return Hands.Pickup(cassetteId);
                    // Let go of it first: the magazine's AddItem doesn't end the grab.
                    tape.Item.ForceEndInteraction();
                    yield return Wait.Until(() => Hands.Held == null, 5f, "the cassette out of the hand");
                    if (magazine[0] == null && !magazine.AddItem(tape.gameObject, 0))
                        throw new Exception($"the boombox won't take cassette {cassetteId}");
                    for (int i = 0; i < 30; i++) yield return null;
                }
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(boomboxId);
                BoomboxInteractionController boombox = item.GetComponent<BoomboxInteractionController>();
                yield return Wait.Until(() => boombox.initialized, 5f, "the boombox to start");
                // As its knobs do.
                boombox.modeKnobControl.SetValue(radio ? 1f : 0f);
                boombox.volumeKnobControl.SetValue(volume);
                boombox.powerKnobControl.SetValue(1f);
                yield return new WaitForSecondsRealtime(1.5f);
                if (!radio)
                {
                    boombox.CassettePlay();
                    AudioSource music = boombox.volumeAndInterferenceController.musicAudioSource;
                    yield return Wait.Until(() => boombox.cassetteController.IsPlaying && music.isPlaying && music.timeSamples > 0, 20f, "the cassette to play");
                    yield return new WaitForSecondsRealtime(2f);
                }
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
                yield return Wait.Until(() => PlayerInventory.Holds(boomboxId), 5f, "the stash");
                // The row's player loads its track before it plays.
                yield return Wait.Until(() => RowSource(boomboxId) is { isPlaying: true }, 20f, "the row's music to play");
                Hands.ReleaseControl();
                Music read = ReadRow(boomboxId);
                read.Holder = NetworkRole.LocalPlayerId;
                read.HolderAt = Positions.Of(PlayerManager.PlayerTransform.position);
                step.Result = read;
            }

            /// <summary>The other player: comes near the holder, and waits to hear the holder's stashed boombox.</summary>
            [Step]
            public static IEnumerator HearNear(Step<bool> step, Abs at, ushort boomboxId, byte holder)
            {
                yield return LocalPlayer.TeleportAbs(new Vector3(at.X + 2f, at.Y + 0.1f, at.Z), 1f);
                float until = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < until && HeardSource(boomboxId, holder) is not { isPlaying: true })
                    yield return null;
                AudioSource heard = HeardSource(boomboxId, holder);
                step.Result = heard != null && heard.isPlaying && heard.spatialBlend > 0f;
            }

            /// <summary>Takes the boombox out to the hand, and reads it a few seconds later.</summary>
            [Step]
            public static IEnumerator TakeOut(Step<Music> step, ushort boomboxId)
            {
                Music row = ReadRow(boomboxId);
                yield return PlayerInventory.TakeOut(boomboxId);
                NetworkedItem.TryGet(boomboxId, out NetworkedItem item);
                BoomboxInteractionController boombox = item.GetComponent<BoomboxInteractionController>();
                AudioSource music = boombox.volumeAndInterferenceController.musicAudioSource;
                yield return Wait.Until(() => music.isPlaying && music.timeSamples > 0, 20f, "the boombox's music to play");
                for (int i = 0; i < 10; i++) yield return null;
                Music read = ReadRow(boomboxId);
                read.RowTrack = row.RowTrack;
                read.RowSeek = row.RowSeek;
                read.ObjectPlaying = music.isPlaying && (boombox.IsInRadioMode || boombox.cassetteController.IsPlaying);
                if (boombox.cassetteController.player is { } player)
                {
                    read.ObjectTrack = player.GetTrackIndex();
                    read.ObjectSeek = player.GetSeekPosition();
                }
                Hands.ReleaseControl();
                step.Result = read;
            }

            /// <summary>The other player: their copy of the boombox plays its music.</summary>
            [Step]
            public static IEnumerator CopyPlays(Step<bool> step, ushort boomboxId, bool radio)
            {
                float until = Time.realtimeSinceStartup + 20f;
                bool Plays()
                {
                    if (!NetworkedItem.TryGet(boomboxId, out NetworkedItem item) || item == null) return false;
                    BoomboxInteractionController boombox = item.GetComponent<BoomboxInteractionController>();
                    return boombox != null && boombox.initialized && boombox.volumeAndInterferenceController.musicAudioSource.isPlaying &&
                           (radio ? boombox.IsInRadioMode : boombox.cassetteController.IsPlaying);
                }
                while (Time.realtimeSinceStartup < until && !Plays())
                    yield return null;
                step.Result = Plays();
            }

            private static AudioSource RowSource(ushort id)
            {
                GameObject row = GameObject.Find($"{SoundRoot}/[MP] Stashed boombox {id}");
                return row != null ? row.GetComponent<AudioSource>() : null;
            }

            private static AudioSource HeardSource(ushort id, byte holder) =>
                Object.FindObjectsOfType<AudioSource>().FirstOrDefault(source => source.name == $"[MP] Stashed boombox {id} of P{holder}");

            private static Music ReadRow(ushort id)
            {
                InventoryStandIn.TryGet(id, out InventoryStandIn standIn);
                AudioSource source = RowSource(id);
                DV.Radio.PlaylistPlayer player = source != null ? source.GetComponent<DV.Radio.PlaylistPlayer>() : null;
                string state = standIn != null ? standIn.State : "";
                return new Music
                {
                    StandIn = standIn != null,
                    Nested = StandInAmmo.Of(state).Count > 0,
                    RowPlaying = source != null && source.isPlaying,
                    Volume = source != null ? source.volume : -1f,
                    RowTrack = player != null ? player.GetTrackIndex() : -1,
                    RowSeek = player != null ? player.GetSeekPosition() : -1,
                    State = StandInAmmo.Without(state)
                };
            }
        }
    }
}
