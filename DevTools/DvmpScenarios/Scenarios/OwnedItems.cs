using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Server.Items;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// What a player owns isn't counted from another player's items (playtest 2). Player numbers
    /// are handed out per session, and a record restored from a save, or left by a player who has
    /// gone, keeps the number its owner had then; another player can have that number now.
    /// OwnedItemsSummary matched by that number as well as by identity, so the other player's comms
    /// radio counted as this player's: DV's starting-items safeguard made them none on joining, and
    /// /give refused one ("already has a CommsRadio somewhere"). A pair has one client, so the
    /// shift is set up by hand: the host spawns an essential item owned by a player who isn't here
    /// (a new identity) and gives its record the client's number, as a restored record whose owner
    /// hasn't joined would have. Then the client's owned items (what its join packet tells DV it
    /// has, and what /give checks) don't include it; and if the client didn't own one already,
    /// /give (sent by the client, made an admin in memory only) gives it one, into its inventory.
    /// </summary>
    [Scenario("owned-items", "An essential item owned by an absent player whose saved number is now the client's doesn't count as the client's, and /give gives the client one")]
    public sealed class OwnedItems : Scenario<OwnedItems.Options>
    {
        public sealed class Options
        {
            /// <summary>An essential item (default: one the client doesn't own, else the comms radio).</summary>
            public string Prefab = "";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Stale s = await pair.Host.Run<Stale>(st => InGame.SpawnStale(st, o.Prefab));
            Report.Note($"host spawned {s.Prefab} {s.Item}, owned by an absent player whose number ({s.Number}) is now the client's");
            Report.Note($"the client owned {s.Before} {s.Prefab} before, counts {s.After} now");
            Expect.Equal(s.Before, s.After, $"{s.Prefab} the client owns, with the absent player's (as many as before)");
            if (s.Before != 0)
            {
                Report.Note($"(the client already owns a {s.Prefab}, so /give is left out; set PREFAB to one it doesn't own)");
                return;
            }

            ClientPlayer client = await pair.Host.Run(() => InGame.MakeClientAdmin());
            pair.Cleanup(() => pair.Host.Run(() => InGame.SetAdmins(client.OldAdmins)));
            Report.Note($"the client sends /give {client.Name} {s.Prefab}");
            await pair.Client.Run(() => InGame.Chat($"/give {client.Name} {s.Prefab}"));
            ushort given = await pair.Client.Run<ushort>(st => InGame.WaitInInventory(st, s.Prefab, 10f));
            Report.Note("in the client's inventory: " + (given == 0 ? "nothing" : $"{s.Prefab} {given}"));
            if (given != 0) pair.Cleanup(() => pair.Host.Run<ushort[]>(st => Items.Destroy(st, new[] { given })));
            Expect.That(given != 0, $"/give gave the client a {s.Prefab}");
        }

        public sealed class Stale
        {
            public string Prefab;
            public ushort Item;
            public int Number, Before, After;
        }

        public sealed class ClientPlayer
        {
            public string Name, OldAdmins;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: an essential item at the host's feet owned by a player who isn't here (a new
            /// identity), with the client's player number as its owner number: a restored record
            /// whose owner hasn't joined, after the numbers moved on. Reports how many of that prefab
            /// the client owns (OwnedItemsSummary, what its join and /give use) before and after.
            /// </summary>
            [Step]
            public static IEnumerator SpawnStale(Step<Stale> step, string wanted)
            {
                var server = NetworkLifecycle.Instance.Server;
                var client = server.ServerPlayers.First(p => p.PlayerId != server.SelfId);
                var essentials = DV.Globals.G.Items.items.Where(i => i != null && i.IsEssential && !string.IsNullOrEmpty(i.ItemPrefabName)).ToList();
                int Owned(string prefab) => OwnedItemsSummary.For(client).Count(x => string.Equals(x.Prefab, prefab, StringComparison.OrdinalIgnoreCase));
                var spec = !string.IsNullOrEmpty(wanted)
                    ? essentials.FirstOrDefault(i => string.Equals(i.ItemPrefabName, wanted, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception(wanted + " isn't an essential item")
                    : essentials.FirstOrDefault(i => Owned(i.ItemPrefabName) == 0) ?? essentials.First(i => i.ItemPrefabName == "CommsRadio");
                int before = Owned(spec.ItemPrefabName);

                var go = UnityEngine.Object.Instantiate(spec.gameObject, PlayerManager.PlayerTransform.position + Vector3.up * 0.6f + Vector3.right * 1.5f, Quaternion.identity, WorldMover.OriginShiftParent);
                go.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
                NetworkedItem item = null;
                AuthoritativeItemRegistry.Record record = null;
                yield return Wait.Until(() => (item = go.GetComponent<NetworkedItem>()) != null && item.NetId != 0 && AuthoritativeItemRegistry.TryGet(item.NetId, out record),
                    10f, $"the spawned {spec.ItemPrefabName}'s server record");
                Items.Track(item.NetId);
                record.PersistentOwnerIdentity = Guid.NewGuid();
                record.PersistentOwnerPlayerId = client.PlayerId;
                yield return null;
                step.Result = new Stale { Prefab = spec.ItemPrefabName, Item = item.NetId, Number = client.PlayerId, Before = before, After = Owned(spec.ItemPrefabName) };
            }

            /// <summary>Host: makes the client an admin, in memory only. Result: its name, and the
            /// admins setting as it was.</summary>
            [Step]
            public static ClientPlayer MakeClientAdmin()
            {
                var server = NetworkLifecycle.Instance.Server;
                var client = server.ServerPlayers.First(p => p.PlayerId != server.SelfId);
                var old = SetAdmins(client.Guid.ToString());
                return new ClientPlayer { Name = client.Username, OldAdmins = old };
            }

            /// <summary>Host: sets the admins setting. Result: what it was.</summary>
            [Step]
            public static string SetAdmins(string admins)
            {
                string old = Multiplayer.Multiplayer.Settings.Admins;
                Multiplayer.Multiplayer.Settings.Admins = admins ?? "";
                return old;
            }

            /// <summary>Sends a chat line or command as this game's player.</summary>
            [Step]
            public static void Chat(string line) => NetworkLifecycle.Instance.LocalSession.SendChat(line);

            /// <summary>Waits up to <paramref name="seconds"/> for an item of the prefab, bound to the
            /// host, in this player's inventory. Result: its net ID, 0 for none.</summary>
            [Step]
            public static IEnumerator WaitInInventory(Step<ushort> step, string prefab, float seconds)
            {
                NetworkedItem got = null;
                for (float t0 = Time.realtimeSinceStartup; got == null && Time.realtimeSinceStartup - t0 < seconds;)
                {
                    yield return new WaitForSecondsRealtime(0.5f);
                    got = DV.InventorySystem.Inventory.Instance.GetItemsArray(false).Where(go => go != null && go.name.StartsWith(prefab))
                        .Select(go => go.GetComponent<NetworkedItem>()).FirstOrDefault(n => n != null && n.NetId != 0);
                }
                step.Result = got == null ? (ushort)0 : got.NetId;
            }
        }
    }
}
