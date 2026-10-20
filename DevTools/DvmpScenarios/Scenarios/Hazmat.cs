using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Hazmat ground tiles (fire and spills): the host spills gasoline and sets it alight, the client
    /// sets a second spill alight, and the client's own spill must do nothing (the ground is the
    /// host's). Both peers must show the same tiles, the same burning tiles with flames, and about the
    /// same liquid (it is sent rounded, and burns between updates).
    /// </summary>
    [Scenario("hazmat", "Spills and ground fires: the host's spill and fire, and a fire the client lights, show the same tiles, flames and about the same liquid on both; the client's own spill does nothing")]
    [Budget(300)]
    public sealed class Hazmat : Scenario<Hazmat.Options>
    {
        public sealed class Options { }

        // Spots away from the gate's other scenarios.
        private const float X1 = 7260, X2 = 7290, X3 = 7320, Z = 6960;

        protected override async Task Run(Pair pair, Options o)
        {
            if (!pair.Dedicated) await pair.Host.Run(() => Players.TeleportToGround(7275f, 6940f));
            await pair.Client.Run(() => Players.TeleportToGround(7280f, 6940f));

            Report.Note("== host spills 5000 L of gasoline");
            await pair.Host.Run(() => InGame.Spill(X1, Z, 5000f, "Gasoline"));
            await Task.Delay(5000);   // the spill spreads and is sent, as before
            Tiles t = await Check("spill", X1);
            Require.AtLeast(t.Count, 1, "tiles from the spill");

            Report.Note("== host sets it alight");
            await pair.Host.Run(() => InGame.Ignite(X1, Z));
            await Task.Delay(6000);
            t = await Check("host's fire", X1);
            Require.AtLeast(t.Burning, 1, "tiles burning");

            Report.Note("== host spills again; the client sets that one alight");
            await pair.Host.Run(() => InGame.Spill(X2, Z, 5000f, "Gasoline"));
            await Task.Delay(4000);
            await pair.Client.Run(() => InGame.Ignite(X2, Z));
            await Task.Delay(6000);
            t = await Check("client's fire", X2);
            Require.AtLeast(t.Burning, 1, "tiles burning from the client's ignition, on the host");

            Report.Note("== the client spills on its own");
            await pair.Client.Run(() => InGame.Spill(X3, Z, 5000f, "Gasoline"));
            await Task.Delay(4000);
            await Check("client's own spill", X3);

            // The two games are read one after the other, while a fire may still be spreading: a tile
            // that catches on the host just after its read reaches the client (0.5 s sends) before the
            // client's read, so the client looks a tile ahead (and, burning longer, has a little less
            // liquid, which it always does). A mismatch is read again, up to 4 more times a second
            // apart; a real difference doesn't go away. Result: the host's tiles.
            async Task<Tiles> Check(string what, float x)
            {
                Tiles h = null, c = null;
                List<string> bad = null;
                for (int i = 1; i <= 5; i++)
                {
                    h = await pair.Host.Run(() => InGame.Near(x, Z));
                    c = await pair.Client.Run(() => InGame.Near(x, Z));
                    bad = Differences(h, c);
                    if (bad.Count == 0) break;
                    if (i < 5)
                    {
                        Report.Note($"{what}: {Show(h, c)} - {string.Join("; ", bad)} (read {i}, settling)");
                        await Task.Delay(1000);
                    }
                }
                Expect.That(bad.Count == 0, $"{what}: the same on both", Show(h, c) + (bad.Count > 0 ? " - " + string.Join("; ", bad) : ""));
                return h;
            }
        }

        private static List<string> Differences(Tiles h, Tiles c)
        {
            List<string> bad = new();
            if (h.Count != c.Count) bad.Add($"tiles {h.Count}/{c.Count}");
            if (h.Burning != c.Burning || h.Flames != c.Flames || c.Flames != c.Burning) bad.Add($"burning {h.Burning}/{c.Burning}, flames {h.Flames}/{c.Flames}");
            if (Math.Abs(h.Liquid - c.Liquid) > Math.Max(200, 0.1 * h.Liquid)) bad.Add($"liquid {h.Liquid}/{c.Liquid}");
            return bad;
        }

        private static string Show(Tiles h, Tiles c) =>
            $"tiles {h.Count}/{c.Count}, liquid {h.Liquid}/{c.Liquid} L, burning {h.Burning}/{c.Burning}, flames {h.Flames}/{c.Flames} (host/client)";

        public sealed class Tiles
        {
            public int Count, Burning, Flames;
            public float Liquid;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host or client: pours <paramref name="amount"/> litres of a cargo onto the
            /// ground at an absolute x/z.</summary>
            [Step]
            public static IEnumerator Spill(float x, float z, float amount, string cargo)
            {
                var m = DV.Utils.SingletonBehaviour<HazmatTileManager>.Instance;
                m.UpdateTileLiquidToBeAddedDictionary(new Vector3(x, 0, z) + WorldMover.currentMove, amount,
                    (DV.ThingTypes.CargoType)Enum.Parse(typeof(DV.ThingTypes.CargoType), cargo));
                for (int i = 0; i < 10; i++) yield return null;
            }

            /// <summary>Either peer: sets the tile at an absolute x/z alight.</summary>
            [Step]
            public static IEnumerator Ignite(float x, float z)
            {
                var m = DV.Utils.SingletonBehaviour<HazmatTileManager>.Instance;
                m.IgniteTile(m.GetGridPositionFromWorldPosition(new Vector3(x, 0, z) + WorldMover.currentMove), 100000f);
                for (int i = 0; i < 10; i++) yield return null;
            }

            /// <summary>Either peer: the hazmat tiles within 60 m of an absolute x/z: how many, their
            /// liquid, how many burn, and how many burning tiles have fire effects.</summary>
            [Step]
            public static Tiles Near(float x, float z)
            {
                var m = DV.Utils.SingletonBehaviour<HazmatTileManager>.Instance;
                var near = m.TileDictionary.Values.Where(t =>
                {
                    var p = m.GetWorldPositionFromGridCoords(m.GetTileCoordX(t.gridPosition), m.GetTileCoordY(t.gridPosition), false);
                    return new Vector2(p.x - x, p.z - z).magnitude < 60f;
                }).ToList();
                return new Tiles
                {
                    Count = near.Count,
                    Liquid = (float)Math.Round(near.Sum(t => t.liquidContent.Values.Sum())),
                    Burning = near.Count(t => t.IsIgnited),
                    Flames = near.Count(t => t.IsIgnited && t.terrainFireEffects != null)
                };
            }
        }
    }
}
