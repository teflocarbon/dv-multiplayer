using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using HarmonyLib;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Issue #37: an item that falls through the map near a client is reported by the client's game
    /// and rescued by the server. The server's own scan is turned off for the run (it loads terrain
    /// around remote players and would usually find the item first), so only the client's below-map
    /// report can rescue it: the host's copy is put 4 m under the client's ground and the client
    /// sent that pose. The client must report it, the server rescue it (its record's last move a
    /// below-map rescue from the client's report), and both games end with it on the ground at the
    /// same spot.
    /// </summary>
    [Scenario("client-fall-rescue", "An item 4 m under the map next to a client (the server's own scan off) is reported by the client and rescued by the server: on the ground at the same spot in both games")]
    public sealed class ClientFallRescue : Scenario<ClientFallRescue.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab.</summary>
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await pair.Host.Run(() => InGame.HostScan(false));
            pair.Cleanup(() => pair.Host.Run(() => InGame.HostScan(true)));

            ushort item = await pair.Host.Run<ushort>(s => InGame.SpawnKinematic(s, o.Prefab));
            float surface = await pair.Client.Run<float>(s => InGame.Surface(s, item), 60);
            Report.Note($"{o.Prefab} {item} next to the client, ground at {surface:0.##} there; the host's copy goes 4 m under it");
            Sunk k = await pair.Host.Run(() => InGame.Sink(item, surface));
            Report.Note($"sent to {k.Sent} client(s); the host has terrain there: {k.HostHasTerrain}");
            Require.AtLeast(k.Sent, 1, "clients sent the sunk pose (none: the client doesn't know the item)");

            ItemRecord r = await Until.Value(() => pair.Host.Run(() => Items.Record(item)), v => v.LastMove == "RescuedFromBelowMap", 30,
                "the server rescues it from below the map (else the client never reported it)");
            Report.Note($"rescued: {r.LastMoveDetail}");
            await Task.Delay(3000);   // the rescue reaches the client

            PeerResults<ItemPose> pose = await pair.Both.Run(() => Items.Pose(item));
            Report.Note($"host {pose.Host.Pos}, client {pose.Client.Pos}, ground {surface:0.##}, record y {r.Pos.Y:0.##}");
            Expect.That(r.LastMoveDetail.Contains("client-report"), "the rescue came from the client's report", r.LastMoveDetail);
            Expect.Equal("World", r.Placement, "record placement");
            Expect.AtLeast(r.Pos.Y, surface - 0.5, "record height (m), against the ground less 0.5");
            foreach (var (peer, v) in pose)
            {
                if (!Expect.That(v.Present, $"{peer}: has the item")) continue;
                Expect.AtLeast(v.Pos.Y, surface - 0.5, $"{peer}: item height (m), against the ground less 0.5");
            }
            if (pose.Client.Present && pose.Client.HasTerrain)
                Expect.AtMost(pose.Client.Depth, 0.5, "client: depth under its terrain (m)");
            if (pose.Host.Present && pose.Client.Present)
            {
                Expect.AtMost(Peers.Horizontal(pose.Host.Pos, pose.Client.Pos), 0.3, "host/client apart across (m)");
                Expect.AtMost(Math.Abs(pose.Host.Pos.Y - pose.Client.Pos.Y), 1, "host/client apart in height (m)");
            }
        }

        public sealed class Sunk
        {
            public int Sent;
            public bool HostHasTerrain;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string HarmonyId = "dvmp.client-fall-rescue.no-host-scan";

            /// <summary>Host: turns the server's own fall-rescue scan (ServerItemManager.HostScanForFallenItems)
            /// off (false) or back on, so an item under the map is rescued only through a client's
            /// below-map report: the server loads terrain around remote players, and its own scan
            /// would usually find the item first.</summary>
            [Step]
            public static void HostScan(bool on)
            {
                var harmony = new Harmony(HarmonyId);
                harmony.UnpatchAll(HarmonyId);
                if (!on)
                    harmony.Patch(AccessTools.Method(typeof(ServerItemManager), nameof(ServerItemManager.HostScanForFallenItems)),
                        prefix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(Skip))));
            }

            public static bool Skip() => false;

            /// <summary>Host: a fresh item next to the client's avatar, kinematic, so the host's copy
            /// stays where the run puts it (the host may have no ground there).</summary>
            [Step]
            public static IEnumerator SpawnKinematic(Step<ushort> step, string prefab)
            {
                Multiplayer.Shared.Players.NetworkedPlayer client = null;
                yield return Wait.Until(() => (client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().FirstOrDefault()) != null, 60f, "the client's avatar");
                var spec = DV.Globals.G.Items.items.First(i => i.ItemPrefabName == prefab);
                var go = UnityEngine.Object.Instantiate(spec.gameObject, client.transform.position + Vector3.up * 0.3f + client.transform.right * 2f,
                    Quaternion.identity, WorldMover.OriginShiftParent);
                Rigidbody body = go.GetComponent<Rigidbody>() ?? go.GetComponentInChildren<Rigidbody>();
                if (body != null) body.isKinematic = true;
                NetworkedItem item = go.GetComponent<NetworkedItem>();
                yield return Wait.Until(() => item.NetId != 0, 5f, $"the {prefab} to get a net ID");
                Items.Track(item.NetId);
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = item.NetId;
            }

            /// <summary>Client: waits for the item, and says where the ground is under it in this game.</summary>
            [Step]
            public static IEnumerator Surface(Step<float> step, ushort id)
            {
                yield return Items.Arrive(id, 30f);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                yield return new WaitForSeconds(2f);
                if (NetworkedItemManager.BelowTerrain(item.transform.position, out float surface) == null)
                    throw new Exception("no terrain loaded under the item in the client's game");
                step.Result = surface;
            }

            /// <summary>Host: puts the host's copy of the item 4 m under the client's ground, as when it
            /// falls through the terrain (#37), and sends the clients that pose as convergence would
            /// (without waiting for its turn). <paramref name="surface"/>: the absolute height of the
            /// ground there, from the client.</summary>
            [Step]
            public static Sunk Sink(ushort id, float surface)
            {
                if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) throw new Exception("no item " + id);
                Vector3 abs = Dev.Abs(item.transform.position);
                Vector3 under = Dev.World(new Vector3(abs.x, surface - 4f, abs.z));
                Rigidbody body = item.Item.ItemRigidbody;
                if (body != null) { body.isKinematic = true; body.position = under; }
                item.transform.position = under;
                var server = Multiplayer.Server.Session.NetworkServer.Instance;
                int sent = 0;
                foreach (var player in server.ServerPlayers)
                {
                    if (player == server.LocalPlayer || !player.KnownItems.ContainsKey(item)) continue;
                    if (!ServerItemManager.TryCreateConvergenceSnapshot(item, out var snapshot)) continue;
                    server.SendItemUpdate(snapshot, sendToPlayer: player);
                    sent++;
                }
                return new Sunk { Sent = sent, HostHasTerrain = NetworkedItemManager.BelowTerrain(item.transform.position, out _) != null };
            }
        }
    }
}
