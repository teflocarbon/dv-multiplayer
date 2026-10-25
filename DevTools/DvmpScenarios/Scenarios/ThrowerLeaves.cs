using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// OI-PHY-07 (F32): on a dedicated host the client throws a mug high and its game is killed while
    /// the mug flies. Its samples stop, and the lease goes to whoever can simulate it. With no other
    /// player, before R7 that was nobody: the host committed the last sample and the mug hung in
    /// mid-air where the thrower's game last had it. Now the server takes the lease itself (its world
    /// stays loaded where the client was) and the mug falls and settles on the ground: its lease
    /// ends "settled:host-settlement", it is within half a metre of the ground, and its record is
    /// where the host's copy is. The client rejoins after.
    /// </summary>
    [Scenario("thrower-leaves", "Dedicated host: a mug in flight when its thrower's game dies is simulated by the server and comes to rest on the ground, not in mid-air")]
    [Needs(Need.DedicatedHost, Need.RestartsGames)]
    [Budget(600)]
    public sealed class ThrowerLeaves : Scenario<ThrowerLeaves.Options>
    {
        public sealed class Options
        {
            /// <summary>The thrown item's prefab.</summary>
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            Running<Landing> watch = pair.Host.Start<Landing>(s => InGame.Watch(s, item, 60f), 120);
            await pair.Client.Run(() => InGame.ThrowHigh(item), 90);
            await pair.Client.Kill();
            try
            {
                Landing l = await watch;
                Report.Note($"leased {l.Leased} (simulator {l.FirstSimulator}, then {l.LastSimulator}), lease ended {(l.Ended.Length > 0 ? l.Ended : "never")} after {l.Seconds:0.#} s; " +
                            $"{l.Placement} r{l.Revision}, {l.AboveGround:0.##} m above the ground, {l.FromRecord:0.##} m from its record");
                Require.That(l.Leased, "the throw opened a lease (it flew)");
                Expect.Equal(0, l.LastSimulator, "the lease's last simulator (the server itself)");
                Expect.Equal("settled:host-settlement", l.Ended, "what ended its lease (the server's own settlement, not a frozen sample)");
                Expect.AtMost(l.AboveGround, 0.5, "metres above the ground where it came to rest");
                Expect.Equal("World", l.Placement, "where its record has it");
                Expect.AtMost(l.FromRecord, 0.3, "metres from the host's copy to its record");
            }
            finally
            {
                await pair.Rejoin();
            }
        }

        public sealed class Landing
        {
            public bool Leased;
            public int FirstSimulator = -1, LastSimulator = -1;
            public string Ended = "", Placement = "";
            public uint Revision;
            public float Seconds, AboveGround = -1f, FromRecord = -1f;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: picks the item up and throws it straight up, hard: three seconds of flight.</summary>
            [Step]
            public static IEnumerator ThrowHigh(ushort id)
            {
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                for (int i = 0; i < 20; i++) yield return null;
                yield return Hands.Throw((PlayerManager.PlayerTransform.forward * 0.2f + Vector3.up).normalized);
                Rigidbody body = item.Item?.ItemRigidbody;
                if (body != null && !body.isKinematic)
                    body.velocity = Vector3.up * 15f + PlayerManager.PlayerTransform.forward;
            }

            /// <summary>
            /// Host: watches the item's lease (who simulates it, why it ended) until it ends and the
            /// item rests, then measures it against the ground and its record.
            /// </summary>
            [Step]
            public static IEnumerator Watch(Step<Landing> step, ushort id, float seconds)
            {
                var manager = NetworkedItemManager.Instance;
                Landing l = new();
                string endedBefore = manager.Spatial.LastLeaseEnd(id);
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < seconds)
                {
                    yield return null;
                    if (manager.Spatial.TryGetActiveLeaseIdentity(id, out _, out _, out byte simulator, out _))
                    {
                        if (!l.Leased) l.FirstSimulator = simulator;
                        l.Leased = true;
                        l.LastSimulator = simulator;
                        continue;
                    }
                    string ended = manager.Spatial.LastLeaseEnd(id);
                    if (l.Leased && ended != endedBefore)
                    {
                        l.Ended = ended;
                        break;
                    }
                }
                l.Seconds = Time.realtimeSinceStartup - t0;
                yield return new WaitForSecondsRealtime(2f);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (item != null)
                {
                    Vector3 at = item.transform.position;
                    var own = item.GetComponentsInChildren<Collider>(true);
                    var hits = Physics.RaycastAll(at + Vector3.up * 0.5f, Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore)
                        .Where(h => h.collider != null && !own.Contains(h.collider)).OrderBy(h => h.distance).ToArray();
                    l.AboveGround = hits.Length > 0 ? hits[0].distance - 0.5f : 999f;
                    if (LiveItems.TryGet(id, out LiveItems.LiveItem record))
                    {
                        l.Placement = record.Placement.ToString();
                        l.Revision = record.Revision;
                        l.FromRecord = Vector3.Distance(at - WorldMover.currentMove, record.Position);
                    }
                }
                step.Result = l;
            }
        }
    }
}
