// Container steps (docs/design/items/containers.md): a player deposits into and withdraws from a box
// they hold, through the game's own container calls, which the container UI turns into requests. Each
// [Step] runs inside a game: call it through pair.Host / pair.Client. A refusal is a result, not a
// throw, so a scenario can expect one.
using System;
using System.Collections;
using System.Linq;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;
using CUI = Multiplayer.Client.Items.Containers.ContainerUi;

namespace Dvmp.Scenarios
{
    public static class Boxes
    {
        /// <summary>What a deposit or withdrawal came to: "accepted" (a withdrawal's adds the item's NetId), else the refusal's detail.</summary>
        public const string Accepted = "accepted";

        /// <summary>
        /// Puts the item in this game's inventory (unless it's there), takes the box in hand, opens it
        /// and deposits the item, then closes it. Result: "accepted" or the refusal's detail.
        /// </summary>
        [Step]
        public static IEnumerator Deposit(Step<string> step, ushort boxId, ushort itemId)
        {
            yield return Stash(itemId);
            yield return OpenBox(boxId);
            yield return DepositIntoOpen(step, boxId, itemId);
            yield return CloseBox(boxId);
        }

        /// <summary>Puts the item in this game's inventory, unless it's there.</summary>
        [Step]
        public static IEnumerator Stash(ushort itemId)
        {
            if (PlayerInventory.Holds(itemId))
                yield break;
            NetworkedItem item = null;
            yield return Wait.Until(() => NetworkedItem.TryGet(itemId, out item) && item != null, 20f, "the item to arrive");
            if (Hands.Held != null) yield return Hands.Drop();
            yield return LocalPlayer.GoTo(item);
            yield return Hands.Pickup(itemId);
            DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
            yield return Wait.Until(() => PlayerInventory.Holds(itemId), 10f, "the item in the inventory");
            for (int i = 0; i < 30; i++) yield return null;
        }

        /// <summary>Takes the box in hand and opens it.</summary>
        [Step]
        public static IEnumerator OpenBox(ushort boxId)
        {
            NetworkedItem box = null;
            yield return Wait.Until(() => NetworkedItem.TryGet(boxId, out box) && box != null, 20f, "the box to arrive");
            yield return Hold(box);
            yield return Open(box.GetComponentInChildren<DV.Items.ItemContainer>(true));
        }

        /// <summary>Deposits an item from this game's inventory into the box, open in hand. Result: "accepted" or the refusal's detail.</summary>
        [Step]
        public static IEnumerator DepositIntoOpen(Step<string> step, ushort boxId, ushort itemId)
        {
            if (!NetworkedItem.TryGet(boxId, out NetworkedItem box) || box == null || !CUI.Active)
                throw new Exception("the box isn't open");
            var c = box.GetComponentInChildren<DV.Items.ItemContainer>(true);
            uint seq = CUI.MutationSequence;
            // Put away, it's a stand-in on a client (D14); the UI answers false and sends the request.
            if (c.AddItem(PlayerInventory.SlotObject(itemId), -1)) throw new Exception("the deposit was not intercepted");
            yield return Wait.Until(() => CUI.MutationSequence != seq && CUI.LastMutation != null, 10f, "the deposit's result");
            step.Result = CUI.LastMutation.Accepted ? Accepted : CUI.LastMutation.Detail ?? "refused";
            for (int i = 0; i < 30; i++) yield return null;   // the refreshed view
        }

        /// <summary>Closes the box, if it's open.</summary>
        [Step]
        public static IEnumerator CloseBox(ushort boxId)
        {
            if (NetworkedItem.TryGet(boxId, out NetworkedItem box) && box != null)
                yield return Close(box.GetComponentInChildren<DV.Items.ItemContainer>(true));
        }

        /// <summary>
        /// Takes the box in hand (out of the inventory, where it's a stand-in, D14), opens it and
        /// withdraws what is in its slot (the first filled one when <paramref name="slot"/> is -1),
        /// then closes it. Result: "accepted NETID" or the refusal's detail.
        /// </summary>
        [Step]
        public static IEnumerator Withdraw(Step<string> step, ushort boxId, int slot)
        {
            if (PlayerInventory.Holds(boxId))
            {
                if (Hands.Held != null) yield return Hands.Drop();
                yield return PlayerInventory.TakeOut(boxId);
            }
            NetworkedItem box = null;
            yield return Wait.Until(() => NetworkedItem.TryGet(boxId, out box) && box != null, 20f, "the box to arrive");
            yield return Hold(box);
            var c = box.GetComponentInChildren<DV.Items.ItemContainer>(true);
            yield return Open(c);
            if (slot < 0)
                slot = CUI.ActiveSlots.Any() ? CUI.ActiveSlots.First() : throw new Exception("the box is empty");
            uint seq = CUI.MutationSequence;
            if (c.RemoveItem(slot, true, false)) throw new Exception("the withdrawal was not intercepted");
            yield return Wait.Until(() => CUI.MutationSequence != seq && CUI.LastMutation != null, 10f, "the withdrawal's result");
            step.Result = CUI.LastMutation.Accepted ? $"{Accepted} {CUI.LastMutation.ItemNetId}" : CUI.LastMutation.Detail ?? "refused";
            if (CUI.LastMutation.Accepted && CUI.LastMutation.ItemNetId != 0)
                Items.Track(CUI.LastMutation.ItemNetId);
            yield return Close(c);
        }

