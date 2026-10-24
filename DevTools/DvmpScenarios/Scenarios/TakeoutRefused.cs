using System;
using System.Collections;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using HarmonyLib;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The server refuses a take-out (docs/design/items/inventory.md, "Taking out"; D14,
    /// OI-COLD-10): a client takes a stored lantern out of its slot (to the hand, then as a drop),
    /// and the host refuses the request it makes, once with no correction (as an expired request or
    /// an item it can't make is refused) and once with one (as a stale revision is). Each time the
    /// client's object goes, the stand-in is back in its slot, and the server and ledger still have
    /// it there. Then, unrefused, the take-out goes through. A remote client only: the host's own
    /// player keeps objects until R4C C4.
    /// </summary>
    [Scenario("takeout-refused", "The server refuses a client's take-out, with and without a correction: the object goes and the stand-in is back in its slot")]
    [Budget(240)]
    public sealed class TakeoutRefused : Scenario<TakeoutRefused.Options>
    {
        public sealed class Options
        {
            /// <summary>The item to take out.</summary>
            public string Prefab = "Lantern";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort id = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            int slot = await pair.Client.Run<int>(s => Items.Stash(s, id, false), 60);
            Require.That(await pair.Client.Run(() => PlayerInventory.IsStandIn(id)), $"the {o.Prefab} is a stand-in in the client's slot {slot}");
            pair.Cleanup(async () => await pair.Host.Run(() => InGame.Refuse(0, false)));

            foreach ((string how, bool correct) in new[] { ("hand", false), ("hand", true), ("drop", false), ("drop", true) })
            {
                string what = $"a take-out to the {how}, refused {(correct ? "with" : "without")} a correction";
                await pair.Host.Run(() => InGame.Refuse(id, correct));
                Out took = await pair.Client.Run<Out>(s => InGame.TakeOut(s, id, how), 60);
                Report.Note($"{what}: {took}");
                Expect.That(took.Made, $"{what}: its object made at once");
                Expect.Equal(1, await pair.Host.Run(() => InGame.Refused()), $"{what}: the requests the host refused");
                Expect.That(took.BackAsStandIn, $"{what}: the stand-in is back", took.ToString());
                Expect.Equal(slot, took.SlotAfter, $"{what}: the client's slot afterwards");
                Expect.That(!took.ObjectAfter, $"{what}: no object left on the client", took.ToString());
                ServerSlot server = await pair.Host.Run(() => Ledger.Where(id));
                Report.Note($"{what}: server {server}");
                Expect.Equal(slot, server.Slot, $"{what}: the server's slot");
                Expect.Equal(slot, server.LedgerSlot, $"{what}: the ledger's slot");
            }

            // Unrefused, it goes through.
            await pair.Host.Run(() => InGame.Refuse(0, false));
            Out ok = await pair.Client.Run<Out>(s => InGame.TakeOut(s, id, "hand"), 60);
            Report.Note($"a take-out, not refused: {ok}");
            Expect.That(ok.Held, "the take-out not refused: the item in the hand");
            Expect.Equal("PlayerHand", (await pair.Host.Run(() => Items.Record(id))).Placement, "the take-out not refused: the server's record");
            await pair.Client.Run(() => Items.Drop());
        }

        public sealed class Out
        {
            public bool Made, Held, BackAsStandIn, ObjectAfter;
            public int SlotAfter = -1;
            public float Seconds;

            public override string ToString() =>
                $"made {Made}, held {Held}, back as a stand-in {BackAsStandIn} in slot {SlotAfter}, object after {ObjectAfter}, {Seconds:F1} s";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static ushort armed;
            private static bool withCorrection;
            private static int refused;
            private static bool patched;

            /// <summary>Host: refuses the next take or drop request for the item (0: none), sending its
            /// correction first or not. Clears the count.</summary>
            [Step]
            public static void Refuse(ushort netId, bool correct)
            {
                if (!patched)
                {
                    new Harmony("dvmp.scenarios.takeout-refused").Patch(AccessTools.Method(typeof(NetworkServer), "Carry"),
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(InGame), nameof(CarryPrefix))));
                    patched = true;
                }
                armed = netId;
                withCorrection = correct;
                refused = 0;
            }

            /// <summary>Host: how many requests it refused since <see cref="Refuse"/>.</summary>
            [Step]
            public static int Refused() => refused;

            private static bool CarryPrefix(ItemRequest request, ServerPlayer player, ref ClientboundItemResultPacket __result)
            {
                if (armed == 0 || request == null || request.ItemNetId != armed ||
                    request.Action != ItemAction.Take && request.Action != ItemAction.Drop)
                    return true;
                if (withCorrection)
                {
                    // A correction is the item's state, so it needs the object: the server makes a
                    // data-only item first and carries the request again.
                    if (!NetworkedItem.TryGet(armed, out NetworkedItem item) || item == null) return true;
                    item.SendCanonicalRejectionCorrection(player, "takeout-refused");
                }
                armed = 0;
                refused++;
                __result = NetworkServer.Refuse(request, "takeout-refused");
                return false;
            }

            /// <summary>
            /// Client: takes the stored item out of its slot (<paramref name="how"/> "hand" or
            /// "drop") and waits up to 8 s for the server's answer to settle. Result: what it saw.
            /// </summary>
            [Step]
            public static IEnumerator TakeOut(Step<Out> step, ushort id, string how)
            {
                var result = new Out();
                step.Result = result;
                if (Hands.Held != null) yield return Hands.Drop();
                if (!PlayerInventory.IsStandIn(id)) throw new Exception($"item {id} isn't a stand-in in the inventory");
                float t0 = Time.realtimeSinceStartup;
                GameObject go = PlayerInventory.SlotObject(id);
                if (how == "drop")
                    PlayerInventory.Drop(id);
                else
                    DV.InventorySystem.Inventory.Instance.EquipItem(go, PlayerInventory.Slot(id), 0);
                result.Made = NetworkedItem.TryGet(id, out NetworkedItem made) && made != null;
                while (Time.realtimeSinceStartup - t0 < 8f && !PlayerInventory.IsStandIn(id)) yield return null;
                yield return new WaitForSecondsRealtime(1f);
                result.Seconds = Time.realtimeSinceStartup - t0;
                result.Held = Hands.Held != null && Hands.Held.NetId == id;
                result.BackAsStandIn = PlayerInventory.IsStandIn(id) && PlayerInventory.Holds(id);
                result.SlotAfter = PlayerInventory.Slot(id);
                result.ObjectAfter = NetworkedItem.TryGet(id, out NetworkedItem after) && after != null;
                Hands.ReleaseControl();
            }
        }
    }
}
