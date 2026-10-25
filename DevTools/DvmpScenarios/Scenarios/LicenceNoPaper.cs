using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV.Booklets;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// No licence papers in multiplayer (OI-START-07; docs/design/items/essentials.md): a licence is an
    /// entitlement, synced with the career, and never an item. Neither game has a licence paper, and
    /// vanilla's paper for a job and a general licence (what a bought licence, the load's tutorial fix
    /// and the paying screen make) is refused on both: none is made.
    /// </summary>
    [Scenario("licence-no-paper", "No licence paper exists or can be made on either game: a licence is an entitlement only (OI-START-07)")]
    [Budget(120)]
    public sealed class LicenceNoPaper : Scenario<LicenceNoPaper.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);
            foreach (Peer game in new[] { pair.Host, pair.Client })
            {
                string[] papers = await game.Run(() => InGame.Papers());
                Report.Note($"{game.Name}'s licence papers: {(papers.Length == 0 ? "none" : string.Join(", ", papers))}");
                Expect.Empty(papers, $"the licence papers {game.Name}'s game has");
                string made = await game.Run(() => InGame.MakePapers());
                Report.Note($"{game.Name}: {made}");
                Expect.Equal("job: none, general: none", made, $"what vanilla's licence papers make on {game.Name}'s game");
                Expect.Empty(await game.Run(() => InGame.Papers()), $"the licence papers {game.Name}'s game has after");
            }
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static HashSet<string> PaperPrefabs() =>
                new(DV.Globals.G.Types.jobLicenses.Where(l => l.licensePrefab != null).Select(l => l.licensePrefab.name)
                    .Concat(DV.Globals.G.Types.generalLicenses.Where(l => l.licensePrefab != null).Select(l => l.licensePrefab.name)));

            /// <summary>The licence papers this game has: each one's prefab and net ID.</summary>
            [Step]
            public static string[] Papers()
            {
                HashSet<string> prefabs = PaperPrefabs();
                return Dev.All<NetworkedItem>()
                    .Where(i => i != null && i.Item?.InventorySpecs != null && prefabs.Contains(i.Item.InventorySpecs.ItemPrefabName))
                    .Select(i => $"{i.Item.InventorySpecs.ItemPrefabName} ({i.NetId})").ToArray();
            }

            /// <summary>Asks vanilla for a job licence's paper and a general licence's. Result: what each made.</summary>
            [Step]
            public static string MakePapers()
            {
                var job = DV.Globals.G.Types.jobLicenses.First(l => l.licensePrefab != null);
                var general = DV.Globals.G.Types.generalLicenses.First(l => l.licensePrefab != null);
                Vector3 at = PlayerManager.PlayerTransform != null ? PlayerManager.PlayerTransform.position + Vector3.up : Vector3.zero;
                GameObject a = BookletCreator.CreateLicense(job, at, Quaternion.identity, WorldMover.OriginShiftParent);
                GameObject b = BookletCreator.CreateLicense(general, at, Quaternion.identity, WorldMover.OriginShiftParent);
                string result = $"job: {(a == null ? "none" : a.name)}, general: {(b == null ? "none" : b.name)}";
                if (a != null) Object.Destroy(a);
                if (b != null) Object.Destroy(b);
                return result;
            }
        }
    }
}
