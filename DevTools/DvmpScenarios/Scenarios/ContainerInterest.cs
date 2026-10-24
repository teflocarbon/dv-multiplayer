using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A deposit's Destroy goes by interest (docs/design/items/containers.md, "Deposit"; OI-CON-12):
    /// it used to go to every player. The client walks far off, so the server never sends it the
    /// host's mug, and the host's own player deposits the mug into a box: the Destroy is sent to the
    /// host's player only, never to the client and never to everyone. Player host only: on a dedicated
    /// host the only player is the depositor. (A withdrawal's item goes by interest already: a row to
    /// the withdrawer, or loose through the world index: <c>withdraw-full</c>.)
    /// </summary>
    [Scenario("container-interest", "A deposit's Destroy goes only to the players who know the item, not to a client far away (OI-CON-12; player host)")]
    [Budget(600)]
    public sealed class ContainerInterest : Scenario<ContainerInterest.Options>
    {
        public sealed class Options
        {
            /// <summary>The container's prefab.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>How far the client walks off, in metres.</summary>
            public float Away = 2000f;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (pair.Dedicated)
            {
                Report.Note("skipped: a dedicated host has no player of its own to deposit while the client is away");
                return;
            }
            await Task.Delay(5000);
            await pair.Client.Run<Abs>(s => Walk.Away(s, 0, o.Away), 120);
            pair.Cleanup(() => pair.Client.Run<Abs>(s => Walk.Away(s, 0, -o.Away), 120));
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, "host"));
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "host"));
            await pair.Host.Run(() => Ownership.Give(mug, "host"));
            pair.Cleanup(async () =>
            {
                await pair.Host.Run(() => InGame.Unwatch());
                ushort[] left = await pair.Host.Run(() => Items.Existing(new[] { box, mug }));
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, left), 120);
            });
            await Task.Delay(3000);
            Require.That(!await pair.Host.Run(() => InGame.ClientKnows(mug)), "the client doesn't know the host's mug before the deposit");

            await pair.Host.Run(() => InGame.Watch(mug));
            Require.Equal(Boxes.Accepted, await pair.Host.Run<string>(s => Boxes.Deposit(s, box, mug), 120), "the host's deposit of its mug");
            string[] sent = await pair.Host.Run(() => InGame.Sent());
            Report.Note("the deposit's Destroy went to: " + string.Join(", ", sent));
            Expect.That(!sent.Contains("everyone"), "the Destroy isn't sent to everyone", string.Join(", ", sent));
            Expect.That(!sent.Contains("client"), "the Destroy isn't sent to the client far away", string.Join(", ", sent));
            Expect.That(sent.Contains("host"), "the Destroy is sent to the host's player, the depositor", string.Join(", ", sent));
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string HarmonyId = "dvmp.container-interest.watch";
            private static ushort watched;
            private static readonly List<string> sent = new();

            /// <summary>Host: whether the server counts the client as knowing the item.</summary>
            [Step]
            public static bool ClientKnows(ushort netId) =>
                NetworkServer.Instance.ServerPlayers.Any(p => p.Peer != NetworkServer.Instance.SelfPeer &&
                    p.KnownItems.Keys.Any(i => i != null && i.NetId == netId));

            /// <summary>Host: records who each Destroy of the item is sent to.</summary>
            [Step]
            public static void Watch(ushort netId)
            {
                watched = netId;
                sent.Clear();
                var harmony = new Harmony(HarmonyId);
                harmony.UnpatchAll(HarmonyId);
                harmony.Patch(AccessTools.Method(typeof(NetworkServer), nameof(NetworkServer.SendItemUpdate)),
                    prefix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(Seen))));
            }

            public static void Seen(ItemUpdate item, ServerPlayer sendToPlayer)
            {
                if (item == null || item.ItemNetId != watched || item.UpdateType != ItemUpdate.ItemUpdateType.Destroy)
                    return;
                sent.Add(sendToPlayer == null ? "everyone"
                    : sendToPlayer.Peer == NetworkServer.Instance.SelfPeer ? "host" : "client");
            }

            /// <summary>Host: who the item's Destroys were sent to: "host", "client" or "everyone".</summary>
            [Step]
            public static string[] Sent() => sent.ToArray();

            [Step]
            public static void Unwatch() => new Harmony(HarmonyId).UnpatchAll(HarmonyId);
        }
    }
}
