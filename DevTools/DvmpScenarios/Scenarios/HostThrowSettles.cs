using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The host throws an item itself (no hand-off to a client): the host simulates it under its own
    /// spatial lease and commits where it comes to rest. It must settle at the same spot in both
    /// games, with the same revision, no lease left open, and the host's copy's revision never going
    /// back. Nothing about it may reach the host's own client: the server leaves that client out of
    /// the lease, samples and commit (HostOwnClient drops them if they come, and counts as a failure
    /// here). This is the host-follower family (docs/design/host-local-session.md, stage 1): the
    /// host's loopback client applying what the server sent for remote players to the host's own
    /// world. A dedicated host's parked body doesn't throw: player host only.
    /// </summary>
    [Scenario("host-throw-settles", "The host throws an item itself: it settles at the same spot and revision in both games, no lease left open, the host's revision never goes back, and nothing reaches the host's own client")]
    [Needs(Need.PlayerHost)]
    public sealed class HostThrowSettles : Scenario<HostThrowSettles.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab.</summary>
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool watching = await pair.Host.Run(() => InGame.WatchIgnored(true));
            pair.Cleanup(() => pair.Host.Run(() => InGame.WatchIgnored(false)));
            if (!watching) Report.Note("the host has no NetworkClient.IgnoreOnHost (no loopback client): nothing to drop, not checked");

            // The client comes over to watch.
            Abs host = await pair.Host.Run(() => InGame.PlayerAt());
            await pair.Client.Run(() => Players.TeleportToGround(host.X - 6f, host.Z));

            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "host"));
            Thrown t = await pair.Host.Run<Thrown>(s => InGame.Throw(s, item), 90);
            Report.Note($"lease seen {t.LeaseSeen}, settled {t.Settled} after {t.Seconds:0.#} s, revisions {t.Revisions} (held at r{t.HeldRevision})");
            await Task.Delay(2000);   // the commit reaches the client

            PeerResults<ItemPose> pose = await pair.Both.Run(() => Items.Pose(item));
            ItemRecord record = await pair.Host.Run(() => Items.Record(item));
            Ignored ignored = await pair.Host.Run(() => InGame.ReadIgnored());
            Report.Note($"host {pose.Host} ({record.Placement} r{record.Revision}), client {pose.Client}; host client dropped: {(ignored.ByType.Length > 0 ? ignored.ByType : "nothing")}");

            Expect.That(t.LeaseSeen, "the throw opened a spatial lease");
            Expect.That(t.Settled, "the host saw it settle", $"{t.Seconds:0.#} s");
            Expect.Zero(t.WentBack, $"times the host copy's revision went back ({t.Revisions})");
            foreach (var (peer, v) in pose)
            {
                if (!Expect.That(v.Present, $"{peer}: has the item")) continue;
                Expect.That(!v.Leased, $"{peer}: no spatial lease still open");
                Expect.Equal("Dropped", v.State, $"{peer}: item state");
            }
            if (pose.Host.Present)
            {
                Expect.Equal("World", record.Placement, "host record placement");
                if (record.Known) Expect.AtMost(Peers.Distance(pose.Host.Pos, record.Pos), 0.3, "host copy from its record (m)");
            }
            if (pose.Host.Present && pose.Client.Present)
            {
                Expect.AtMost(Peers.Distance(pose.Host.Pos, pose.Client.Pos), 0.3, "host/client positions apart (m)");
                Expect.Equal(pose.Host.Revision, pose.Client.Revision, "client revision (the host's)");
            }
            Expect.Zero(ignored.Items, $"item packets the host's own client was sent and dropped ({ignored.ByType})");
        }

        public sealed class Thrown
        {
            public uint HeldRevision;
            public bool LeaseSeen, Settled;
            public int WentBack;
            public string Revisions;
            public float Seconds;
        }

        public sealed class Ignored
        {
            public int Total, Items;
            public string ByType = "";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string HarmonyId = "dvmp.host-throw-settles.ignored";
            private static readonly Dictionary<string, int> ignored = new();

            /// <summary>
            /// Host: counts (true) or stops counting (false) the packets the host's own client drops
            /// (NetworkClient.IgnoreOnHost: the server sent the host's client something it must never
            /// follow), by type. Named by string: stage 3 removes the host's client, and with it this
            /// method. Result: whether it's there to watch.
            /// </summary>
            [Step]
            public static bool WatchIgnored(bool on)
            {
                var harmony = new Harmony(HarmonyId);
                harmony.UnpatchAll(HarmonyId);
                lock (ignored) ignored.Clear();
                var method = AccessTools.Method("Multiplayer.Client.Session.NetworkClient:IgnoreOnHost");
                if (on && method != null)
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(OnIgnored))));
                return method != null;
            }

            public static void OnIgnored(Type packetType)
            {
                lock (ignored)
                {
                    string name = packetType?.Name ?? "null";
                    ignored.TryGetValue(name, out int n);
                    ignored[name] = n + 1;
                }
            }

            [Step]
            public static Ignored ReadIgnored()
            {
                lock (ignored)
                    return new Ignored
                    {
                        Total = ignored.Values.Sum(),
                        Items = ignored.Where(p => p.Key.Contains("Item")).Sum(p => p.Value),
                        ByType = string.Join(",", ignored.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value))
                    };
            }

            /// <summary>Where this game's player stands.</summary>
            [Step]
            public static Abs PlayerAt() => Positions.Of(PlayerManager.PlayerTransform.position);

            /// <summary>
            /// Host: picks the item up and throws it, then watches it until it settles: the host's own
            /// copy is simulated here under a spatial lease and committed when it comes to rest.
            /// Records every revision the host's copy shows, so a revision that goes back (a stale
            /// echo applied) shows up.
            /// </summary>
            [Step]
            public static IEnumerator Throw(Step<Thrown> step, ushort id)
            {
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                for (int i = 0; i < 20; i++) yield return null;
                uint heldRevision = item.AuthorityRevision;
                Vector3 dir = (PlayerManager.PlayerTransform.forward + Vector3.up * 0.5f).normalized;
                yield return Hands.Throw(dir);
                var manager = NetworkedItemManager.Instance;
                var revisions = new List<uint> { item.AuthorityRevision };
                bool leaseSeen = false, settled = false;
                int wentBack = 0;
                float restSince = -1f, t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 15f)
                {
                    yield return null;
                    if (item == null) throw new Exception("item " + id + " was destroyed in flight");
                    uint revision = item.AuthorityRevision;
                    if (revision < revisions[revisions.Count - 1]) wentBack++;
                    if (revision != revisions[revisions.Count - 1]) revisions.Add(revision);
                    bool leased = manager.HasActiveSpatialState(id);
                    leaseSeen |= leased;
                    Rigidbody body = item.Item?.ItemRigidbody;
                    bool resting = !leased && (body == null || body.IsSleeping() || body.isKinematic || body.velocity.sqrMagnitude < 0.0025f);
                    if (!resting) { restSince = -1f; continue; }
                    if (restSince < 0f) restSince = Time.realtimeSinceStartup;
                    if (leaseSeen && Time.realtimeSinceStartup - restSince >= 1f) { settled = true; break; }
                }
                step.Result = new Thrown
                {
                    HeldRevision = heldRevision, LeaseSeen = leaseSeen, Settled = settled, WentBack = wentBack,
                    Revisions = string.Join(",", revisions), Seconds = Time.realtimeSinceStartup - t0
                };
            }
        }
    }
}
