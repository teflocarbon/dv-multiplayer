using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    /// - late-retire: a retirement's ack arrives after the mug came back into the client's interest
    ///   (F45: given to the server as the race delivers it). The server must keep the mug known, hold
    ///   nothing back, and take the new Create's ack. Before R7 it forgot the mug for 5 s and refused
    ///   that ack;
    /// - cause: the host corrects the client's displaced copy (convergence), and the client walks
    ///   out of the mug's range. The client's copy must have the host's correction as its last move,
    ///   and its retirement must say it left interest and where it was (OI-SYNC-18). The server's
    ///   debug state names the client's projection of the mug (OI-SYNC-17);
    /// - collision: one of the client's scene items is given another's key, and the client's
    ///   catalogue is negotiated again. The server must still accept it and refuse only that key.
    ///   Before R7 it refused the whole catalogue: no scene items for that client (OI-SYNC-08).
    /// </summary>
    [Scenario("projection-acks", "A Create the client can't make is sent again; a late retirement ack holds nothing back; corrections and retirements carry their causes")]
    [Budget(180)]
    public sealed class ProjectionAcks : Scenario<ProjectionAcks.Options>
    {
        public sealed class Options
        {
            /// <summary>failed, late-retire, cause or collision.</summary>
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
                default:
                    throw new ArgumentException("MODE: failed, late-retire, cause or collision, not " + o.Mode);
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
            private static int failures;
            private static ushort watched;
            private static string removal = "";

            /// <summary>Client: fails the next Create of this prefab ("" stops).</summary>
            [Step]
            public static bool FailNextCreate(string prefab)
            {
                var harmony = new Harmony(HarmonyId + ".create");
                harmony.UnpatchAll(HarmonyId + ".create");
                failPrefab = prefab ?? "";
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
                failPrefab = "";
                failures++;
                __result = false;
                return false;
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
                    RetryIn = client.ProjectionRetryAt.TryGetValue(netId, out float at) ? Mathf.Max(0f, at - Time.time) : 0f
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

            /// <summary>Client: records the next removal cause given to this item (0 stops).</summary>
            [Step]
            public static bool WatchRemovals(ushort netId)
            {
                var harmony = new Harmony(HarmonyId + ".removal");
                harmony.UnpatchAll(HarmonyId + ".removal");
                watched = netId;
                removal = "";
                if (netId != 0)
                    harmony.Patch(AccessTools.Method(typeof(NetworkedItem), nameof(NetworkedItem.SetLastRemovalCause)),
                        postfix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(SawRemoval))));
                return true;
            }

            public static void SawRemoval(NetworkedItem __instance, ChangeCause<ItemRemovalReason> cause)
            {
                if (__instance != null && __instance.NetId == watched && removal.Length == 0)
                    removal = cause.Reason + ": " + cause.Detail;
            }

            [Step]
            public static string Removal() => removal;

            private static NetworkedItem collided;
            private static string collidedKey = "";

            /// <summary>
            /// Client: gives one scene item another's key, as two objects of a modded scene might,
            /// and negotiates its catalogue again. Result: the key two objects now make.
            /// </summary>
            [Step]
            [ClientOnly]
            public static string Collide()
            {
                NetworkedItem[] authored = NetworkedItem.GetAll().Where(i => i != null && i.IsSceneAuthored)
                    .GroupBy(i => i.AuthoredItemKey).Select(g => g.First()).Take(2).ToArray();
                if (authored.Length < 2) throw new Exception("fewer than two scene items in this game");
                collided = authored[1];
                collidedKey = collided.AuthoredItemKey;
                collided.AuthoredItemKey = authored[0].AuthoredItemKey;
                NetworkedItemManager.Instance.Authored.Catalogue.Rebuild();
                Multiplayer.Client.Session.NetworkClient.Instance.SendWorldItemCatalogue();
                return authored[0].AuthoredItemKey;
            }

            /// <summary>Client: gives the item its own key back and negotiates again.</summary>
            [Step]
            [ClientOnly]
            public static bool Uncollide()
            {
                if (collided != null && collidedKey.Length > 0)
                    collided.AuthoredItemKey = collidedKey;
                collided = null;
                collidedKey = "";
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
