using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Jobs;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// OI-COLD-15 (R4C C7, with OI-COLD-07): a client's job booklet is back in their own inventory,
    /// in its slot, after a restart. A booklet a player carries is a row, saved with them like any
    /// item, and vanilla's load doesn't reprint it (docs/jobs.md, "Save, load and rejoin"). The
    /// client takes a job at FM's office and stashes its booklet, the host saves, and both games
    /// restart: the booklet must be in the client's inventory, in its slot, and in no other, the same
    /// item (its id) and not one printed again. WHO=host: the host's own player carries it (a player
    /// host only); its join once drew its rows before the save's jobs were known, dropped the booklet
    /// and printed another (R4C C9 B6).
    /// </summary>
    [Scenario("item-bug-booklet-restart", "OI-COLD-15: a client's job booklet is back in their own inventory, in its slot, after a restart")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class ItemBugBookletRestart : Scenario<ItemBugBookletRestart.Options>
    {
        public sealed class Options
        {
            public string Station = "FM";
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
            /// <summary>Who carries the booklet: client, or host (the host's own player; needs a player host).</summary>
            public string Who = "client";
        }

        // FM's office, as booklet-summon.
        private const float OfficeX = 5990f, OfficeZ = 6740f, AwayX = 8600f, AwayZ = 6740f;

        protected override async Task Run(Pair pair, Options o)
        {
            Peer[] players = pair.Dedicated ? new[] { pair.Client } : new[] { pair.Host, pair.Client };
            if (await pair.Host.Run(() => BookletSummon.InGame.AvailableJobs(o.Station)) == 0)
            {
                Report.Note($"no transport or empty-haul job at {o.Station}: going 2 km away so DV generates new ones");
                foreach (Peer p in players) await p.Run(() => Players.TeleportToGround(AwayX, AwayZ), 90);
                await Task.Delay(8000);
            }
            for (int i = 0; i < players.Length; i++)
            {
                float x = OfficeX - 4f * i;
                await players[i].Run(() => Players.TeleportToGround(x, OfficeZ), 90);
            }
            await Task.Delay(6000);   // the office's overviews load and reach the client

            bool byHost = o.Who == "host";
            if (byHost && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = byHost ? pair.Host : pair.Client;
            string job = await pair.Host.Run(() => BookletSummon.InGame.PickJob(o.Station));
            Report.Note($"the {who.Name} takes {job}");
            pair.Cleanup(() => pair.Host.Run(() => BookletSummon.InGame.Abandon(job)));
            ushort booklet = await who.Run<ushort>(s => BookletSummon.InGame.Accept(s, job), 60);
            int slot = await who.Run<int>(s => Items.Stash(s, booklet, false), 60);
            string id = await pair.Host.Run(() => Owned.IdOf(booklet));
            Report.Note($"the {who.Name} stashed booklet {booklet} ({id}) in slot {slot}");
            Require.That(id.Length > 0, "the stashed booklet has an id");
            Require.Equal(slot, await who.Run(() => InGame.SlotOf(job)), $"the {who.Name}'s booklet slot before the save");

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(20000);   // the client receives its items; vanilla's reprint waits for the host's

            int client = await pair.Client.Run(() => InGame.SlotOf(job));
            int host = pair.Dedicated ? -1 : await pair.Host.Run(() => InGame.SlotOf(job));
            Report.Note($"after the restart: the client's inventory {(client < 0 ? "has no booklet for it" : "slot " + client)}; " +
                        (pair.Dedicated ? "a dedicated host prints none" : $"the host's inventory {(host < 0 ? "has none" : "slot " + host)}"));
            Expect.Equal(byHost ? -1 : slot, client, $"the booklet for {job} in the client's inventory after the restart");
            if (!pair.Dedicated)
                Expect.Equal(byHost ? slot : -1, host, $"the booklet for {job} in the host's inventory after the restart");
            ushort now = await who.Run(() => InGame.NetIdOf(job));
            string after = now != 0 ? await pair.Host.Run(() => Owned.IdOf(now)) : "";
            Report.Note($"the {who.Name}'s booklet after the restart: {now} ({after})");
            Expect.Equal(id, after, $"the {who.Name}'s booklet's id after the restart (the same booklet, not one printed again)");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>This player: the inventory slot of a booklet for the job (an object or a stand-in), or -1.</summary>
            [Step]
            public static int SlotOf(string jobId)
            {
                foreach ((ushort _, int slot, GameObject item) in PlayerInventory.Stored())
                {
                    if (item.GetComponent<JobBooklet>()?.job?.ID == jobId)
                        return slot;
                    if (item.TryGetComponent(out InventoryStandIn standIn) && standIn.PrefabName == nameof(JobBooklet) &&
                        JobPapers.JobIdOf(standIn.State) == jobId)
                        return slot;
                }
                return -1;
            }

            /// <summary>This player: the NetId of a booklet for the job in the inventory (its stand-in's or its object's), or 0.</summary>
            [Step]
            public static ushort NetIdOf(string jobId)
            {
                int slot = SlotOf(jobId);
                return slot < 0 ? (ushort)0 : PlayerInventory.NetIdAt(slot);
            }
        }
    }
}
