using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Client.Items.Inventory;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// An object's state change the server sends while it holds a stashed item's object
    /// (docs/design/items/inventory.md, "Rows on the client"; R4C C9 A6). The client stashes a
    /// flashlight; the host has its object made for a moment (PlayerItems.WithObject, as a request
    /// naming it does) and changes a value the server drives (its light's intensity), which goes to
    /// the client as an update with no ItemState. Such an update read as the item leaving the
    /// inventory: the row ended and an unplaced object was made. For 4 s, every frame, the client has
    /// its stand-in, no object and its row.
    /// </summary>
    [Scenario("stand-in-object-state", "The server changing a stashed flashlight's object leaves the client's stand-in in its slot, with no object, and its row")]
    [Budget(120)]
    public sealed class StandInObjectState : Scenario<StandInObjectState.Options>
    {
        public sealed class Options
        {
            /// <summary>The item stashed: one with a value the server drives.</summary>
            public string Prefab = "Flashlight";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort id = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            int slot = await pair.Client.Run<int>(s => Items.Stash(s, id, false), 60);
            await Task.Delay(2000);   // the server's object goes to data
            Require.That(await pair.Host.Run(() => InGame.IsDataOnly(id)), $"the server keeps the {o.Prefab} as data");

            Running<Watched> watch = pair.Client.Start<Watched>(s => InGame.Watch(s, id, slot, 4f), 30);
            int changes = await pair.Host.Run<int>(s => InGame.ChangeWithObject(s, id), 30);
            Report.Note($"the host changed the {o.Prefab}'s object {changes} time(s) while it had it");
            Require.That(changes > 0, "the host had the object to change");
            Watched w = await watch;
            Report.Note($"client, over {w.Frames} frames: {w}");
            Expect.Zero(w.Object, "frames the client had an object of the stashed item");
            Expect.Zero(w.NoStandIn, $"frames the client had no stand-in for it in slot {slot}");
            Expect.Zero(w.NoRow, "frames the client had no row for it");
            await Task.Delay(1000);   // the server's object goes back to data after its grace
            Expect.That(await pair.Host.Run(() => InGame.IsDataOnly(id)), $"the server keeps the {o.Prefab} as data again");
        }

        public sealed class Watched
        {
            public int Frames, Object, NoStandIn, NoRow;
            public override string ToString() => $"an object in {Object}, no stand-in in {NoStandIn}, no row in {NoRow}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: whether the server keeps the item as data only (no object).</summary>
            [Step]
            public static bool IsDataOnly(ushort id) => PlayerItems.IsDataOnly(id);

            /// <summary>
            /// Host: has the server's object of the stashed item made (as a request does) and, for
            /// 0.8 s, moves its flashlight's light intensity (server-driven) every 0.1 s, sending the
            /// holder each change as the object's state alone (an ObjectState update, as the server's
            /// tick does for a tracked value: one made inactive in a slot may not track its values).
            /// Result: how many times it changed it.
            /// </summary>
            [Step]
            public static IEnumerator ChangeWithObject(Step<int> step, ushort id)
            {
                NetworkedItem made = null;
                bool done = false;
                PlayerItems.WithObject(id, item =>
                {
                    made = item;
                    done = true;
                });
                yield return Wait.Until(() => done, 5f, $"the server's object of {id}");
                NetworkServer server = NetworkServer.Instance;
                ServerPlayer holder = server.ServerPlayers.First(p => p != server.LocalPlayer && p.PlayerId != 0);
                FlashlightItem light = made != null ? made.GetComponentInChildren<FlashlightItem>(true) : null;
                int changes = 0;
                for (int i = 0; i < 8 && light != null && made != null; i++)
                {
                    light.originalLightIntensity += i % 2 == 0 ? 0.5f : -0.25f;
                    ItemUpdate update = made.CreateUpdateData(ItemUpdate.ItemUpdateType.ObjectState);
                    if (update != null) server.SendItemUpdate(update, holder);
                    changes++;
                    yield return new WaitForSecondsRealtime(0.1f);
                }
                step.Result = changes;
            }

            /// <summary>Client: counts the frames over <paramref name="seconds"/> in which the item has an
            /// object here, no stand-in in its slot, or no row.</summary>
            [Step]
            public static IEnumerator Watch(Step<Watched> step, ushort id, int slot, float seconds)
            {
                var w = new Watched();
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < seconds)
                {
                    w.Frames++;
                    if (NetworkedItem.TryGet(id, out NetworkedItem item) && item != null) w.Object++;
                    if (!PlayerInventory.IsStandIn(id) || PlayerInventory.Slot(id) != slot) w.NoStandIn++;
                    if (!InventoryRows.TryGet(id, out _)) w.NoRow++;
                    yield return null;
                }
                step.Result = w;
            }
        }
    }
}
