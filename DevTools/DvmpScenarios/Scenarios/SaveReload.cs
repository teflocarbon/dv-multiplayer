using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Stations;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Items must not multiply, fall from the sky, or drift into Lost and Found across repeated
    /// host save + reload cycles. Each cycle restarts the pair on the save (the same kind of host:
    /// a dedicated pair stays dedicated), counts the host's items, and saves.
    /// </summary>
    [Scenario("save-reload", "Repeated save + reload of the host: no more items, job overviews or Lost and Found items than on the first load, none in the sky, the host's inventory recorded as inventory")]
    [Needs(Need.RestartsGames)]
    [Budget(1800)]
    public sealed class SaveReload : Scenario<SaveReload.Options>
    {
        public sealed class Options
        {
            /// <summary>The session (or save) to load.</summary>
            public string Session = "DVMP Test Career 2";
            /// <summary>Reloads.</summary>
            public int Cycles = 3;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Counts first = null;
            for (int cycle = 1; cycle <= o.Cycles; cycle++)
            {
                await pair.Restart(o.Session);
                Counts c = await pair.Host.Run<Counts>(s => InGame.Count(s, 8f));
                Report.Note($"load {cycle}: {c.Items} items, {c.JobOverviews} job overviews, {c.LostAndFound} in Lost and Found, " +
                            $"{c.ActiveInSky} active in the sky, {c.HostInventoryMisrecorded} host inventory items misrecorded");
                first ??= c;
                Expect.AtMost(c.Items, first.Items, $"load {cycle}: items (at most the first load's)");
                Expect.AtMost(c.JobOverviews, first.JobOverviews, $"load {cycle}: job overviews (at most the first load's)");
                Expect.AtMost(c.LostAndFound, first.LostAndFound, $"load {cycle}: items in Lost and Found (at most the first load's)");
                Expect.Zero(c.ActiveInSky, $"load {cycle}: items active in the sky (above 1500 m)");
                Expect.Zero(c.HostInventoryMisrecorded, $"load {cycle}: host inventory items recorded somewhere else");
                await pair.Host.Run(() => InGame.Save());
            }
        }

        public sealed class Counts
        {
            public int Items, JobOverviews, ActiveInSky, LostAndFound, HostInventoryMisrecorded;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: item totals that must stay stable across save/reload cycles, once the
            /// world has had <paramref name="settle"/> seconds since loading.</summary>
            [Step]
            public static IEnumerator Count(Step<Counts> step, float settle)
            {
                yield return new WaitForSeconds(settle);
                var all = Dev.All<NetworkedItem>().Where(i => i.NetId != 0).ToList();
                // The host's own player's slots hold stand-ins (D14).
                int misrecorded = 0;
                foreach (var (netId, _, _) in PlayerInventory.Stored())
                {
                    if (netId != 0 && LiveItems.TryGet(netId, out var r) && r.Placement != ItemPlacementKind.PlayerInventory)
                        misrecorded++;
                }
                step.Result = new Counts
                {
                    Items = all.Count,
                    JobOverviews = all.Count(i => i.name.StartsWith("JobOverview")),
                    ActiveInSky = all.Count(i => i.gameObject.activeInHierarchy && Dev.Abs(i.transform.position).y > 1500),
                    LostAndFound = all.Count(i => LostAndFoundServer.Contains(i.NetId)),
                    HostInventoryMisrecorded = misrecorded
                };
            }

            /// <summary>Host: a manual save, given 3 s to be written.</summary>
            [Step]
            public static IEnumerator Save()
            {
                SaveGameManager.Instance.Save(DV.Common.SaveType.Manual);
                yield return new WaitForSeconds(3);
            }
        }
    }
}
