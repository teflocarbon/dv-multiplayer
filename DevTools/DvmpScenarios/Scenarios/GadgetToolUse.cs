using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization.Gadgets;
using Multiplayer.Shared.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Other players see and hear gadget tools work (gadgets.md, PR 11). The server puts a drill and a
    /// soldering iron by the client. The client works each in its hand for a few seconds, then stops: on a
    /// player host the host's game shows that tool working, then stopped; on a dedicated host (no game of
    /// its own to show it in) the server's relay holds it working, then stopped.
    /// </summary>
    [Scenario("gadget-tool-use", "Another player's drill and soldering iron are seen working while they use them, and stopped after")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class GadgetToolUse : Scenario<GadgetToolUse.Options>
    {
        public sealed class Options
        {
            public float Seconds = 5f;
            public int Lag;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Kit kit = await pair.Host.Run<Kit>(s => InGame.SpawnTools(s));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(kit.PersistentIds)));
            Report.Note(kit.Note);
            using (await pair.Client.Lag(o.Lag))
            {
                foreach ((ushort tool, string name) in new[] { (kit.Drill, "drill"), (kit.Iron, "soldering iron") })
                {
                    if (tool == 0)
                        continue;
                    await pair.Client.Run(() => Items.Pickup(tool), 60);
                    Require.Equal(tool, await pair.Client.Run<ushort>(() => Items.Held()), $"the client holds the {name}");
                    await pair.Client.Run(() => InGame.Press(tool, true));
                    Expect.Equal(true, await pair.Host.Run<bool>(s => InGame.SeenWhen(s, tool, true, o.Seconds)),
                        $"the client works the {name}: seen working by the {(pair.Dedicated ? "server" : "host's game")}");
                    // Still working after the heartbeat's expiry: the presence is kept alive while it works.
                    await Task.Delay(3500);
                    Expect.Equal(true, await pair.Host.Run<bool>(s => InGame.SeenWhen(s, tool, true, 1f)),
                        $"the client keeps working the {name}: still seen working");
                    await pair.Client.Run(() => InGame.Press(tool, false));
                    Expect.Equal(false, await pair.Host.Run<bool>(s => InGame.SeenWhen(s, tool, false, o.Seconds)),
                        $"the client stops the {name}: seen stopped");
                    await pair.Client.Run(() => Items.Drop());
                }
            }
        }

        public sealed class Kit
        {
            public ushort Drill, Iron;
            public string[] PersistentIds;
            public string Note;
        }

        // ---- runs inside the games
        public static class InGame
        {
            [Step]
            public static IEnumerator SpawnTools(Step<Kit> step)
            {
                var client = Gadgets.Client();
                Vector3 beside = client.WorldPosition + Vector3.right * 0.8f + Vector3.up * 0.6f;
                InventoryItemSpec Spec(Func<InventoryItemSpec, bool> fits) => DV.Globals.G.Items.items
                    .Where(s => s != null && fits(s)).OrderBy(s => s.ItemPrefabName, StringComparer.Ordinal).FirstOrDefault();
                NetworkedItem drill = null, iron = null;
                if (Spec(s => s.GetComponentInChildren<DrillTool>(true) != null) is { } drillSpec)
                    yield return Gadgets.SpawnOwned(drillSpec, beside, client, i => drill = i);
                if (Spec(s => s.GetComponentInChildren<GadgetSolderingTool>(true) != null) is { } ironSpec)
                    yield return Gadgets.SpawnOwned(ironSpec, beside + Vector3.forward * 0.5f, client, i => iron = i);
                step.Result = new Kit
                {
                    Drill = drill != null ? drill.NetId : (ushort)0,
                    Iron = iron != null ? iron.NetId : (ushort)0,
                    PersistentIds = new[] { drill, iron }.Where(i => i != null)
                        .Select(i => i.GetComponent<Multiplayer.Server.Items.PersistentWorldItemIdentity>().Value.ToString("D")).ToArray(),
                    Note = $"drill {drill?.name ?? "none"}, iron {iron?.name ?? "none"}"
                };
            }

            /// <summary>Presses (or lets go of) the held tool's use, as the player's input does.</summary>
            [Step]
            public static void Press(ushort tool, bool pressed) =>
                Items.Find(tool).GetComponentInChildren<GadgetInteractor>(true).IsPressed = pressed;

            /// <summary>Host: whether the tool is seen working (the host's game on a player host, the
            /// server's relay on a dedicated one), once that is <paramref name="expected"/> or after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator SeenWhen(Step<bool> step, ushort tool, bool expected, float seconds)
            {
                bool dedicated = Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsDedicatedHost;
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    step.Result = dedicated
                        ? Multiplayer.Server.Gadgets.GadgetToolPresenceServer.IsWorking(tool)
                        : Multiplayer.Client.Gadgets.GadgetToolPresence.ShowsWorking(tool);
                    if (step.Result == expected)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }
        }
    }
}
