using System;
using System.Collections;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// An item's state through a move the server makes, and the state only the server keeps
    /// (docs/design/items/inventory.md, "State"; R4C C9 A1, A2, A3):
    /// 1. The client turns a lantern's wick, stashes it, drops it and walks away; it's collected into
    ///    its Lost and Found and retrieved into a slot. The stand-in has the row's state (a server-made
    ///    move once reached the client with none, and a take-out made a fresh lantern); taken out, the
    ///    lantern has its wick; dropped, the client doesn't hold it tombstoned as Lost and Found's, and
    ///    the host has it dropped in the world with its wick.
    /// 2. The client stashes a toolbox, takes it out and stashes it again: its server row keeps the
    ///    item id the server's object had, which is its container's id too (a holder's copy once minted
    ///    its own container id, which replaced the server's and orphaned its contents; OI-CON-09).
    /// The Lost and Found policy is tightened for the run (owner distance 50 m, grace 1 s), as
    /// lost-and-found does.
    /// </summary>
    [Scenario("retrieved-state", "A lantern retrieved from Lost and Found keeps its wick through a take-out and a drop; a toolbox's server row keeps its item id through stashes")]
    [Budget(300)]
    public sealed class RetrievedState : Scenario<RetrievedState.Options>
    {
        public sealed class Options
        {
            /// <summary>The container stashed.</summary>
            public string Container = "ItemContainerToolbox";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = 10, Grace = 1 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));

            // 1. A lantern with its wick turned, its state on the server's row by the stash, dropped and left.
            ushort lantern = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Lantern", "client"));
            float wick = (await pair.Host.Run(() => ItemRelease.InGame.ReadLantern(lantern))).Wick < 0.5f ? 0.85f : 0.15f;
            await pair.Client.Run(() => InGame.TurnAndStash(lantern, wick), 60);
            await pair.Client.Run(() => Owned.DropFromInventory(lantern), 60);
            await Task.Delay(1000);   // the drop reaches the host
            // Who the lantern is before Lost and Found (OI-LF-19): its id, its record and the host's object.
            string id = await pair.Host.Run(() => Owned.IdOf(lantern));
            ItemPose objectBefore = await pair.Host.Run(() => Items.Pose(lantern));
            Require.That(id.Length > 0 && await pair.Host.Run(() => InGame.RememberRecord(lantern)), "the lantern has an id and a record on the host", id);
            Require.That(objectBefore.Present, "the host has the dropped lantern's object");
            await pair.Client.Run<Abs>(s => Walk.Away(s, 300, 0));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 300, 20));
            ItemRecord r = await Until.Value(() => pair.Host.Run(() => Items.Record(lantern)), v => v.Lost, 30, "the host collects the lantern into Lost and Found");
            // An entry is data on the server: its object goes (lost-and-found.md, "Kept as data"), so the retrieval must make it again.
            await Until.Value(() => pair.Host.Run(() => Items.Record(lantern)), v => !v.Exists, 10, "the server keeps the lantern's entry as data, with no object");
            int slot = await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, r.Handle), 60);
            await Task.Delay(2000);   // the server's object goes to data and sends the client its row
            // Made again from its row under the same NetId, id and record: it fails if the retrieval made a new item.
            Expect.Equal(id, await pair.Host.Run(() => Owned.IdOf(lantern)), "the retrieved lantern's id, under its old NetId");
            Expect.Equal(lantern, await pair.Host.Run(() => Owned.NetIdOf(id)), "the NetId of the item with the lantern's id after the retrieval");
            Expect.That(await pair.Host.Run(() => InGame.SameRecord(lantern)), "the retrieved lantern has the record it had before Lost and Found");
            Seen stored = await pair.Client.Run(() => InGame.Look(lantern));
            Report.Note($"wick {wick}; retrieved into slot {slot}: {stored}");
            Expect.That(stored.StandIn, "the retrieved lantern is a stand-in in the client's slot");
            Expect.That(stored.State.Length > 0, "the stand-in has its row's state (A2)", stored.State.Length > 0 ? stored.State : "none");

            ItemRelease.LanternState held = await pair.Client.Run<ItemRelease.LanternState>(s => InGame.TakeOutAndDrop(s, lantern), 60);
            Expect.AtMost(Off(held.Wick, wick), 0.05, $"the client's wick off {wick}, taken out");
            await Task.Delay(2000);   // the drop settles on the host
            Seen dropped = await pair.Client.Run(() => InGame.Look(lantern));
            Report.Note($"dropped: client {dropped}");
            Expect.That(dropped.Object, "the client has the dropped lantern's object");
            Expect.That(!dropped.Tombstoned, "the client doesn't hold the dropped lantern tombstoned as Lost and Found's (A3)");
            ItemRecord h = await pair.Host.Run(() => Items.Record(lantern));
            ItemRelease.LanternState server = await pair.Host.Run(() => ItemRelease.InGame.ReadLantern(lantern));
            Report.Note($"host: {h}, {server.State}, wick {server.Wick:0.##}");
            Expect.That(h.Placement == "World" && !h.Lost, "the host has the lantern in the world, not in Lost and Found", h.ToString());
            Expect.Equal("Dropped", server.State, "the host's lantern's state");
            Expect.AtMost(Off(server.Wick, wick), 0.05, $"the host's wick off {wick}");
            ItemPose objectAfter = await pair.Host.Run(() => Items.Pose(lantern));
            Report.Note($"the host's lantern object: instance {objectBefore.Instance} before Lost and Found, {objectAfter.Instance} now");
            Expect.That(objectAfter.Present && objectAfter.Instance != objectBefore.Instance,
                "the host's lantern is a new object, made from its row (the old one went with the entry)", $"{objectBefore.Instance} then {objectAfter.Instance}");
            Expect.Equal(id, await pair.Host.Run(() => Owned.IdOf(lantern)), "the dropped lantern's id, under its old NetId");
            Expect.That(await pair.Host.Run(() => InGame.SameRecord(lantern)), "the dropped lantern still has its record from before Lost and Found");

            // 2. A toolbox's id (its container's id too, OI-CON-09) is the server's, whatever the client's copy says.
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, "client"));
            string minted = await pair.Host.Run(() => InGame.ObjectContainerId(box));
            Report.Note($"{o.Container} {box}: the server's item id {minted}");
            Require.That(minted.Length > 0, "the server's toolbox has an item id");
            await pair.Client.Run<int>(s => Items.Stash(s, box, false), 60);
            await Task.Delay(2000);   // the stash's row is written
            Expect.Equal(minted, await pair.Host.Run(() => InGame.RowContainerId(box)), "the item id in the server's row after the stash (A1)");
            await pair.Client.Run<StashState.Kept>(s => StashState.InGame.TakeOutAndStash(s, box), 60);
            await Task.Delay(2000);
            Expect.Equal(minted, await pair.Host.Run(() => InGame.RowContainerId(box)), "the item id in the server's row after a take-out and a second stash");
        }

        private static float Off(float value, float wanted) => Math.Abs(value - wanted);

        public sealed class Seen
        {
            public bool StandIn, Object, Tombstoned;
            public string State = "";
            public override string ToString() =>
                $"{(StandIn ? "stand-in" : "no stand-in")}, {(Object ? "an object" : "no object")}{(Tombstoned ? ", tombstoned" : "")}, state {(State.Length > 0 ? State : "none")}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static LiveItems.LiveItem remembered;

            /// <summary>Host: remembers the item's record (the object itself), for SameRecord. Result: whether it has one.</summary>
            [Step]
            public static bool RememberRecord(ushort id) => LiveItems.TryGet(id, out remembered) && remembered != null;

            /// <summary>Host: whether the item's record now is the one RememberRecord saw.</summary>
            [Step]
            public static bool SameRecord(ushort id) =>
                remembered != null && LiveItems.TryGet(id, out LiveItems.LiveItem now) && ReferenceEquals(now, remembered);

            /// <summary>Client: picks the lantern up, turns its wick to <paramref name="wick"/> and stashes it.</summary>
            [Step]
            public static IEnumerator TurnAndStash(ushort id, float wick)
            {
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                item.GetComponentInChildren<Lantern>(true).knob.SetValue(wick);
                for (int i = 0; i < 60; i++) yield return null;
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
                yield return Wait.Until(() => PlayerInventory.Holds(id), 5f, "the stash");
                yield return new WaitForSecondsRealtime(1.5f);
                Hands.ReleaseControl();
            }

            /// <summary>Client: what this game has of the item: its stand-in and the stand-in's state, its
            /// object, and whether it holds it tombstoned as collected into Lost and Found.</summary>
            [Step]
            public static Seen Look(ushort id)
            {
                bool standIn = InventoryStandIn.TryGet(id, out InventoryStandIn row);
                return new Seen
                {
                    StandIn = standIn,
                    State = standIn ? row.State ?? "" : "",
                    Object = NetworkedItem.TryGet(id, out NetworkedItem item) && item != null,
                    Tombstoned = NetworkedItemManager.Instance?.ClientPart?.IsLostAndFoundTombstoned(id) == true
                };
            }

            /// <summary>Client: takes the lantern out to the hand, reads it a moment later, and drops it.
            /// Result: what it read.</summary>
            [Step]
            public static IEnumerator TakeOutAndDrop(Step<ItemRelease.LanternState> step, ushort id)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                yield return PlayerInventory.TakeOut(id);
                for (int i = 0; i < 30; i++) yield return null;
                step.Result = ItemRelease.InGame.ReadLantern(id);
                yield return Hands.Drop();
                for (int i = 0; i < 60; i++) yield return null;
                Hands.ReleaseControl();
            }

            /// <summary>Host: the item id the server's object of the item saves, "" for none.</summary>
            [Step]
            public static string ObjectContainerId(ushort id)
            {
                if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) return "";
                string state = ItemStateText.Capture(item.gameObject);
                return state.Length > 0 ? (string)JObject.Parse(state)[ItemIdentityPatch.Key] ?? "" : "";
            }

            /// <summary>Host: the item id in the server's row of an item kept as data, "" for none.</summary>
            [Step]
            public static string RowContainerId(ushort id) =>
                LiveItems.TryGet(id, out LiveItems.LiveItem record) && ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow row)
                    ? (string)row.State?[ItemIdentityPatch.Key] ?? "" : "";
        }
    }
}