        /// <summary>Opens the box (taken in hand) and reads its rows. Result: how many.</summary>
        [Step]
        public static IEnumerator Rows(Step<int> step, ushort boxId)
        {
            NetworkedItem box = null;
            yield return Wait.Until(() => NetworkedItem.TryGet(boxId, out box) && box != null, 20f, "the box to arrive");
            yield return Hold(box);
            var c = box.GetComponentInChildren<DV.Items.ItemContainer>(true);
            yield return Open(c);
            step.Result = CUI.ActiveRowCount;
            yield return Close(c);
        }

        /// <summary>
        /// Host: a container prefab whose box takes a box of its own kind, by the running game's own
        /// check on two live boxes (the prefab's mask may differ from a fresh object's), tried
        /// <paramref name="preferred"/> first. Result: its prefab, or "" when none does, as in vanilla,
        /// where no box takes its own kind (<see cref="Nesting"/> finds two kinds that nest).
        /// </summary>
        [Step]
        public static IEnumerator SelfNesting(Step<string> step, string preferred)
        {
            var specs = DV.Globals.G.Items.items
                .Where(spec => spec != null && spec.gameObject.GetComponentInChildren<DV.Items.ItemContainer>(true) != null)
                .OrderBy(spec => spec.ItemPrefabName == preferred ? 0 : 1).ToList();
            Vector3 at = PlayerManager.PlayerTransform.position + Vector3.up * 50f;
            step.Result = "";
            foreach (var spec in specs)
            {
                var a = UnityEngine.Object.Instantiate(spec.gameObject, at, Quaternion.identity, WorldMover.OriginShiftParent);
                var b = UnityEngine.Object.Instantiate(spec.gameObject, at + Vector3.right, Quaternion.identity, WorldMover.OriginShiftParent);
                yield return null;
                bool takes = a.GetComponentInChildren<DV.Items.ItemContainer>(true).ValidItem(b);
                UnityEngine.Object.Destroy(a);
                UnityEngine.Object.Destroy(b);
                yield return null;
                if (takes)
                {
                    step.Result = spec.ItemPrefabName;
                    yield break;
                }
            }
        }

        /// <summary>
        /// Host: two container prefabs for a box in a box: an outer whose box takes the inner, both
        /// taking <paramref name="item"/>, the inner tried as <paramref name="inner"/> first. No
        /// vanilla box takes its own kind: the crate takes a toolbox or a briefcase, the toolbox a
        /// registrator, the registrator a folder. Checked by the running game on live
        /// objects (a prefab's mask may differ from a fresh object's). Result: "outer inner", or ""
        /// when no pair nests.
        /// </summary>
        [Step]
        public static IEnumerator Nesting(Step<string> step, string inner, string item)
        {
            var specs = DV.Globals.G.Items.items
                .Where(spec => spec != null && spec.gameObject.GetComponentInChildren<DV.Items.ItemContainer>(true) != null)
                .OrderBy(spec => spec.ItemPrefabName == inner ? 0 : 1).ToList();
            var itemSpec = DV.Globals.G.Items.items.FirstOrDefault(spec => spec != null && spec.ItemPrefabName == item)
                ?? throw new Exception($"no item prefab {item}");
            Vector3 at = PlayerManager.PlayerTransform.position + Vector3.up * 50f;
            var boxes = specs.Select((spec, i) => UnityEngine.Object.Instantiate(spec.gameObject, at + Vector3.right * 2f * i, Quaternion.identity, WorldMover.OriginShiftParent)).ToList();
            var held = UnityEngine.Object.Instantiate(itemSpec.gameObject, at + Vector3.back * 2f, Quaternion.identity, WorldMover.OriginShiftParent);
            yield return null;
            step.Result = "";
            for (int i = 0; i < boxes.Count && step.Result.Length == 0; i++)
            {
                if (!boxes[i].GetComponentInChildren<DV.Items.ItemContainer>(true).ValidItem(held)) continue;
                for (int j = 0; j < boxes.Count; j++)
                    if (j != i && boxes[j].GetComponentInChildren<DV.Items.ItemContainer>(true) is { } outer &&
                        outer.ValidItem(boxes[i]) && outer.ValidItem(held))
                    {
                        step.Result = $"{specs[j].ItemPrefabName} {specs[i].ItemPrefabName}";
                        break;
                    }
            }
            foreach (GameObject box in boxes) UnityEngine.Object.Destroy(box);
            UnityEngine.Object.Destroy(held);
            yield return null;
        }

