using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV.CabControls;
using Dvmp.Agent;
using Dvmp.Lib;
using HarmonyLib;
using Multiplayer.Client.Items.Projection;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A projection is known only once acknowledged, and every move and removal says why
    /// (docs/design/items/projection.md, "Acknowledgements"; change-causes.md). MODE:
    /// - failed (default): the client's game fails the next mug's Create. It must tell the server
    ///   (a Failed ack), which forgets the Create and sends it again: within 15 s the client has the
    ///   mug, acknowledged. Before R7 the immediate path sent nothing, and the server sent only
    ///   updates for an object the client never made (OI-SYNC-07);
    /// - failing: the client's game fails every Create of the mug, as a client without a modded
    ///   item's prefab would. The server sends it again after 5, 10, 20 and 40 s, then stops while
    ///   the mug stays in the client's interest; once the client has left and come back it sends it
    ///   afresh. Before, it went every 5 s for as long as the client stayed near it;
    /// - late-retire: a retirement's ack arrives after the mug came back into the client's interest
    ///   (F45: given to the server as the race delivers it). The server must keep the mug known, hold
    ///   nothing back, and take the new Create's ack. Before R7 it forgot the mug for 5 s and refused
    ///   that ack;
    /// - cause: the host corrects the client's displaced copy (convergence), and the client walks
    ///   out of the mug's range. The client's copy must have the host's correction as its last move,
    ///   and its retirement must say it left interest and where it was (OI-SYNC-18). The server's
    ///   debug state names the client's projection of the mug (OI-SYNC-17);
    /// - collision: the client's game is given a second object of one scene item (its key twice), and its
    ///   catalogue is negotiated again. The server must still accept it and refuse only that key.
    ///   Before R7 it refused the whole catalogue: no scene items for that client (OI-SYNC-08).
    /// </summary>
    [Scenario("projection-acks", "A Create the client can't make is sent again; a late retirement ack holds nothing back; corrections and retirements carry their causes")]
    [Budget(300)]
    public sealed class ProjectionAcks : Scenario<ProjectionAcks.Options>
    {
        public sealed class Options
        {
            /// <summary>failed, failing, late-retire, cause or collision.</summary>
            public string Mode = "failed";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);   // a just-joined client is still receiving its items
            switch (o.Mode)
            {
                case "failed":
                    await Failed(pair);
                    break;
                case "late-retire":
                    await LateRetire(pair);
                    break;
                case "collision":
                    await Collision(pair);
                    break;
                case "cause":
                    await Causes(pair);
                    break;
                case "failing":
                    await Failing(pair);
                    break;
                default:
                    throw new ArgumentException("MODE: failed, failing, late-retire, cause or collision, not " + o.Mode);
            }
        }

        private static async Task Failed(Pair pair)
        {
            await pair.Client.Run(() => InGame.FailNextCreate("Mug"));
            pair.Cleanup(() => pair.Client.Run(() => InGame.FailNextCreate("")));
            ushort mug = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "client"));
            Projection first = await Until.Value(() => pair.Host.Run(() => InGame.ProjectionTo(mug)),
                p => p.RetryIn > 0 || p.Acknowledged, 10, "the client's answer to the mug's Create");
            Report.Note("after the first Create: " + first);
            Expect.That(await pair.Client.Run(() => InGame.Failures()) > 0, "the client's game failed the mug's Create");
            Expect.That(!first.Acknowledged, "the server doesn't count the failed Create as the client's", first.ToString());
            Projection again = await Until.Value(() => pair.Host.Run(() => InGame.ProjectionTo(mug)),
                p => p.Acknowledged, 15, "the Create sent again, and acknowledged");
            Report.Note("after the retry: " + again);
            Expect.That(await pair.Client.Run(() => InGame.Has(mug)), "the client has the mug after the retry");
        }

        private static async Task Failing(Pair pair)
        {
            await pair.Client.Run(() => InGame.FailCreates("Mug"));
            pair.Cleanup(() => pair.Client.Run(() => InGame.FailNextCreate("")));
            ushort mug = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "client"));
            // 1 + 4 retries over 5 + 10 + 20 + 40 s: all of them by 90 s, and none after.
            Projection given = await Until.Value(() => pair.Host.Run(() => InGame.ProjectionTo(mug)),
                p => p.RetryIn < 0, 100, "the server giving up on the mug's Create");
            Report.Note("after the failures: " + given);
            await Task.Delay(15000);
            int failures = await pair.Client.Run(() => InGame.Failures());
            Report.Note($"the client's game failed the mug's Create {failures} times");
            // Core ProjectionRetry: the first Create and its 4 retries.
            Expect.Equal(5, failures, "Creates of the mug the client failed (the first and each retry, then none)");
            Expect.That(!given.Known && !given.Acknowledged, "the server doesn't count the mug as the client's", given.ToString());

            // Out of interest and back: it is sent afresh, and made this time.
            await pair.Client.Run(() => InGame.FailNextCreate(""));
            Abs at = await pair.Host.Run(() => OwnedListShelf.InGame.At(mug));
            await pair.Client.Run<Abs>(s => Walk.Away(s, 900, 0), 60);
            await Until.Value(() => pair.Host.Run(() => InGame.ProjectionTo(mug)), p => p.RetryIn == 0, 20,
                "the server forgetting the failures once the mug left the client's interest");
            await pair.Client.Run(() => OwnedListShelf.InGame.GoTo(at), 60);
            Projection back = await Until.Value(() => pair.Host.Run(() => InGame.ProjectionTo(mug)),
                p => p.Acknowledged, 20, "the mug sent afresh after the client came back, and acknowledged");
            Report.Note("after coming back: " + back);
            Expect.That(await pair.Client.Run(() => InGame.Has(mug)), "the client has the mug after coming back");
        }

        private static async Task LateRetire(Pair pair)
        {
            ushort mug = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "client"));
            Projection before = await Until.Value(() => pair.Host.Run(() => InGame.ProjectionTo(mug)),
                p => p.Acknowledged, 15, "the client's mug acknowledged");
            Report.Note("before: " + before);
            Projection late = await pair.Host.Run(() => InGame.Ack(mug, "retired"));
            Report.Note("after the late retirement ack: " + late);
            Expect.That(late.Known, "the server still knows it sent the client the mug", late.ToString());
            Expect.Equal(0f, late.RetryIn, "seconds the server holds the mug's Create back");
            Projection after = await pair.Host.Run(() => InGame.Ack(mug, "projected"));
            Report.Note("after the re-entry's ack: " + after);
            Expect.That(after.Acknowledged, "the re-entry's ack is taken", after.ToString());
        }

        private static async Task Causes(Pair pair)
        {
            ushort mug = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "client"));
            await Until.Value(() => pair.Host.Run(() => InGame.ProjectionTo(mug)), p => p.Acknowledged, 15, "the client's mug acknowledged");
            string state = await pair.Host.Run(() => InGame.Recipients(mug));
            Report.Note("the server's projection state: " + state);
            Expect.That(state.Contains("acknowledged=True"), "the item's debug state shows the client's acknowledged projection", state);

            await pair.Client.Run(() => InGame.Displace(mug, 1.2f), 30);
            Require.That(await pair.Host.Run(() => InGame.SendConvergence(mug)), "the host sent the client a convergence correction");
            string moved = await Until.Value(() => pair.Client.Run(() => InGame.LastMove(mug)),
                m => m.StartsWith("HostCorrection", StringComparison.Ordinal), 10, "the client's copy corrected");
            Report.Note("the client's last move: " + moved);
            Expect.That(moved.Contains("convergence"), "the correction says it was the host's convergence", moved);

            await pair.Client.Run(() => InGame.WatchRemovals(mug));
            pair.Cleanup(() => pair.Client.Run(() => InGame.WatchRemovals(0)));
            await pair.Client.Run<Abs>(s => Walk.Away(s, 900, 0), 60);
            string removed = await Until.Value(() => pair.Client.Run(() => InGame.Removal()),
                r => r.Length > 0, 20, "the mug's retirement in the client's game");
            Report.Note("the client's removal: " + removed);
            Expect.That(removed.StartsWith("LeftInterest", StringComparison.Ordinal), "the retirement's cause is LeftInterest", removed);
            Expect.That(removed.Contains("cell"), "the retirement says where the mug was", removed);
        }

        private static async Task Collision(Pair pair)
        {
            string key = await pair.Client.Run(() => InGame.Collide());
            pair.Cleanup(async () =>
            {
                await pair.Client.Run(() => InGame.Uncollide());
                await Task.Delay(2000);
            });
            Report.Note("the client's game makes this key twice: " + key);
            Catalogue answer = await Until.Value(() => pair.Host.Run(() => InGame.CatalogueOf()),
                c => c.Refused.Length > 0 || !c.Accepted, 15, "the server's answer to the client's new catalogue");
            Report.Note("the server: " + answer);
            Expect.That(answer.Accepted, "the client's catalogue is still accepted", answer.ToString());
            Expect.Equal(key, answer.Refused, "the keys refused (only the collided one)");
        }

        public sealed class Catalogue
        {
            public bool Accepted;
            public string Refused = "";
            public override string ToString() => $"accepted {Accepted}, refused [{Refused}]";
        }

        public sealed class Projection
        {
            public bool Known, Acknowledged;
            public float RetryIn;
            public override string ToString() => $"known {Known}, acknowledged {Acknowledged}, retry in {RetryIn:0.#} s";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string HarmonyId = "dvmp.projection-acks";
            private static string failPrefab = "";
            private static bool failAlways;
            private static int failures;

            /// <summary>Client: fails the next Create of this prefab ("" stops).</summary>
            [Step]
            public static bool FailNextCreate(string prefab)
            {
                var harmony = new Harmony(HarmonyId + ".create");
                harmony.UnpatchAll(HarmonyId + ".create");
                failPrefab = prefab ?? "";
                failAlways = false;
                failures = 0;
                if (failPrefab.Length > 0)
                    harmony.Patch(AccessTools.Method(typeof(ClientItemManager), "CreateItem"),
                        prefix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(RefuseCreate))));
                return true;
            }

            public static bool RefuseCreate(ItemUpdate snapshot, ref bool __result)
            {
                if (failPrefab.Length == 0 || snapshot?.PrefabName != failPrefab)
                    return true;
                if (!failAlways)
                    failPrefab = "";
                failures++;
                __result = false;
                return false;
            }

            /// <summary>Client: fails every Create of this prefab, until <see cref="FailNextCreate"/> stops it.</summary>
            [Step]
            public static bool FailCreates(string prefab)
            {
                FailNextCreate(prefab);
                failAlways = true;
                return true;
            }

            [Step]
            public static int Failures() => failures;

            [Step]
            public static bool Has(ushort netId) => NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null;

            private static ServerPlayer Client()
            {
                NetworkServer server = NetworkServer.Instance;
                return server.ServerPlayers.FirstOrDefault(p => p.Peer != server.SelfPeer && p != server.LocalPlayer)
                       ?? throw new Exception("no client connected");
            }

            /// <summary>Host: what the server has sent the client of the item, and what it said back.</summary>
            [Step]
            public static Projection ProjectionTo(ushort netId)
            {
                ServerPlayer client = Client();
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                return new Projection
                {
                    Known = item != null && client.KnownItems.ContainsKey(item),
                    Acknowledged = client.AcknowledgedWorldItems.Contains(netId),
                    // -1: no more Creates while it stays in the client's interest.
                    RetryIn = !client.ProjectionRetries.TryGetValue(netId, out var retry) ? 0f
                        : float.IsPositiveInfinity(retry.At) ? -1f : Mathf.Max(0f, retry.At - Time.time)
                };
            }

            /// <summary>Host: the client's ack of the item (projected, retired or failed) arrives, as its peer would send it.</summary>
            [Step]
            public static Projection Ack(ushort netId, string kind)
            {
                ServerPlayer client = Client();
                LiveItemsRevision(netId, out uint revision);
                NetworkServer.Instance.OnServerboundWorldItemProjectionAckPacket(new ServerboundWorldItemProjectionAckPacket
                {
                    ItemNetId = netId,
                    AuthorityRevision = revision,
                    Kind = kind == "retired" ? ProjectionAck.Retired : kind == "failed" ? ProjectionAck.Failed : ProjectionAck.Projected
                }, client.Peer);
                return ProjectionTo(netId);
            }

            private static void LiveItemsRevision(ushort netId, out uint revision) =>
                revision = Multiplayer.Server.Items.LiveState.LiveItems.TryGet(netId, out var record) ? record.Revision : 0;

            /// <summary>Host: the item's per-recipient debug state, as text.</summary>
            [Step]
            public static string Recipients(ushort netId)
            {
                Dictionary<string, object> state = Multiplayer.Shared.Items.LiveState.ItemServer.Current?.AuthorityDebugState(netId);
                if (state == null || !state.TryGetValue("recipients", out object value) || value is not Dictionary<string, object>[] rows)
                    return "no recipients";
                return string.Join("; ", rows.Select(row => string.Join(", ", row.Select(kv => kv.Key + "=" + kv.Value))));
            }

            /// <summary>Client: moves its own copy of the item by some metres, behind the host's back.</summary>
            [Step]
            public static IEnumerator Displace(ushort netId, float metres)
            {
                yield return Items.Arrive(netId);
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                Rigidbody body = item.Item?.ItemRigidbody;
                if (body != null) body.isKinematic = true;
                item.transform.position += Vector3.right * metres;
                yield return null;
            }

            /// <summary>Host: sends the client a convergence correction for the item.</summary>
            [Step]
            public static bool SendConvergence(ushort netId)
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null) return false;
                if (!Multiplayer.Server.Items.Projection.ServerItemManager.TryCreateConvergenceSnapshot(item, out ItemUpdate snapshot) || snapshot == null)
                    return false;
                NetworkServer.Instance.SendItemUpdate(snapshot, Client());
                return true;
            }

            /// <summary>Client: the item's last move cause, as "Reason: detail".</summary>
            [Step]
            public static string LastMove(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null && item.LastMoveCause is { } cause
                    ? cause.Reason + ": " + cause.Detail
                    : "";

            private static NetworkedItem watchedItem;

            /// <summary>
            /// Client: watches this item for the removal cause its retirement gives it (0 stops). It
            /// keeps the item itself and reads its cause: the setter is a one-liner Mono inlines into
            /// callers compiled long before, so a patch on it never ran.
            /// </summary>
            [Step]
            public static bool WatchRemovals(ushort netId)
            {
                watchedItem = netId != 0 && NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null &&
                              !item.LastRemovalCause.HasValue ? item : null;
                return true;
            }

            /// <summary>Client: the watched item's removal cause, as "Reason: detail", once it has one.</summary>
            [Step]
            public static string Removal() =>
                // A retired copy is a destroyed object; its managed fields still read.
                !ReferenceEquals(watchedItem, null) && watchedItem.LastRemovalCause is { } cause
                    ? cause.Reason + ": " + cause.Detail
                    : "";

            private static GameObject twinHolder;

            /// <summary>
            /// Client: a second object of one scene item, with its name, scene and key, as a modded
            /// scene might have, and negotiates its catalogue again. The client's game has the same
            /// keys as the host's, one of them twice: only that one collides. (Before, this gave a
            /// second item the first's key, which took the second's key out of the client's set: the
            /// catalogues differed, and the server refused it whole however it treated collisions.)
            /// The copy is made under an inactive parent, so it never wakes: no NetId, nothing sent.
            /// Result: the key two objects now make.
            /// </summary>
            [Step]
            [ClientOnly]
            public static string Collide()
            {
                var catalogue = NetworkedItemManager.Instance.Authored.Catalogue;
                NetworkedItem original = catalogue.Items.FirstOrDefault(i => i != null && i.Item != null);
                if (original == null) throw new Exception("no scene item in this game's catalogue");
                twinHolder = new GameObject("dvmp projection-acks twin");
                twinHolder.SetActive(false);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(twinHolder, original.gameObject.scene);
                GameObject twin = UnityEngine.Object.Instantiate(original.gameObject, twinHolder.transform);
                twin.name = original.gameObject.name;
                // ItemBase takes its spec at Awake, which a sleeping copy never has.
                ItemBase item = twin.GetComponent<ItemBase>();
                AccessTools.Property(typeof(ItemBase), nameof(ItemBase.InventorySpecs))
                    .SetValue(item, twin.GetComponent<InventoryItemSpec>());
                twin.GetComponent<NetworkedItem>().AuthoredItemKey = original.AuthoredItemKey;
                int before = catalogue.BaselineCount;
                catalogue.Rebuild();
                if (catalogue.BaselineCount != before || !catalogue.BaselineCollisionKeys.Contains(original.AuthoredItemKey))
                    throw new Exception($"the twin didn't collide alone: {before} keys before, {catalogue.BaselineCount} after, collided [{string.Join(",", catalogue.BaselineCollisionKeys)}]");
                Multiplayer.Client.Session.NetworkClient.Instance.SendWorldItemCatalogue();
                return original.AuthoredItemKey;
            }

            /// <summary>Client: removes the second object and negotiates again.</summary>
            [Step]
            [ClientOnly]
            public static bool Uncollide()
            {
                if (twinHolder != null)
                    UnityEngine.Object.DestroyImmediate(twinHolder);
                twinHolder = null;
                NetworkedItemManager.Instance.Authored.Catalogue.Rebuild();
                Multiplayer.Client.Session.NetworkClient.Instance.SendWorldItemCatalogue();
                return true;
            }

            /// <summary>Host: the server's answer to the client's catalogue.</summary>
            [Step]
            public static Catalogue CatalogueOf()
            {
                ServerPlayer client = Client();
                return new Catalogue
                {
                    Accepted = client.WorldItemCatalogueAccepted,
                    Refused = string.Join(",", client.RefusedAuthoredKeys.OrderBy(k => k, StringComparer.Ordinal))
                };
            }
        }
    }
}
