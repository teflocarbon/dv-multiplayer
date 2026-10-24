using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Newtonsoft.Json.Linq;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The host's own player is stored by GUID like any player (docs/design/server-player-split.md):
    /// the host stashes a mug, saves, both games restart from the save, and the host must get its
    /// whole inventory back once (nothing duplicated, nothing lost), at the same place. The save
    /// must be in the per-player format, with the host's items as records owned by its GUID, and
    /// DV's own inventory list kept as a copy for vanilla DV. On a dedicated host the local player
    /// is a parked body: a player host only.
    /// </summary>
    [Scenario("host-player-persist", "The host's own player is stored by GUID like any player: after a save and restart its whole inventory comes back once, where it saved")]
    [Needs(Need.PlayerHost, Need.RestartsGames)]
    [Budget(900)]
    public sealed class HostPlayerPersist : Scenario<HostPlayerPersist.Options>
    {
        public sealed class Options
        {
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "host"));
            await pair.Host.Run<int>(s => Items.Stash(s, item, false), 60);
            State before = await pair.Host.Run(() => InGame.ReadState());
            Report.Note($"host stashed mug {item}; inventory: {before.Inventory}");
            Expect.Equal("", before.Duplicates, "items the host holds more than once, before saving");

            await pair.Host.Run(() => Saves.Manual());
            Saved saved = await pair.Host.Run(() => InGame.ReadSave());
            Report.Note($"save: format {saved.Format}, {saved.HostRecords} record(s) owned by the host's GUID, DV's own inventory list holds {saved.Mirror}");
            Expect.Equal(2, saved.Format, "the save's player state format (2: per player)");
            Expect.That(saved.HostRecords != 0, "the save has records owned by the host's GUID", saved.HostRecords.ToString());
            Expect.That(saved.Mirror != 0, "DV's own inventory list in the save has items (the copy for vanilla DV)", saved.Mirror.ToString());

            await pair.Restart(o.Session);
            await Task.Delay(12000);   // the host's player state is applied
            State after = await pair.Host.Run(() => InGame.ReadState());
            Report.Note($"after: inventory {after.Inventory}");
            Expect.Equal("", after.Duplicates, "items the host holds more than once, after reloading");
            Expect.Equal(before.Inventory, after.Inventory, "the host's inventory after the reload (as saved)");
            Expect.AtMost(Peers.Horizontal(before.Position, after.Position), 5, "where the host spawned from where it saved (m)");
        }

        public sealed class State
        {
            public string Inventory, Duplicates;
            public Abs Position;
        }

        public sealed class Saved
        {
            public int Format, HostRecords, Mirror;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: its inventory by prefab (sorted, so two snapshots compare), the prefabs
            /// held more than once, and where it stands.</summary>
            [Step]
            public static State ReadState()
            {
                var names = PlayerInventory.Stored().Select(e => PlayerInventory.PrefabOf(e.go).Replace("(Clone)", "")).ToList();
                names.Sort(StringComparer.Ordinal);
                var dupes = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key + " x" + g.Count());
                return new State { Inventory = string.Join(",", names), Duplicates = string.Join(",", dupes), Position = Positions.Of(PlayerManager.PlayerTransform.position) };
            }

            /// <summary>Host: what the save it just wrote holds for its own player: the format marker,
            /// how many items Multiplayer.Items has in its GUID's inventory (its ledger homes and unowned
            /// rows), and how many entries DV's own inventory list (the mirror for vanilla) has.</summary>
            [Step]
            public static Saved ReadSave()
            {
                var data = SaveGameManager.Instance.data;
                var root = data.GetJObject("Multiplayer");
                string guid = Multiplayer.Shared.Session.PlayerIdentity.Local.ToString("D");
                JObject items = root?["Items"] as JObject;
                int records = ((items?["ledger"] as JArray)?.OfType<JObject>()
                    .Count(r => string.Equals((string)r["where"]?["holder"], guid, StringComparison.OrdinalIgnoreCase)) ?? 0) +
                    ((items?["unowned"] as JArray)?.OfType<JObject>()
                    .Count(r => string.Equals((string)r["holder"], guid, StringComparison.OrdinalIgnoreCase)) ?? 0);
                var mirror = StorageSerializer.LoadStorageData(DV.ThingTypes.StorageType.Inventory, data);
                return new Saved { Format = (int?)root?["PlayerStateFormat"] ?? 0, HostRecords = records, Mirror = mirror?.Count ?? 0 };
            }
        }
    }
}