        /// <summary>
        /// Host: a row nobody owns into the box's first free slot, as a load makes one from a save
        /// (a deposit can't: only an owned item goes in). The graph is loaded again with it, so the
        /// box must be closed. Result: the row's id.
        /// </summary>
        [Step]
        public static string PutUnowned(ushort boxId, string prefab)
        {
            if (!ContainerServer.TryResolveShell(boxId, out Guid container) ||
                !ContainerServer.Graph.TryGetContainer(container, out ContainerRecord box))
                throw new Exception("the box has no container");
            ContainerGraphSnapshot now = ContainerServer.Graph.ExportSnapshot();
            int slot = Enumerable.Range(0, box.Capacity).Where(s =>
                !now.Items.Any(i => i.ParentContainerId == container && i.Slot == s)).DefaultIfEmpty(-1).First();
            if (slot < 0) throw new Exception("the box is full");
            Guid id = Guid.NewGuid();
            now.Items.Add(new StoredItemRecord { PersistentItemId = id, PrefabName = prefab, DisplayName = prefab, ParentContainerId = container, Slot = slot });
            var refused = ContainerServer.Graph.ImportRows(now.Containers, now.Items);
            if (refused.Count > 0) throw new Exception("the graph refused rows: " + string.Join(", ", refused.Select(r => r.Reason)));
            return id.ToString("D");
        }

        /// <summary>Host: the slot of the box the stored item is in, or -1.</summary>
        [Step]
        public static int StoredSlot(string id) =>
            Guid.TryParse(id, out Guid wanted) && ContainerServer.Graph.TryGetItem(wanted, out StoredItemRecord row) ? row.Slot : -1;

        /// <summary>Host: the id of the box the stored item is in, or "".</summary>
        [Step]
        public static string StoredIn(string id) =>
            Guid.TryParse(id, out Guid wanted) && ContainerServer.Graph.TryGetItem(wanted, out StoredItemRecord row) ? row.ParentContainerId.ToString("D") : "";

        /// <summary>Host: the box's revision now (0 when there's no such box).</summary>
        [Step]
        public static uint RevisionOf(string id) =>
            Guid.TryParse(id, out Guid wanted) && ContainerServer.Graph.TryGetContainer(wanted, out ContainerRecord box) ? box.Revision : 0;

        /// <summary>Host: the wire handle of an item or box by its id, as a view or list gives it.</summary>
        [Step]
        public static uint HandleOf(string id) => Multiplayer.Server.Items.Projection.ItemHandles.Of(Guid.Parse(id));

        /// <summary>
        /// Client: moves a stored item into a slot of a box, sent as the container UI sends it with that
        /// box open: both revisions the open box's. Result: "accepted" or the refusal's detail.
        /// </summary>
        [Step]
        public static IEnumerator MoveAsOpen(Step<string> step, uint itemHandle, uint boxHandle, uint openRevision, int slot)
        {
            Multiplayer.Shared.Protocol.Packets.Items.ClientboundItemResultPacket result = null;
            uint requestId = 0;
            void Got(Multiplayer.Shared.Protocol.Packets.Items.ClientboundItemResultPacket p) { if (p.RequestId == requestId) result = p; }
            ItemClient.Current.ItemResultReceived += Got;
            try
            {
                requestId = ItemClient.Current.SendItemRequest(new Multiplayer.Shared.Protocol.Data.Items.ItemRequest
                {
                    Action = Multiplayer.Shared.Protocol.Data.Items.ItemAction.MoveInContainer,
                    ItemHandle = itemHandle,
                    ExpectedRevision = openRevision,
                    Container = boxHandle,
                    ContainerRevision = openRevision,
                    Slot = slot
                });
                yield return Wait.Until(() => result != null, 15f, "the move's result");
            }
            finally
            {
                ItemClient.Current.ItemResultReceived -= Got;
            }
            step.Result = result.Accepted ? Accepted : result.Detail ?? "refused";
        }

        /// <summary>Host: the cold graph checked whole, as the load would check a save of it: "ok" or why not.</summary>
        [Step]
        public static string GraphCheck()
        {
            ContainerOperationResult result = ContainerServer.Graph.ValidateWholeGraph();
            return result.Accepted ? "ok" : result.Reason;
        }

        private static IEnumerator Hold(NetworkedItem box)
        {
            if (Hands.Held == box) yield break;
            if (Hands.Held != null) yield return Hands.Drop();
            yield return LocalPlayer.GoTo(box);
            yield return Hands.Pickup(box.NetId);
        }

        private static IEnumerator Open(DV.Items.ItemContainer c)
        {
            var open = DV.InventorySystem.Inventory.Instance.ItemContainerRegistry.ActiveContainer;   // left open earlier
            if (open != null && open != c) { open.ToggleContainerAccess(); for (int i = 0; i < 10; i++) yield return null; }
            if (!CUI.Active) c.ToggleContainerAccess();
            yield return Wait.Until(() => CUI.Active, 10f, "the container view");
            for (int i = 0; i < 10; i++) yield return null;
        }

        private static IEnumerator Close(DV.Items.ItemContainer c)
        {
            if (CUI.Active) c.ToggleContainerAccess();
            for (int i = 0; i < 30; i++) yield return null;
        }
    }
}
