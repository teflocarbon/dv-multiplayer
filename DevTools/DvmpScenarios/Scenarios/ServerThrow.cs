using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The host throws an item itself, naming a player as the throw's actor, as the job re-roll
    /// throws a binned job overview back out: the actor drops a mug in a loco cab and, while the
    /// lease of that drop is still open, the host throws it out of the side of the loco into the
    /// world. Its flight must survive:
    /// - the transition indexes the mug in its world cell at once (moved from a car interior into
    ///   the world it used to be in no cell until a spatial sample came, and every player's interest
    ///   retired it and re-created it at rest);
    /// - the throw keeps its force when the actor is the host's own player (granting the new lease
    ///   used to stop the body under the old one, losing GrabHandlerItem.Throw's one-step force).
    /// Both games must then have it at the same spot outside the loco, no lease open, the client's
    /// copy never retired and re-created. ACTOR=host needs a player host (the host's body boards).
    /// </summary>
    [Scenario("server-throw", "The host throws a mug out of a loco cab on a player's behalf under the drop's lease: it's indexed in its world cell at once, keeps its force, and lands at the same spot in both games")]
    public sealed class ServerThrow : Scenario<ServerThrow.Options>
    {
        public sealed class Options
        {
            /// <summary>The throw's actor, who drops the mug in the cab: host or client.</summary>
            public string Actor = "host";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Actor == "host" && pair.Dedicated) throw new HarnessException("ACTOR=host needs a player host: the host's body boards the loco");
            Peer actor = o.Actor == "host" ? pair.Host : pair.Client;
            // Around the host's player, as the bash scenario did (on a dedicated host, its player
            // transform follows the client).
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "host"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));
            // Both on board: the onlooker is near enough to have the mug in its interest all along.
            if (!pair.Dedicated) await pair.Host.Run(() => Players.Board(loco));
            await pair.Client.Run(() => Players.Board(loco));

            ushort mug = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", o.Actor));
            ItemPose before = await pair.Client.Run(() => Items.Pose(mug));
            // The host throws it out under the drop's lease: it watches for the drop while the actor makes it.
            var throwing = pair.Host.Start<Thrown>(s => InGame.Throw(s, mug, loco, o.Actor), 120);
            await actor.Run<float>(s => Items.CarryIntoCab(s, loco, mug, "drop", 0), 90);
            Thrown t = await throwing;
            await Task.Delay(3000);   // it lands, and the landing reaches the client

            PeerResults<ItemPose> pose = await pair.Both.Run(() => Items.Pose(mug));
            ItemRecord record = await pair.Host.Run(() => Items.Record(mug));
            Report.Note($"flew {t.Travel:0.##} m (rose {t.Rise:0.##} m); host {pose.Host} ({record.Placement}, last move {record.LastMove}), client {pose.Client}");

            Expect.That(t.LeaseLiveAtThrow, "a lease from the drop was open when the host threw it (else the throw superseded none, and this run shows nothing about its force)",
                $"{t.DroppedFor:0.#} s after it was in the cab");
            Expect.That(t.IndexedOnCommit, "the transition put the mug in a world cell");
            // Rescued from below the map, it was put back where it was thrown and fell straight down
            // from there: where it lies says nothing about the force of the throw.
            bool rescued = pose.Host.Present && record.LastMove == "RescuedFromBelowMap";
            if (rescued) Expect.That(false, "the mug stayed above the ground", $"fell through after flying {t.Travel:0.##} m sideways and was rescued from below the map");
            else Expect.AtLeast(t.Travel, 1.5, "the host's copy's flight sideways (m), less is a throw that lost its force");
            foreach (var (peer, v) in pose)
            {
                if (!Expect.That(v.Present, $"{peer}: has the mug")) continue;
                Expect.That(!v.Leased, $"{peer}: no spatial lease still open");
                Expect.Equal("Dropped", v.State, $"{peer}: mug state");
                if (!rescued) Expect.AtLeast(Peers.Horizontal(v.Pos, t.Start), 1.5, $"{peer}: came down from where it was thrown (m)");
            }
            if (pose.Host.Present) Expect.Equal("World", record.Placement, "host record placement");
            if (before.Present && pose.Client.Present) Expect.Equal(before.Instance, pose.Client.Instance, "the client's copy (its Unity instance: another is one retired and re-created)");
            if (pose.Host.Present && pose.Client.Present)
            {
                Expect.AtMost(Peers.Distance(pose.Host.Pos, pose.Client.Pos), 0.3, "host/client positions apart (m)");
                Expect.Equal(pose.Host.Revision, pose.Client.Revision, "client revision (the host's)");
            }
        }

        public sealed class Thrown
        {
            public float DroppedFor, Travel, Rise;
            public bool LeaseLiveAtThrow, IndexedOnCommit;
            public Abs Start;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: once the mug lies in the cab under the lease its drop opened, throws it out of the
            /// side of the loco with a host transition naming the actor, as the job re-roll throws a
            /// binned overview back out (Server/Jobs/JobReroll.cs), but without that code's
            /// workarounds: no RegisterHostWorldItem after it, no velocity set on the new lease. Then
            /// watches the host's copy fly.
            /// </summary>
            [Step]
            public static IEnumerator Throw(Step<Thrown> step, ushort id, string loco, string actorName)
            {
                var server = NetworkLifecycle.Instance.Server;
                ServerPlayer actor = actorName == "host" ? server.LocalPlayer : server.ServerPlayers.First(p => p != server.LocalPlayer);
                var spatial = NetworkedItemManager.Instance.Spatial;
                NetworkedItem item = null;
                AuthoritativeItemRegistry.Record record = null;
                bool leased = false, held = false;
                float t0 = Time.realtimeSinceStartup, inCabAt = -1f;
                while (Time.realtimeSinceStartup - t0 < 60f)
                {
                    yield return null;
                    if (item == null) NetworkedItem.TryGet(id, out item);
                    if (item == null || !AuthoritativeItemRegistry.TryGet(id, out record)) continue;
                    // The mug is spawned at the actor's feet in the cab and may already lie there,
                    // recorded in it: only a cab placement after the actor has picked it up is the drop.
                    if (record.Placement == ItemPlacementKind.PlayerHand) held = true;
                    if (!held) continue;
                    if (record.Placement != ItemPlacementKind.TrainInterior)
                    {
                        if (inCabAt >= 0f) break;   // it left the cab on its own
                        continue;
                    }
                    if (inCabAt < 0f) inCabAt = Time.realtimeSinceStartup;
                    // The drop's lease begins a frame or so after its transition: thrown before, the
                    // throw's lease would be the only one, superseding nothing.
                    leased = spatial.TryGetActiveLeaseIdentity(id, out _, out _, out byte simulator, out _) && simulator == actor.PlayerId;
                    if (leased || Time.realtimeSinceStartup - inCabAt > 2f) break;
                }
                if (!held)
                    throw new Exception($"the mug was never recorded in the {actorName}'s hand ({record?.Placement.ToString() ?? "no record"})");
                if (record.Placement != ItemPlacementKind.TrainInterior)
                    throw new Exception(inCabAt >= 0f
                        ? $"the mug left the cab on its own before the host threw it ({record.Placement})"
                        : $"the mug was never recorded in the cab after the {actorName} dropped it ({record.Placement})");
                float droppedFor = Time.realtimeSinceStartup - inCabAt;

                var car = Trains.Find(loco);
                Vector3 side = car.transform.right;
                Vector3 start = car.transform.position + side * 2.5f + Vector3.up * 3f;
                Vector3 velocity = side * 2.5f + Vector3.up * 3.5f;
                var snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState);
                snapshot.ItemState = ItemState.Thrown;
                snapshot.PlayerId = 0;
                snapshot.ItemPosition = start - WorldMover.currentMove;
                // GrabHandlerItem.Throw applies direction * mass * 140 as a force for one physics step.
                snapshot.ThrowDirection = velocity / (140f * Time.fixedDeltaTime);
                snapshot.WorldParentKind = ItemWorldParentKind.World;
                snapshot.WorldParentNetId = 0;
                snapshot.WorldParentKey = string.Empty;
                snapshot.InventoryClaimPlayerId = 0;
                snapshot.InventoryClaimSlot = -1;
                snapshot.InventoryClaimFlags = ItemInventoryClaimFlags.None;
                if (!AuthoritativeItemRegistry.TryApplyTransition(item, snapshot, actor, ItemTransitionReason.Unknown, true,
                        out string rejection, cause: ItemChangeLog.Move(Multiplayer.Core.Items.ItemMoveReason.Thrown, actor, "dvmp server-throw")))
                    throw new Exception("the throw was refused: " + rejection);
                var manager = ServerItemManager.Instance;
                // In its world cell the moment the transition is committed, before anything applies it.
                bool indexedOnCommit = manager.HostCellByItem.ContainsKey(item) && !manager.HostNonSpatialItems.Contains(item);
                AuthoritativeItemRegistry.TryGet(id, out record);
                AuthoritativeItemRegistry.WriteToSnapshot(record, snapshot, ItemTransitionReason.Unknown);
                snapshot.ItemState = ItemState.Thrown;
                item.ApplyServerCanonicalSnapshot(snapshot);
                server.SendItemUpdatePacket(snapshot);

                Vector3 startAbs = start - WorldMover.currentMove;
                float travel = 0f, rise = 0f;
                float t1 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t1 < 4f && item != null)
                {
                    yield return null;
                    Vector3 at = Dev.Abs(item.transform.position) - startAbs;
                    rise = Mathf.Max(rise, at.y);
                    travel = Mathf.Max(travel, new Vector3(at.x, 0f, at.z).magnitude);
                }
                step.Result = new Thrown
                {
                    DroppedFor = droppedFor, LeaseLiveAtThrow = leased, IndexedOnCommit = indexedOnCommit, Travel = travel, Rise = rise,
                    Start = new Abs { X = startAbs.x, Y = startAbs.y, Z = startAbs.z }
                };
            }
        }
    }
}
