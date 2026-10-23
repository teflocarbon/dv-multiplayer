using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.Server.Career;
using Multiplayer.Server.Items.Essentials;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.OwnedList;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Session;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A player known by their install GUID logs in with a Steam ID for the first time
    /// (InstallGuidRekey; item-refactor.md, R1). Owners and holders are identities, so a re-key
    /// moves everything of theirs to the new one, and nothing is bound to a session. A pair logs in
    /// unverified, so the host is made to resolve the client's next login to a Steam-form identity,
    /// with its install GUID as the old one, as a verified login would. Before: the client owns a
    /// mug in its Lost and Found and one in its inventory, and its essentials. After the rejoin, under
    /// the new identity: the same owned-items list, essentials and starter kit, nothing left under
    /// the install GUID, no essential issued again, the stashed mug in its inventory and the lost one
    /// retrieved. The client's data is moved back to its install GUID afterwards.
    /// </summary>
    [Scenario("rekey", "An install GUID re-keyed to a Steam ID at login keeps the player's owned list, Lost and Found retrieval and essentials")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class Rekey : Scenario<Rekey.Options>
    {
        public sealed class Options
        {
            /// <summary>The Steam ID has data already: a mug of its own in the stashed mug's slot (OI-ID-19).
            /// The two are merged, and the stashed mug, its slot taken, goes to Lost and Found.</summary>
            public bool Merge = false;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            string install = await pair.Host.Run(() => Owned.ClientIdentity());
            Require.That(!await pair.Host.Run(() => InGame.IsSteamForm(install)), "the client logs in by its install GUID (an unverified pair)", install);
            string steam = await pair.Host.Run(() => InGame.NewSteamIdentity());

            ushort lost = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(lost), 60);
            Require.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, lost, "SaveRecovery")), "the first mug went to the client's Lost and Found");
            ushort kept = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run<int>(s => Items.Stash(s, kept, false), 60);
            await Task.Delay(2000);   // the stash reaches the host's record
            string lostId = await pair.Host.Run(() => Owned.IdOf(lost));
            string keptId = await pair.Host.Run(() => Owned.IdOf(kept));
            Holdings before = await pair.Host.Run(() => InGame.Of(install));
            Report.Note($"under the install GUID {install}: {before}");
            Require.AtLeast(before.Lost, 1, "the client's Lost and Found entries");
            Require.AtLeast(before.Essentials, 1, "the client's essentials");
            await pair.Host.Run(() => Saves.Manual());

            string seededId = "";
            if (o.Merge)
            {
                int keptSlot = (await pair.Host.Run(() => Ledger.Where(kept))).Slot;
                seededId = await pair.Host.Run(() => InGame.SeedMug(steam, keptSlot));
                Report.Note($"the Steam ID's own mug {seededId} in slot {keptSlot}, the stashed mug's");
            }
            await pair.Host.Run(() => InGame.ResolveAs(install, steam));
            pair.Cleanup(async () =>
            {
                await pair.Host.Run(() => InGame.ResolveAs("", ""));
                if (await pair.Host.Run(() => Ownership.RemotePlayers()) > 0) await pair.Client.Quit();
                await Until.Value(() => pair.Host.Run(() => Ownership.RemotePlayers()), n => n == 0, 60, "the client gone");
                await pair.Host.Run(() => InGame.MoveBack(steam, install));
                await pair.Rejoin();
            });
            await pair.Client.Quit();
            await Until.Value(() => pair.Host.Run(() => Ownership.RemotePlayers()), n => n == 0, 60, "the host to notice the client left");
            await pair.Rejoin();
            await Task.Delay(10000);   // the rejoined client receives its items

            Require.Equal(steam, await pair.Host.Run(() => Owned.ClientIdentity()), "the client's identity after the rejoin (the Steam-form one)");
            Holdings after = await pair.Host.Run(() => InGame.Of(steam));
            Holdings left = await pair.Host.Run(() => InGame.Of(install));
            Report.Note($"under the Steam identity: {after}; left under the install GUID: {left}");
            int seeded = o.Merge ? 1 : 0;
            Expect.Equal(before.Rows + seeded, after.Rows, "the owned-items list's rows");
            Expect.Equal(before.Ledger + seeded, after.Ledger, "the ledger's live entries");
            Expect.Equal(before.Owned + seeded, after.Owned, "what the player owns (the ledger)");
            Expect.Equal(before.Essentials, after.Essentials, "the player's essentials (none issued again)");
            Expect.Equal(before.Lost + seeded, after.Lost, "the player's Lost and Found entries");
            Expect.Equal(before.Kit, after.Kit, "the starter kit");
            Expect.Equal(0, left.Ledger + left.Owned + left.Lost + (left.Kit ? 1 : 0), "anything left under the install GUID");

            OwnerInfo k = await pair.Host.Run(() => Ownership.ById(keptId));
            Expect.Equal(steam, k.Owner, "the stashed mug's owner");
            // It went cold when the client left and was made again at the rejoin, under a new net ID (D12).
            ushort keptNow = await pair.Host.Run(() => Owned.NetIdOf(keptId));
            await pair.Host.Run(() => Ledger.Track(keptNow));
            if (o.Merge)
            {
                // Its slot was the Steam ID's: it went to Lost and Found, and the Steam ID's mug is in the slot.
                Expect.That(k.Lost, "the stashed mug, its slot taken, is in Lost and Found", k.ToString());
                ushort seededNow = await pair.Host.Run(() => Owned.NetIdOf(seededId));
                await pair.Host.Run(() => Ledger.Track(seededNow));
                OwnerInfo sd = await pair.Host.Run(() => Ownership.ById(seededId));
                Expect.Equal(steam, sd.Holder, "the Steam ID's mug's holder");
                Expect.That(await Until.Value(() => pair.Client.Run(() => Owned.InInventory(seededNow)), v => v, 20, "the Steam ID's mug in the client's inventory"),
                    "the Steam ID's own mug is in the client's inventory after the merge");
            }
            else
            {
                Expect.Equal(steam, k.Holder, "the stashed mug's holder");
                Expect.That(await Until.Value(() => pair.Client.Run(() => Owned.InInventory(keptNow)), v => v, 20, "the stashed mug in the client's inventory"),
                    "the stashed mug is in the client's inventory after the rejoin");
            }

            OwnerInfo l = await pair.Host.Run(() => Ownership.ById(lostId));
            Expect.Equal(steam, l.Owner, "the lost mug's owner");
            Expect.That(l.Lost, "the lost mug is still in Lost and Found", l.ToString());
            uint lostHandle = await pair.Host.Run(() => Owned.HandleOf(lost));   // the list names rows by handle (R2)
            int slot = await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, lostHandle), 60);
            await Task.Delay(2000);
            OwnerInfo got = await pair.Host.Run(() => Ownership.ById(lostId));
            Report.Note($"retrieved into slot {slot}: {got}");
            Expect.Equal("PlayerInventory", got.Placement, "the retrieved mug's placement");
            Expect.Equal(steam, got.Holder, "who has the retrieved mug (the client, by its new identity)");
            Expect.Empty(await pair.Host.Run<string[]>(s => Owned.AuditFor(s, new[] { lostId, keptId })), "ledger audit problems for the two mugs");
        }

        /// <summary>What an identity has on the host.</summary>
        public sealed class Holdings
        {
            public int Rows, Ledger, Owned, Essentials, Lost;
            public bool Kit;

            public override string ToString() =>
                $"{Rows} list rows, {Ledger} ledger entries, {Owned} owned ({Essentials} essential), {Lost} in Lost and Found, starter kit {Kit}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string HarmonyId = "dvmp.scenarios.rekey";
            private static Guid from, to;

            /// <summary>Host: whether an identity is in Steam form.</summary>
            [Step]
            public static bool IsSteamForm(string identity) =>
                Guid.TryParse(identity, out Guid id) && PlayerIdentity.TryGetSteamId(id, out _);

            /// <summary>Host: a Steam-form identity no player has.</summary>
            [Step]
            public static string NewSteamIdentity() =>
                PlayerIdentity.FromSteamId(76561190000000000UL + (ulong)new Random().Next(1, int.MaxValue)).ToString("D");

            /// <summary>
            /// Host: the next logins by <paramref name="install"/> resolve to <paramref name="steam"/>,
            /// with the install GUID as the old one (as a verified login has); "" stops it.
            /// </summary>
            [Step]
            public static void ResolveAs(string install, string steam)
            {
                var harmony = new Harmony(HarmonyId);
                harmony.UnpatchAll(HarmonyId);
                if (!Guid.TryParse(install, out from) || !Guid.TryParse(steam, out to))
                    return;
                harmony.Patch(AccessTools.Method(typeof(NetworkServer), "AdmitPlayer"),
                    prefix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(Admitting))));
            }

            public static void Admitting(ref Guid playerId, Guid legacyGuid, bool isLocalPlayer)
            {
                if (!isLocalPlayer && from != Guid.Empty && legacyGuid == from)
                    playerId = to;
            }

            /// <summary>Host: what the identity owns: list rows (while connected), ledger entries,
            /// owned items and essentials, Lost and Found entries, the starter kit.</summary>
            [Step]
            public static Holdings Of(string identity)
            {
                Guid id = Guid.Parse(identity);
                ServerPlayer player = NetworkServer.Instance.ServerPlayers.FirstOrDefault(p => p.PlayerId != 0 && p.Guid == id);
                var owned = ServerLedger.Ledger.LiveOwnedBy(Multiplayer.Core.Items.Ledger.OwnerRef.Player(id));
                return new Holdings
                {
                    Rows = player != null ? OwnedItemRows.For(player).Count : 0,
                    Ledger = owned.Count,
                    Owned = owned.Count,
                    Essentials = owned.Count(item => DV.Globals.G.Items.items.Any(spec => spec != null && spec.IsEssential &&
                        string.Equals(spec.ItemPrefabName, item.Prefab, StringComparison.OrdinalIgnoreCase))),
                    Lost = Ownership.LostCount(identity),
                    Kit = ServerLedger.Ledger.HasStarterKit(id)
                };
            }

            /// <summary>Host: gives an identity no one is logged in as a mug in an inventory slot, kept as a
            /// cold row with its ledger entry, as if it had played before. Result: the mug's id.</summary>
            [Step]
            public static string SeedMug(string identity, int slot)
            {
                Guid owner = Guid.Parse(identity), id = Guid.NewGuid();
                var registered = ServerLedger.Ledger.Register(new Multiplayer.Core.Items.Ledger.BirthRequest
                {
                    Id = id,
                    Owner = Multiplayer.Core.Items.Ledger.OwnerRef.Player(owner),
                    Prefab = "Mug",
                    Home = Multiplayer.Core.Items.Ledger.Home.InSlot(owner, slot),
                    Reason = Multiplayer.Core.Items.Ledger.BirthReason.Granted,
                    Detail = "rekey scenario: the Steam ID's own mug"
                });
                if (!registered.Accepted) throw new Exception("the ledger refused the seeded mug: " + registered.Refusal);
                Multiplayer.Server.Items.Inventory.ColdInventories.Add(new Multiplayer.Server.Items.SaveLoad.SavedItemRow
                {
                    PersistentItemId = id,
                    PrefabName = "Mug",
                    OwnerIdentity = owner,
                    PlacementPlayerIdentity = owner,
                    Slot = slot
                });
                return id.ToString("D");
            }

            /// <summary>Host: moves an identity's data back to another (the run's cleanup), as a re-key does.</summary>
            [Step]
            public static bool MoveBack(string steam, string install) =>
                InstallGuidRekey.MoveInto(Guid.Parse(steam), Guid.Parse(install), "rekey scenario cleanup");
        }
    }
}
