using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.World;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A junction or turntable the server's own world moves, with nobody's request behind it (what a
    /// mod such as the AI add-on does on a dedicated host), reaches the client once
    /// (docs/world-sync.md, junctions and turntables). The server switches the junction nearest the
    /// client to another branch in code and turns the nearest turntable half round by setting its
    /// target; the client's junction ends on the same branch having switched exactly once, the
    /// host's switched exactly once (nothing came back), and the client's turntable has the host's
    /// rotation. Both are put back afterwards the same way. On a player host and a dedicated host,
    /// with or without `--local-session`: before the server broadcast these itself they went out
    /// through the host's own client, so with no client on the host nothing reached the client.
    /// `host-actions` covers a player's throw (the client's, or the host player's).
    /// </summary>
    [Scenario("server-world-changes", "A junction and a turntable the server's own code moves reach the client once")]
    [Budget(180)]
    public sealed class ServerWorldChanges : Scenario<ServerWorldChanges.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            Picked picked = await pair.Client.Run(() => InGame.Pick());
            Report.Note($"junction {picked.Junction} ({picked.JunctionDistance:0} m from the client), turntable {picked.Turntable} ({picked.TurntableDistance:0} m)");

            // ---- the junction
            await pair.Both.Run(() => InGame.WatchJunction(picked.Junction));
            PeerResults<int> before = await pair.Both.Run(() => InGame.Branch(picked.Junction));
            Require.Equal(before.Host, before.Client, "the junction's branch on the client before the test");
            int branch = await pair.Host.Run<int>(s => InGame.ServerSwitch(s, picked.Junction, -1));
            Require.That(branch != before.Host, "the server's junction switched to another branch", $"{before.Host} -> {branch}");
            pair.Cleanup(() => pair.Host.Run<int>(s => InGame.ServerSwitch(s, picked.Junction, before.Host)));
            await Until.Value(() => pair.Client.Run(() => InGame.Branch(picked.Junction)), b => b == branch, 10,
                "the client's junction on the server's branch");
            await Task.Delay(2000);   // a second switch, an echo or a relay twice, would come by now
            PeerResults<int> switched = await pair.Both.Run(() => InGame.Switched(picked.Junction));
            Expect.Equal(1, switched.Host, "times the host's junction switched");
            Expect.Equal(1, switched.Client, "times the client's junction switched");

            // ---- the turntable
            if (picked.Turntable == 0)
            {
                Report.Note("no turntable: skipped");
                return;
            }
            float start = await pair.Host.Run(() => InGame.Rotation(picked.Turntable));
            float turned = await pair.Host.Run<float>(s => InGame.ServerTurn(s, picked.Turntable, start + 180f));
            pair.Cleanup(() => pair.Host.Run<float>(s => InGame.ServerTurn(s, picked.Turntable, start)));
            Report.Note($"the server turned it {start:0.#} -> {turned:0.#} degrees");
            Require.That(Degrees.Apart(start, turned) > 1f, "the server's turntable turned");
            await Until.Value(() => pair.Client.Run(() => InGame.Rotation(picked.Turntable)), r => Degrees.Apart(r, turned) < 0.5f, 10,
                "the client's turntable at the server's rotation (degrees)");
        }

        public sealed class Picked
        {
            public ushort Junction;
            public byte Turntable;
            public float JunctionDistance, TurntableDistance;
        }

        // ---- runs in the orchestrator
        private static class Degrees
        {
            public static float Apart(float a, float b)
            {
                float d = System.Math.Abs(a - b) % 360f;
                return System.Math.Min(d, 360f - d);
            }
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static readonly Dictionary<ushort, int> switches = new();
            private static readonly HashSet<ushort> watched = new();

            /// <summary>Client: the junction and turntable nearest its player, by NetId (every game
            /// numbers them the same way); turntable 0 if there's none.</summary>
            [Step]
            public static Picked Pick()
            {
                Vector3 player = Dev.Abs(PlayerManager.PlayerTransform.position);
                NetworkedJunction j = NetworkedJunction.IndexedJunctions.Where(n => n != null && n.Junction != null && n.Junction.outBranches.Count > 1)
                    .OrderBy(n => Vector3.Distance(Dev.Abs(n.transform.position), player)).First();
                NetworkedTurntable t = NetworkedTurntable.IndexedTurntables.Where(n => n != null && n.TurntableRailTrack != null)
                    .OrderBy(n => Vector3.Distance(Dev.Abs(n.transform.position), player)).FirstOrDefault();
                return new Picked
                {
                    Junction = j.NetId,
                    JunctionDistance = Vector3.Distance(Dev.Abs(j.transform.position), player),
                    Turntable = t == null ? (byte)0 : t.NetId,
                    TurntableDistance = t == null ? -1f : Vector3.Distance(Dev.Abs(t.transform.position), player),
                };
            }

            /// <summary>Either peer: counts the junction's switches from now (each raises
            /// <c>Junction.Switched</c> once, so a change applied twice counts 2).</summary>
            [Step]
            public static bool WatchJunction(ushort junction)
            {
                NetworkedJunction j = FindJunction(junction);
                if (watched.Add(junction))
                    j.Junction.Switched += (mode, branch) => switches[junction] = (switches.TryGetValue(junction, out int n) ? n : 0) + 1;
                switches[junction] = 0;
                return true;
            }

            /// <summary>Either peer: how many times the junction switched since <see cref="WatchJunction"/>.</summary>
            [Step]
            public static int Switched(ushort junction) => switches.TryGetValue(junction, out int n) ? n : 0;

            /// <summary>Either peer: the junction's selected branch.</summary>
            [Step]
            public static int Branch(ushort junction) => FindJunction(junction).Junction.selectedBranch;

            /// <summary>Host: switches the junction in the server's own world, as code does, with no
            /// player's request: to <paramref name="branch"/>, or the next branch for -1. Returns the
            /// branch it's on.</summary>
            [Step]
            public static IEnumerator ServerSwitch(Step<int> step, ushort junction, int branch)
            {
                Junction j = FindJunction(junction).Junction;
                int to = branch >= 0 ? branch : (j.selectedBranch + 1) % j.outBranches.Count;
                j.Switch(Junction.SwitchMode.REGULAR, (byte)to);
                yield return new WaitForSecondsRealtime(0.5f);
                step.Result = j.selectedBranch;
            }

            /// <summary>Either peer: the turntable's target rotation (degrees).</summary>
            [Step]
            public static float Rotation(byte turntable) => FindTurntable(turntable).TurntableRailTrack.targetYRotation;

            /// <summary>Host: turns the turntable in the server's own world by setting its target, as
            /// code does, with no player at its controls. <c>NetworkedTurntable</c> sends a changed
            /// target from its next tick. Returns the target it has.</summary>
            [Step]
            public static IEnumerator ServerTurn(Step<float> step, byte turntable, float degrees)
            {
                TurntableRailTrack t = FindTurntable(turntable).TurntableRailTrack;
                t.targetYRotation = TurntableRailTrack.AngleRange0To360(degrees);
                t.RotateToTargetRotation();
                yield return new WaitForSecondsRealtime(1f);
                step.Result = t.targetYRotation;
            }

            private static NetworkedJunction FindJunction(ushort netId) =>
                NetworkedJunction.Get(netId, out NetworkedJunction j) ? j : throw new System.InvalidOperationException("no junction " + netId);

            private static NetworkedTurntable FindTurntable(byte netId) =>
                NetworkedTurntable.Get(netId, out NetworkedTurntable t) ? t : throw new System.InvalidOperationException("no turntable " + netId);
        }
    }
}
