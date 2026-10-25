using System;
using System.Collections;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The server takes a pickup only from within reach of the item (docs/design/items/physics.md,
    /// "Pickup reach"; OI-PHY-08, F35). A mug lies beside the client; the client walks 40 m away and
    /// its game asks to take the mug, as a modified or confused game could. The server must refuse
    /// (pickup-out-of-reach) and the mug stay where it lies. Before R7 any player who had been sent
    /// the item could take it from anywhere. Then the client walks to it and picks it up through its
    /// grabber, which must work: the bound doesn't refuse a real pickup.
    /// </summary>
    [Scenario("pickup-range", "A pickup from 40 m away is refused and the item stays where it lies; one from beside it is taken")]
    [Budget(300)]
    public sealed class PickupRange : Scenario<PickupRange.Options>
    {
        public sealed class Options
        {
            public string Prefab = "Mug";
            /// <summary>How far the client walks from the item before it asks to take it, m.</summary>
            public float Distance = 40f;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            await Task.Delay(3000);   // it reaches the client and settles
            ItemRecord lying = await pair.Host.Run(() => Items.Record(item));
            Require.Equal("World", lying.Placement, "the mug lies in the world");

            await pair.Client.Run<Abs>(s => Walk.Away(s, o.Distance, 0), 60);
            await Task.Delay(2000);   // the server hears where the client is now
            string far = await pair.Client.Run<string>(s => InGame.AskToTake(s, item), 30);
            ItemRecord after = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"asked from {o.Distance:0} m: {far}; the record: {after}");
            Expect.That(far.Contains("pickup-out-of-reach"), "the server refused the far pickup as out of reach", far);
            Expect.Equal("World", after.Placement, "the mug still lies in the world");
            Expect.Equal(lying.Revision, after.Revision, "the record's revision");

            await pair.Client.Run(() => Items.Pickup(item), 60);
            ItemRecord taken = await Until.Value(() => pair.Host.Run(() => Items.Record(item)),
                r => r.Placement == "PlayerHand", 15, "the pickup from beside it taken");
            Report.Note("from beside it: " + taken);
            Expect.Equal(taken.ClientId, taken.PlacementPlayer, "the player holding it");
            await pair.Client.Run(() => Items.Drop(), 30);
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: asks the server to take the item into the right hand, from wherever
            /// the player is. Result: "accepted", or the refusal and its detail.</summary>
            [Step]
            public static IEnumerator AskToTake(Step<string> step, ushort netId)
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null)
                    throw new Exception("the client has no item " + netId);
                IItemClient client = ItemClient.Current;
                ClientboundItemResultPacket result = null;
                uint requestId = 0;
                void Heard(ClientboundItemResultPacket packet)
                {
                    if (packet.RequestId == requestId) result = packet;
                }
                client.ItemResultReceived += Heard;
                try
                {
                    requestId = client.SendItemRequest(new ItemRequest
                    {
                        ItemNetId = netId,
                        ExpectedRevision = item.AuthorityRevision,
                        Action = ItemAction.Take,
                        Hand = PlayerHand.Right,
                        Slot = -1,
                        MagazineSlot = -1
                    });
                    if (requestId == 0) throw new Exception("the request wasn't sent");
                    float until = Time.realtimeSinceStartup + 10f;
                    while (result == null && Time.realtimeSinceStartup < until) yield return null;
                }
                finally
                {
                    client.ItemResultReceived -= Heard;
                }
                step.Result = result == null ? "no answer in 10 s"
                    : result.Accepted ? "accepted" : $"{result.Refusal}: {result.Detail}";
            }
        }
    }
}
