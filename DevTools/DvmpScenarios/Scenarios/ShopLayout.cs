using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV.Shops;
using Dvmp.Agent;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Every game lays a shop's shelves out the same way (docs/design/shops.md, gap 2). Vanilla's
    /// <c>ShopShelves</c> picks its seed with <c>UnityEngine.Random</c>, so each game put the items
    /// in different places. Both players stand at the shop, and every display that both games have
    /// laid out must be in the same place, within 1 cm.
    /// </summary>
    [Scenario("shop-layout", "Both games lay the shop's shelves out the same way: every display at the same place")]
    [Budget(180)]
    public sealed class ShopLayout : Scenario<ShopLayout.Options>
    {
        public sealed class Options
        {
            /// <summary>Largest allowed distance between a display on the two games, in metres.</summary>
            public float Tolerance = 0.01f;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Places.Shop(pair, 3f);
            Display[] host = await pair.Host.Run(() => InGame.Displays());
            Display[] client = await pair.Client.Run(() => InGame.Displays());
            Report.Note($"host has {host.Length} displays, client {client.Length}");

            var hostByKey = host.ToDictionary(d => (d.Shop, d.Module));
            int compared = 0, moved = 0;
            float worst = 0f;
            foreach (Display c in client)
            {
                if (!hostByKey.TryGetValue((c.Shop, c.Module), out Display h))
                    continue;
                compared++;
                float dx = h.X - c.X, dy = h.Y - c.Y, dz = h.Z - c.Z;
                float distance = (float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
                worst = System.Math.Max(worst, distance);
                if (distance > o.Tolerance)
                {
                    moved++;
                    if (moved <= 5)
                        Report.Note($"shop {c.Shop} {c.Name}: host ({h.X:F2}, {h.Y:F2}, {h.Z:F2}), client ({c.X:F2}, {c.Y:F2}, {c.Z:F2}), {distance:F2} m apart");
                }
            }
            Report.Note($"compared {compared} displays, {moved} in different places, worst {worst:F3} m");
            Require.That(compared > 0, "both games have shop displays to compare");
            Expect.Zero(moved, "displays in different places on host and client");
        }

        public sealed class Display
        {
            public int Shop, Module;
            public string Name;
            public float X, Y, Z;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Every shop's displays: shop index, module index, name and absolute position.</summary>
            [Step]
            public static Display[] Displays()
            {
                var shops = GlobalShopController.Instance.globalShopList;
                var result = new List<Display>();
                for (int s = 0; s < shops.Count; s++)
                {
                    ScanItemCashRegisterModule[] modules = shops[s]?.scanItemResourceModules;
                    if (modules == null)
                        continue;
                    for (int m = 0; m < modules.Length; m++)
                    {
                        if (modules[m] == null)
                            continue;
                        Vector3 at = Dev.Abs(modules[m].transform.position);
                        result.Add(new Display { Shop = s, Module = m, Name = modules[m].name, X = at.x, Y = at.y, Z = at.z });
                    }
                }
                return result.ToArray();
            }
        }
    }
}
