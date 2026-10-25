using System.Collections.Generic;
using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Vanilla's duplicate pass doesn't run in multiplayer (OI-VAN-01; docs/design/items/vanilla-storage.md,
    /// "The load"). Its <c>CombineAndResolveIllegalDuplicates</c> keeps one radio in a whole save, and a
    /// multiplayer save holds every player's: two radios are put in Lost and Found, the client's and
    /// the host player's (on a dedicated host, two of the client's), the game is saved and both games
    /// restart. Both radios are still in their owners' Lost and Found, their entries live.
    /// </summary>
    [Scenario("duplicate-essentials-load", "Two players' radios in Lost and Found both survive a restart: vanilla's duplicate pass doesn't run (OI-VAN-01)")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class DuplicateEssentialsLoad : Scenario<DuplicateEssentialsLoad.Options>
    {
        public sealed class Options
        {
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
            /// <summary>The essential's prefab.</summary>
            public string Essential = "CommsRadio";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);
            string[] owners = pair.Dedicated ? new[] { "client", "client" } : new[] { "client", "host" };
            var ids = new List<string>();
            foreach (string who in owners)
            {
                ushort radio = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Essential, "client"));
                await pair.Host.Run(() => Ownership.Give(radio, who));
                string id = await pair.Host.Run(() => Owned.IdOf(radio));
                Require.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, radio, "SaveRecovery")), $"the {who}'s {o.Essential} went to its Lost and Found");
                ids.Add(id);
                pair.Cleanup(async () =>
                {
                    ushort netId = await pair.Host.Run(() => Owned.NetIdOf(id));
                    if (netId != 0) await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { netId }), 60);
                });
                Report.Note($"the {who}'s {o.Essential} {radio} ({id}) in its Lost and Found");
            }

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(20000);   // the load, and the client's items

            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                LedgerEntry e = await pair.Host.Run(() => Owned.Entry(id));
                OwnerInfo where = await pair.Host.Run(() => Ownership.ById(id));
                Report.Note($"after the restart, the {owners[i]}'s {o.Essential}: {e}; {where}");
                Expect.That(e.Live, $"the {owners[i]}'s {o.Essential}'s entry lives after the restart", e.ToString());
                Expect.Equal("LostAndFound", e.Place, $"the {owners[i]}'s {o.Essential}'s ledger home after the restart");
                Expect.That(where.Lost, $"the {owners[i]}'s {o.Essential} is in Lost and Found after the restart", where.ToString());
            }
        }
    }
}
