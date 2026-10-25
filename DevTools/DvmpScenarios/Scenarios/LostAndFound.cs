using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Client.Items.LostAndFound;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Client.Items.Requests;
using Multiplayer.Shared.Protocol.Packets.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Lost and Found round trip: the client claims a personal item, drops it and walks away; the
    /// host collects it into Lost and Found, where the server keeps it as data with no object
    /// (OI-COLD-08); the client retrieves it into their inventory. The
    /// policy is tightened for the run (owner distance 50 m, grace 1 s) and restored afterwards.
    /// While it is tightened, other personal items both players walk away from are collected too.
    /// WHO=host: the host's own player does it instead, through its own Lost and Found requests,
    /// which the server handles in-process (checked); needs a player host.
    /// BELONGS_TO_PLAYER=0 (OI-LF-17, OI-LF-01): once it is the owner's, the host's copy has
    /// vanilla's <c>InventorySpecs.BelongsToPlayer</c> set false, as a dumpster, a shop or a withdrawal
    /// flips it, and kept false every frame until it is collected. Lost and Found decides by the
    /// ledger's keeper, so it is collected all the same. Without the fix the policy read vanilla's flag
    /// (<c>not-a-personal-item</c>): the item is never collected and the wait for it fails.
    /// </summary>
    [Scenario("lost-and-found", "A personal item left behind is collected into Lost and Found by the host and retrieved into its owner's inventory (WHO=host: the host's own, its requests handled in-process)")]
    public sealed class LostAndFound : Scenario<LostAndFound.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab.</summary>
            public string Prefab = "Mug";
            /// <summary>Whose item it is: client or host.</summary>
            public string Who = "client";
            /// <summary>False: vanilla's BelongsToPlayer is off on the host's copy until it's collected.</summary>
            public bool BelongsToPlayer = true;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host: a dedicated host has no player to own the item");
            Peer owner = o.Who == "host" ? pair.Host : pair.Client;
            Policy old = await pair.Host.Run(() => InGame.SetPolicy(new Policy { Owner = 50, Nearby = 10, Grace = 1 }));
            pair.Cleanup(() => pair.Host.Run(() => InGame.SetPolicy(old)));

            ushort item = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, o.Who));
            Report.Note($"a personal {o.Prefab} {item} at the {o.Who} (as a purchase makes one)");
            await owner.Run(() => InGame.Claim(item), 60);
            ItemRecord r = await pair.Host.Run(() => Items.Record(item));
            int id = o.Who == "host" ? r.HostId : r.ClientId;
            Require.Equal(id, r.Owner, $"the item's owner (the {o.Who}'s number)");
            Running<object> flagOff = null;
            if (!o.BelongsToPlayer)
            {
                Require.That(!await pair.Host.Run(() => InGame.SetBelongsToPlayer(item, false)), "vanilla's BelongsToPlayer on the host's copy, set off");
                flagOff = pair.Host.Start(() => InGame.KeepBelongsToPlayerOff(item, 45f), 60);
            }

            // Both players walk 300 m away (a dedicated host has no player to move).
            await pair.Client.Run<Abs>(s => Walk.Away(s, 300, 0));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 300, 20));
            r = await Until.Value(() => pair.Host.Run(() => Items.Record(item)), v => v.Lost, 30, "the host collects the item into Lost and Found");
            if (flagOff != null) await flagOff;
            Report.Note($"collected: placement {r.Placement}");
            // An entry is data on the server: its object goes at the next scan (OI-COLD-08), and the
            // retrieval below makes it again.
            await Until.Value(() => pair.Host.Run(() => Items.Record(item)), v => !v.Exists, 10, "the server keeps the entry as data, with no object");

            int slot = await owner.Run<int>(s => InGame.Retrieve(s, r.Handle), 60);
            if (o.Who == "host")
                Expect.Empty(await pair.Host.Run(() => ServerRequests.NotHandledLocally(new[] { "ServerboundItemListRequestPacket", "ServerboundItemRequestPacket" })),
                    "the host's Lost and Found requests that go over loopback");
            await Task.Delay(2000);   // the retrieval's record settles on the host
            r = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"retrieved into slot {slot}; host {r}");
            Expect.That(!r.Lost, "no longer listed as lost on the host");
            Expect.Equal("PlayerInventory", r.Placement, "host record placement");
            Expect.Equal(id, r.PlacementPlayer, $"the player the host has it with (the {o.Who})");
        }

        public sealed class Policy
        {
            public float Owner, Nearby, Grace;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: sets the Lost and Found policy (owner distance, nearby-player
            /// protection distance, grace seconds). Result: what it was.</summary>
            [Step]
            public static Policy SetPolicy(Policy p)
            {
                var s = Multiplayer.Multiplayer.Settings;
                var old = new Policy { Owner = s.LostItemOwnerDistance, Nearby = s.LostItemNearbyPlayerProtectionDistance, Grace = s.LostItemCollectionGraceSeconds };
                s.LostItemOwnerDistance = p.Owner;
                s.LostItemNearbyPlayerProtectionDistance = p.Nearby;
                s.LostItemCollectionGraceSeconds = p.Grace;
                return old;
            }

            /// <summary>Host: sets vanilla's BelongsToPlayer on the item's copy here. Result: what it is now.</summary>
            [Step]
            public static bool SetBelongsToPlayer(ushort id, bool value)
            {
                NetworkedItem item = Items.Find(id);
                item.Item.InventorySpecs.BelongsToPlayer = value;
                return item.Item.InventorySpecs.BelongsToPlayer;
            }

            /// <summary>Host: keeps vanilla's BelongsToPlayer off on the item's copy every frame until
            /// Lost and Found has it (its object may go then) or <paramref name="seconds"/> pass.</summary>
            [Step]
            public static IEnumerator KeepBelongsToPlayerOff(ushort id, float seconds)
            {
                float until = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < until && !LostAndFoundServer.Contains(id))
                {
                    if (NetworkedItem.TryGet(id, out NetworkedItem item) && item != null && item.Item?.InventorySpecs != null)
                        item.Item.InventorySpecs.BelongsToPlayer = false;
                    yield return null;
                }
            }

            /// <summary>Picks the item up (making it this player's) and drops it at their feet.</summary>
            [Step]
            public static IEnumerator Claim(ushort id)
            {
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                for (int i = 0; i < 30; i++) yield return null;
                yield return Hands.Drop();
                for (int i = 0; i < 120; i++) yield return null;
            }

            /// <summary>Waits for the row (its handle, from the host's record) in this player's Lost
            /// and Found list, collects it, and checks the item lands in the inventory. Result: its slot.</summary>
            [Step]
            public static IEnumerator Retrieve(Step<int> step, uint handle)
            {
                ItemRow entry = null;
                float t0 = Time.realtimeSinceStartup;
                while (entry == null && Time.realtimeSinceStartup - t0 < 30f)
                {
                    ItemClient.Current.RequestItemList(ItemListKind.LostAndFound);
                    for (int i = 0; i < 30; i++) yield return null;
                    entry = LostAndFoundList.Items.FirstOrDefault(e => e.Handle == handle);
                }
                if (entry == null) throw new Exception("row " + handle + " never appeared in this player's Lost and Found list");
                ClientboundItemResultPacket result = null;
                uint requestId = 0;
                void Got(ClientboundItemResultPacket p) { if (p.RequestId == requestId) result = ItemAnswers.Copy(p); }
                ItemClient.Current.ItemResultReceived += Got;
                try
                {
                    requestId = ItemClient.Current.SendItemRequest(ItemExchange.CollectLost(entry));
                    t0 = Time.realtimeSinceStartup;
                    while (result == null && Time.realtimeSinceStartup - t0 < 15f) yield return null;
                }
                finally
                {
                    ItemClient.Current.ItemResultReceived -= Got;
                }
                if (result == null) throw new Exception("no retrieval result");
                if (!result.Accepted) throw new Exception($"retrieval refused: {result.Refusal} {result.Detail}");
                var inventory = DV.InventorySystem.Inventory.Instance;
                ushort id = result.ItemNetId;
                // On a client it's a stand-in in its slot, or still its object until R4C C5 (D14).
                yield return Wait.Until(() => PlayerInventory.Holds(id), 15f, $"retrieved item {id} in the inventory");
                step.Note($"listed {LostAndFoundList.Items.Count(e => e.Handle == handle)} time(s) after retrieval" +
                          (PlayerInventory.IsStandIn(id) ? ", a stand-in" : ""));
                step.Result = PlayerInventory.Slot(id);
            }
        }
    }
}
