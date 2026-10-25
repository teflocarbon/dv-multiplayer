using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using HarmonyLib;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Physics;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A physics lease ends with the change that ends it, and why is recorded on the host
    /// (docs/design/items/physics.md, "Leases"). The client throws a mug high, so it is in flight
    /// under the client's lease for a second or two. MODE:
    /// - collect (default): the host collects it into its owner's Lost and Found mid-flight. The
    ///   collection ends the lease ("revoked:lost-and-found"), not a sample refused later (OI-PHY-02,
    ///   -03, F40: the lease failed every frame);
    /// - refused: the host refuses its settlement once. The refusal ends the lease
    ///   ("revoked:settlement-refused:..."); before, it stayed open until its samples timed out
    ///   (OI-PHY-03, -04, F39);
    /// - samples: nothing interferes. The record doesn't change while it flies (a sample commits
    ///   nothing: OI-PHY-01), and the settlement commits it once, one revision on;
    /// - save: the client owns the mug, so it is saved through its record (RowOf). Mid-flight the host
    ///   moves it 20 m itself, a new revision, with its lease kept open past the move (a probe skips
    ///   the lease's end), and saves. The row has the record's pose, not the stale lease's: a lease
    ///   saves its pose only while it holds (OI-PER-11, F25).
    /// </summary>
    [Scenario("lease-ends", "A collection or a refused settlement mid-flight ends the item's lease at once; samples change nothing until the settlement; a mid-flight save takes a lease's pose only while the lease holds")]
    public sealed class LeaseEnds : Scenario<LeaseEnds.Options>
    {
        public sealed class Options
        {
            /// <summary>collect, refused, samples or save.</summary>
            public string Mode = "collect";
            /// <summary>The thrown item's prefab.</summary>
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            if (o.Mode is "collect" or "save")
                Report.Note("the client owns it: " + (await pair.Host.Run(() => Ownership.Give(item, "client"))).Owner);
            if (o.Mode == "refused")
            {
                await pair.Host.Run(() => InGame.RefuseSettlement(item));
                pair.Cleanup(() => pair.Host.Run(() => InGame.RefuseSettlement(0)));
            }
            Running<Flight> watch = o.Mode switch
            {
                "collect" => pair.Host.Start<Flight>(s => InGame.WatchAndCollect(s, item, 45f), 90),
                "save" => pair.Host.Start<Flight>(s => InGame.WatchAndSave(s, item, 45f), 90),
                _ => pair.Host.Start<Flight>(s => InGame.Watch(s, item, 45f), 90)
            };
            await pair.Client.Run(() => InGame.ThrowHigh(item), 90);
            Flight f = await watch;
            Report.Note($"lease r{f.LeaseRevision} seen {f.Leased}, revisions while leased {f.Revisions}, ended after {f.Seconds:0.#} s: {(f.Ended.Length > 0 ? f.Ended : "still open")}");
            Require.That(f.Leased, "the throw opened a lease (it flew)");

            switch (o.Mode)
            {
                case "collect":
                {
                    ItemRecord record = await pair.Host.Run(() => Items.Record(item));
                    Report.Note("after the collection: " + record);
                    Expect.That(f.Acted, "the host collected it while it flew", f.ActedDetail);
                    Expect.Equal("LostAndFound", record.Placement, "where the host has it");
                    Expect.Equal("revoked:lost-and-found", f.Ended, "what ended its lease");
                    break;
                }
                case "refused":
                    Expect.That(f.Acted, "the host refused its settlement", f.ActedDetail);
                    Expect.That(f.Ended.StartsWith("revoked:settlement-refused:"), "the refusal ended its lease", f.Ended);
                    Expect.AtMost(f.SecondsAfterAct, 0.5, "seconds from the refusal to the lease's end (a sample timeout is 3 s)");
                    break;
                case "samples":
                {
                    ItemRecord record = await pair.Host.Run(() => Items.Record(item));
                    Report.Note("settled: " + record);
                    Expect.Zero(f.ChangedWhileLeased, $"record changes while it flew ({f.Revisions})");
                    Expect.That(f.Ended.StartsWith("settled:"), "a settlement ended its lease", f.Ended);
                    Expect.Equal(f.LeaseRevision + 1, record.Revision, "the record's revision (one commit: the settlement)");
                    break;
                }
                case "save":
                    Report.Note(f.ActedDetail);
                    Expect.That(f.Acted, "the host moved and saved it while it flew", f.ActedDetail);
                    Require.That(f.LeaseStale, "its lease was still open past the move (the probe kept it)", f.ActedDetail);
                    Expect.That(f.Saved, "the owned mug's row is in the save", f.ActedDetail);
                    Expect.AtMost(f.RowFromRecord, 0.5, "metres from the saved row's position to the record's (the lease's pose is 20 m off)");
                    break;
                default:
                    throw new ArgumentException("MODE: collect, refused, samples or save, not " + o.Mode);
            }
            PeerResults<ItemPose> pose = await pair.Both.Run(() => Items.Pose(item));
            foreach (var (peer, v) in pose)
                if (v.Present) Expect.That(!v.Leased, $"{peer}: no lease left open");
        }

        public sealed class Flight
        {
            public bool Leased, Acted, Saved, LeaseStale;
            public double RowFromRecord = -1;
            public uint LeaseRevision;
            public int ChangedWhileLeased;
            public string Revisions = "", Ended = "", ActedDetail = "";
            public float Seconds, SecondsAfterAct;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string HarmonyId = "dvmp.lease-ends.refuse";
            private const string KeepHarmonyId = "dvmp.lease-ends.keep";
            private static ushort keepLeaseFor;
            private static ushort refuseFor;
            private static float refusedAt = -1f;

            /// <summary>Host: refuses the next spatial commit of this item once (0: stops).</summary>
            [Step]
            public static bool RefuseSettlement(ushort id)
            {
                var harmony = new Harmony(HarmonyId);
                harmony.UnpatchAll(HarmonyId);
                refuseFor = id;
                refusedAt = -1f;
                if (id != 0)
                    harmony.Patch(AccessTools.Method(typeof(LiveItems), nameof(LiveItems.TryCommitSpatialPose)),
                        prefix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(Refuse))));
                return true;
            }

            public static bool Refuse(NetworkedItem item, ref LiveItems.LiveItem record, ref string rejectionReason, ref bool __result)
            {
                if (item == null || refuseFor == 0 || item.NetId != refuseFor)
                    return true;
                refuseFor = 0;
                refusedAt = Time.realtimeSinceStartup;
                record = null;
                rejectionReason = "lease-ends-test";
                __result = false;
                return false;
            }

            // Keeps this item's lease open through whatever would end it, so it outlives a move.
            public static bool KeepLease(ushort itemNetId) => keepLeaseFor == 0 || itemNetId != keepLeaseFor;

            /// <summary>
            /// Host: moves the flying item 20 m itself (a committed pose, a new revision) with its lease
            /// kept open past the move, then saves, and compares the owned row with the record.
            /// </summary>
            private static void SaveAfterMove(Flight f, ushort id)
            {
                var harmony = new Harmony(KeepHarmonyId);
                harmony.Patch(AccessTools.Method(typeof(ServerItemSpatial), "RevokeHostLease"),
                    prefix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(KeepLease))));
                keepLeaseFor = id;
                try
                {
                    NetworkedItem.TryGet(id, out NetworkedItem item);
                    LiveItems.TryGet(id, out LiveItems.LiveItem record);
                    uint before = record.Revision;
                    ServerItemManager.Instance.FallRescue.PutAt(item, record,
                        Dev.Abs(item.transform.position) + Vector3.right * 20f,
                        ItemChangeLog.Move(ItemMoveReason.HostCorrection, detail: "lease-ends scenario"));
                    LiveItems.TryGet(id, out record);
                    f.LeaseStale = NetworkedItemManager.Instance.HasActiveSpatialState(id);
                    SaveGameManager.Instance.Save(DV.Common.SaveType.Manual);
                    JToken saved = SaveGameManager.Instance.data?.GetJObject("Multiplayer")?
                        [Multiplayer.Server.Items.SaveLoad.ItemsSaveCodec.Key];
                    JObject root = saved as JObject ?? (saved?.Type == JTokenType.String ? JObject.Parse((string)saved) : null);
                    string persistent = record.PersistentItemId.ToString("D");
                    JArray at = root?["states"]?[persistent]?["position"] as JArray;
                    f.Acted = record.Revision > before;
                    f.Saved = at != null;
                    if (at != null)
                        f.RowFromRecord = Vector3.Distance(new Vector3((float)at[0], (float)at[1], (float)at[2]), record.Position);
                    f.ActedDetail = $"moved r{before} to r{record.Revision}, lease {(f.LeaseStale ? "still open" : "ended")}; " +
                                    $"the mug ({persistent}) {(at != null ? $"saved at {at.ToString(Newtonsoft.Json.Formatting.None)}" : "has no saved position")}, its record at {record.Position}";
                }
                finally
                {
                    keepLeaseFor = 0;
                    harmony.UnpatchAll(KeepHarmonyId);
                }
            }

            /// <summary>
            /// Client: picks the item up and throws it straight up, hard, so it flies for a second
            /// or two under the client's lease.
            /// </summary>
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
                    body.velocity = Vector3.up * 9f + PlayerManager.PlayerTransform.forward;
            }

            /// <summary>
            /// Host: waits for the item's lease and watches the record's revision and the lease until
            /// the lease ends or <paramref name="seconds"/> pass. The other two act on it first,
            /// a third of a second into the flight: a Lost and Found collection, or a move and a save.
            /// </summary>
            [Step]
            public static IEnumerator Watch(Step<Flight> step, ushort id, float seconds) => Watch(step, id, seconds, null);

            [Step]
            public static IEnumerator WatchAndCollect(Step<Flight> step, ushort id, float seconds) => Watch(step, id, seconds, "collect");

            [Step]
            public static IEnumerator WatchAndSave(Step<Flight> step, ushort id, float seconds) => Watch(step, id, seconds, "save");

            private static IEnumerator Watch(Step<Flight> step, ushort id, float seconds, string act)
            {
                var manager = NetworkedItemManager.Instance;
                var spatial = manager.Spatial;
                Flight f = new();
                var revisions = new List<uint>();
                string endedBefore = spatial.LastLeaseEnd(id);
                float t0 = Time.realtimeSinceStartup, actedAt = -1f;
                while (Time.realtimeSinceStartup - t0 < seconds)
                {
                    yield return null;
                    bool leased = manager.HasActiveSpatialState(id);
                    if (!LiveItems.TryGet(id, out LiveItems.LiveItem record)) break;
                    if (leased && !f.Leased)
                    {
                        f.Leased = true;
                        spatial.TryGetActiveLeaseIdentity(id, out f.LeaseRevision, out _, out _, out ItemSpatialStateData _);
                    }
                    if (leased)
                    {
                        if (revisions.Count == 0 || revisions[revisions.Count - 1] != record.Revision)
                        {
                            if (revisions.Count > 0) f.ChangedWhileLeased++;
                            revisions.Add(record.Revision);
                        }
                        // A third of a second into the flight: well before it lands.
                        if (act != null && !f.Acted && Time.realtimeSinceStartup - t0 > 0.3f)
                        {
                            actedAt = Time.realtimeSinceStartup;
                            if (act == "collect")
                            {
                                NetworkedItem.TryGet(id, out NetworkedItem item);
                                f.Acted = Multiplayer.Server.Items.LostAndFound.LostAndFoundServer.Collect(item,
                                    Multiplayer.Core.Items.LostAndFound.LostItemReason.OwnerDistance, "lease-ends scenario");
                                f.ActedDetail = f.Acted ? "collected" : "the collection was refused";
                            }
                            else
                                SaveAfterMove(f, id);
                        }
                    }
                    string ended = spatial.LastLeaseEnd(id);
                    if (f.Leased && !leased && ended != endedBefore)
                    {
                        f.Ended = ended;
                        break;
                    }
                }
                float end = Time.realtimeSinceStartup;
                if (refusedAt > 0f) { f.Acted = true; f.ActedDetail = "refused"; actedAt = refusedAt; }
                f.Seconds = end - t0;
                f.SecondsAfterAct = actedAt > 0f ? end - actedAt : -1f;
                f.Revisions = string.Join(",", revisions);
                step.Result = f;
            }
        }
    }
}
