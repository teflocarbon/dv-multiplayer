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
        /// Takes the box in hand, opens it and withdraws what is in its slot (the first filled one
        /// when <paramref name="slot"/> is -1), then closes it. Result: "accepted NETID" or the
        /// refusal's detail.
        /// </summary>
        [Step]
        public static IEnumerator Withdraw(Step<string> step, ushort boxId, int slot)
        {
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
        /// <paramref name="preferred"/> first. Result: its prefab, or "" when none does.
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
