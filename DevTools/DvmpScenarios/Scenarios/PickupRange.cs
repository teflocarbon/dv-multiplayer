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
    /// grabber, which must work: the bound doesn't refuse a real pickup. <c>MODE=attached</c>: the item
    /// is an end-of-train lantern the client hung on a flatbed's coupler (Attached). Before the R7 fixes
    /// the bound skipped every place but the world, a car's inside and a shelf, so the far take was
    /// taken (B3).
    /// </summary>
    [Scenario("pickup-range", ("A pickup from 40 m away is refused and the item stays where it lies (also hung on a car); one from beside it is taken"))]
    [Budget(300)]
    public sealed class PickupRange : Scenario<PickupRange.Options>
    {
        public sealed class Options
        {
            public string Prefab = "Mug";
            /// <summary>How far the client walks from the item before it asks to take it, m.</summary>
            public float Distance = 40f;
            /// <summary>"lying" in the world, or "attached": an end-of-train lantern on a car's coupler.</summary>
            public string Mode = "lying";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort item = o.Mode == "attached" ? await HangLantern(pair) : await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            await Task.Delay(3000);   // it reaches the client and settles
            ItemRecord lying = await pair.Host.Run(() => Items.Record(item));
            string place = o.Mode == "attached" ? "Attached" : "World";
            Require.Equal(place, lying.Placement, "where the item lies");

            await pair.Client.Run<Abs>(s => Walk.Away(s, o.Distance, 0), 60);
            await Task.Delay(2000);   // the server hears where the client is now
            string far = await pair.Client.Run<string>(s => InGame.AskToTake(s, item), 30);
            ItemRecord after = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"asked from {o.Distance:0} m: {far}; the record: {after}");
            Expect.That(far.Contains("pickup-out-of-reach"), "the server refused the far pickup as out of reach", far);
            Expect.Equal(place, after.Placement, "where the item lies after the far pickup");
            Expect.Equal(lying.Revision, after.Revision, "the record's revision");

            // Back beside it first, and the server told, as for the far ask: a pickup straight after
            // the 40 m teleport was asked before the server heard the player had moved (refused out of reach).
            await pair.Client.Run(() => Owned.GoTo(item), 60);
            await Task.Delay(2000);
            await pair.Client.Run(() => Items.Pickup(item), 60);
            ItemRecord taken = await Until.Value(() => pair.Host.Run(() => Items.Record(item)),
                r => r.Placement == "PlayerHand", 15, "the pickup from beside it taken");
            Report.Note("from beside it: " + taken);
            Expect.Equal(taken.ClientId, taken.PlacementPlayer, "the player holding it");
            await pair.Client.Run(() => Items.Drop(), 30);
        }

        // An end-of-train lantern the client hangs on a flatbed's coupler.
        private static async Task<ushort> HangLantern(Pair pair)
        {
            string car = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "FlatbedEmpty", "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { car })));
            ushort lantern = await pair.Host.Run<ushort>(s => SaveHomes.InGame.Grant(s, "EOTLantern"));
            Report.Note("the lantern: " + await pair.Client.Run<string>(s => SaveHomes.InGame.AttachToCoupler(s, lantern, car), 90));
            await Until.Value(() => pair.Host.Run(() => Items.Record(lantern)), r => r.Placement == "Attached", 20, "the host has the lantern on the coupler");
            return lantern;
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
                    if (packet.RequestId == requestId) result = ItemAnswers.Copy(packet);
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
