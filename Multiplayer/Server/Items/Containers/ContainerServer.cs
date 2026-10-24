using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DV.CabControls;
using DV;
using DV.InventorySystem;
using DV.JObjectExtstensions;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.Access;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.Access;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.ChangeCauses;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.Containers;

/// <summary>Host authority for detached container contents; shells remain ordinary network items.</summary>
public static class ContainerServer
{
    private const uint ScanIntervalTicks = 24;
    private static uint lastScanTick; // the tick can advance by more than one a frame
    private static readonly HostRuntimeCompatibility compatibility = new();
    private static readonly LedgerOwners owners = new();
    private static ContainerGraph graph = new(compatibility: compatibility, owners: owners);
    private static readonly Dictionary<ushort, Guid> shellByNetId = new();
    // No request is served before the load has run (OI-CON-05, OI-PER-23).
    private const string Loading = "items-loading";

    public static ContainerGraph Graph => graph;

    public static void HostTick(uint tick)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true || unchecked(tick - lastScanTick) < ScanIntervalTicks) return;
        lastScanTick = tick;
        BindPhysicalShells();
    }

    public static void Clear()
    {
        compatibility.Clear();
        graph = new ContainerGraph(compatibility: compatibility, owners: owners);
        shellByNetId.Clear();
    }

    /// <summary>
    /// The load (docs/design/items/save-load.md): the saved containers and their items, each row on
    /// its own. A row the graph refuses is returned, and the rest load (OI-CON-04, F18).
    /// </summary>
    internal static IReadOnlyList<ContainerRowRefusal> LoadRows(IEnumerable<ContainerRecord> containers,
        IEnumerable<StoredItemRecord> items) => graph.ImportRows(containers, items);

    public static bool TryResolveShell(ushort shellNetId, out Guid containerId) =>
        TryGetOrBindShell(shellNetId, out containerId);

    public static ContainerOperationResult Browse(ServerPlayer actor, ushort shellNetId,
        int offset, int count, out ContainerView view)
    {
        view = null;
        if (!ServerLedger.Loaded)
            return Failure(ContainerOperationKind.Browse, ContainerOperationStatus.NotFound, Loading);
        if (actor == null || !TryGetOrBindShell(shellNetId, out Guid id))
            return Failure(ContainerOperationKind.Browse, ContainerOperationStatus.NotFound,
                "container-shell-not-bound");
        // The two local debug instances can intentionally share the same save GUID. A live
        // shell still has an unambiguous authoritative player owner, so enforce that boundary
        // before the durable graph identity check. This also prevents a copied save identity
        // from disclosing another connected player's physical container.
        if (!ActorOwnsPhysicalShell(actor, shellNetId))
            return Failure(ContainerOperationKind.Browse, ContainerOperationStatus.Unauthorized,
                "container-private");
        return graph.Browse(actor.Guid.ToString("D"), id, offset, count, out view);
    }

    public static ContainerOperationResult Browse(ServerPlayer actor, uint containerHandle,
        int offset, int count, out ContainerView view)
    {
        view = null;
        if (!ServerLedger.Loaded)
            return Failure(ContainerOperationKind.Browse, ContainerOperationStatus.NotFound, Loading);
        if (actor == null || !graph.TryGetContainer(containerHandle, out ContainerRecord container))
            return Failure(ContainerOperationKind.Browse, ContainerOperationStatus.NotFound,
                "container-handle-not-found");
        return graph.Browse(actor.Guid.ToString("D"), container.PersistentContainerId,
            offset, count, out view);
    }

    public static ContainerOperationResult Deposit(ServerPlayer actor, Guid operationId,
        ushort shellNetId, uint destinationHandle, uint expectedRevision, int slot,
        ushort itemNetId)
    {
        if (!ServerLedger.Loaded)
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.NotFound, Loading, operationId);
        Guid destinationId = Guid.Empty;
        ContainerRecord destination = null;
        bool destinationResolved = destinationHandle != 0
            ? graph.TryGetContainer(destinationHandle, out destination) &&
              (destinationId = destination.PersistentContainerId) != Guid.Empty
            : TryGetOrBindShell(shellNetId, out destinationId);
        if (destinationResolved && destination == null)
            destinationResolved = graph.TryGetContainer(destinationId, out destination);
        if (actor == null || !destinationResolved ||
            !NetworkedItem.TryGet(itemNetId, out NetworkedItem item) || item?.Item == null)
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.NotFound,
                "deposit-runtime-object-not-found", operationId);
        if (shellNetId != 0 && !ActorOwnsPhysicalShell(actor, shellNetId))
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.Unauthorized,
                "container-private", operationId);
        bool possessed = LiveItems.TryGet(itemNetId,
                out LiveItems.LiveItem authority) &&
            authority.Holder == actor.Guid && authority.Placement is
                (ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory);
        if (!possessed && actor.PlayerId == InventoryIntegration.LocalPlayerId &&
            InventoryIntegration.ContainsActive(item.gameObject))
        {
            ItemUpdate reconciliation = item.CreateUpdateData(
                ItemUpdate.ItemUpdateType.ItemState);
            if (reconciliation != null)
            {
                reconciliation.ItemState = ItemState.InInventory;
                reconciliation.PlayerId = actor.PlayerId;
                reconciliation.AuthorityRevision = authority?.Revision ?? 0;
                possessed = LiveItems.TryApplyTransition(item, reconciliation,
                    actor, ItemTransitionReason.HostLocalState, true, out _,
                    cause: ItemChangeLog.Move(ItemMoveReason.HostCorrection, actor,
                        "in the host's inventory, though recorded elsewhere; reconciled to deposit it"));
                if (possessed)
                {
                    item.ApplyAuthorityMetadata(reconciliation);
                    LiveItems.TryGet(itemNetId, out authority);
                    Publish("container.deposit-host-possession-reconciled", shellNetId,
                        new ContainerOperationResult
                        {
                            OperationId = operationId,
                            Kind = ContainerOperationKind.Deposit,
                            Status = ContainerOperationStatus.Accepted
                        }, new()
                        {
                            ["itemNetId"] = itemNetId,
                            ["inventorySlot"] = DV.InventorySystem.Inventory.Instance?.IndexOf(
                                item.gameObject) ?? -1
                        });
                }
            }
        }
        if (!possessed)
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.Unauthorized,
                "deposit-item-not-possessed", operationId);
        if (!ItemAccessServer.Allows(actor, ItemAccessAction.Deposit, item, authority, out string refused))
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.Unauthorized,
                refused, operationId);
        NetworkedItem.TryGet(shellNetId, out NetworkedItem shell);
        AItemContainer runtimeContainer = shell?.GetComponent<AItemContainer>();
        // The opened root shell is the game's compatibility oracle. Nested shells may be
        // detached, but graph validation still enforces their capacity/cycle/depth rules.
        bool destinationIsOpenedShell = shellNetId != 0 &&
            TryGetOrBindShell(shellNetId, out Guid shellContainerId) &&
            shellContainerId == destinationId;
        if (destinationIsOpenedShell &&
            (runtimeContainer == null || !runtimeContainer.ValidItem(item.gameObject)))
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.Incompatible,
                "game-container-rejected-item", operationId);

        // The item's own id, not one of the record's: one id for life (ledger.md). A container
        // item's container has that id too (OI-CON-09).
        Guid itemId = authority.PersistentItemId;
        if (itemId == Guid.Empty)
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.InvalidRecord,
                "item-has-no-id", operationId);
        if (IsShell(item))
            EnsureShellRecord(item, itemId);
        JObject detached = item.GetComponent<ItemSaveData>()?.SaveItemData();
        StoredItemRecord record = new()
        {
            PersistentItemId = itemId,
            PrefabName = item.Item.InventorySpecs?.ItemPrefabName ?? item.name,
            DisplayName = item.Item.InventorySpecs?.LocalizedName ?? item.name,
            AuthoredItemKey = item.AuthoredItemKey ?? string.Empty,
            StateVersion = 1,
            DetachedState = detached == null ? Array.Empty<byte>() :
                Encoding.UTF8.GetBytes(detached.ToString(Formatting.None))
        };
        // The live container and item are the strongest compatibility oracle. Prefab registry
        // objects are not fully initialized, so calling ValidItem on them can incorrectly reject
        // combinations that the running game has already accepted.
        if (destinationIsOpenedShell)
            compatibility.Remember(destination.PrefabName, record.PrefabName, true);
        DepositCommand command = new()
        {
            OperationId = operationId,
            ActorIdentity = actor.Guid.ToString("D"),
            DestinationContainerId = destinationId,
            ExpectedDestinationRevision = expectedRevision,
            DestinationSlot = slot,
            PhysicalNetId = itemNetId,
            Item = record
        };
        ContainerOperationResult check = graph.CheckDeposit(command);
        if (!check.Accepted)
            return check;

        // The ledger first (docs/design/items/containers.md, "Deposit"): the item's home is the
        // slot the graph checked, and every later failure moves it back.
        Home before = ServerLedger.HomeOf(authority), stored = Home.Container(destinationId, slot);
        ChangeCause<ItemMoveReason> depositing = ItemChangeLog.Move(ItemMoveReason.DepositedInContainer, actor,
            $"{destination.PrefabName} slot {slot}");
        string ledgerRefused = ServerLedger.CommitStored(itemId, record.PrefabName, before, stored, depositing);
        if (ledgerRefused.Length > 0)
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.InvalidState,
                "deposit-ledger-refused:" + ledgerRefused, operationId);
        ContainerOperationResult result = graph.CommitDeposit(command);
        if (!result.Accepted)
        {
            RollBackDeposit(itemId, record.PrefabName, stored, before, result.Reason);
            return result;
        }

        // Retirement (OI-CON-06): until its Destroy is sent the object is the item, and a failure
        // takes the deposit back, so the item is never both an object and a row; once it's sent
        // the row is the item, and the object goes whatever else fails.
        bool sent = false;
        try
        {
            ItemUpdate destroy = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Destroy) ??
                throw new InvalidOperationException("deposit-destroy-projection-failed");
            LiveItems.WriteToSnapshot(authority, destroy,
                ItemTransitionReason.ContainerDeposit);
            ChangeCause<ItemRemovalReason> deposited = ItemChangeLog.Removal(ItemRemovalReason.DepositedInContainer,
                actor, $"{destination.PrefabName} slot {slot}, now cold record {record.PersistentItemId:D}");
            destroy.RemovalCause = deposited;
            // OnDestroy logs it: a retirement isn't sent again from there.
            item.NoteRemovalCause(deposited);
            item.BeginColdStorageRetirement();
            // Retirement is a transactional boundary, not an ordinary dirty-state update.
            // Publish it immediately on the reliable ordered channel before the Unity object
            // is torn down; waiting for the next item-manager tick can lose the Destroy after
            // the representation and its interest membership have already been removed. It goes
            // by interest (OI-CON-12): to the players who know the object, and the depositor, whose
            // stand-in it was; nobody else ever had it.
            List<ServerPlayer> told = new();
            foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
            {
                player.NearbyItems.Remove(item);
                if (player.KnownItems.Remove(item) || player == actor)
                    told.Add(player);
                player.AcknowledgedWorldItems.Remove(item.NetId);
            }
            foreach (ServerPlayer player in told)
                NetworkServer.Instance.SendItemUpdate(destroy, player);
            sent = true;
            // Out of interest now, not when Unity destroys it at the end of the frame: a scan
            // before then would find a player who no longer knows it and send it again.
            ServerItemManager.Instance?.UnregisterHostWorldItem(item);
            item.Item.ForceEndInteraction();
            InventoryIntegration.RevokeMembership(item.gameObject, item.NetId);
            InventoryIntegration.PurgeContainerMembership(item.gameObject);
            StorageIntegration.MoveTo(item.Item, StorageMembership.None);
            UnityEngine.Object.Destroy(item.gameObject);
        }
        catch (Exception exception)
        {
            Multiplayer.LogException("Cold-container representation retirement failed", exception);
            Publish("container.deposit-retirement-failed", shellNetId, result, new()
            {
                ["itemNetId"] = itemNetId,
                ["destroySent"] = sent,
                ["error"] = exception.Message
            }, DebugSeverity.Error);
            if (!sent)
            {
                item.CancelColdStorageRetirement();
                RollBackDeposit(itemId, record.PrefabName, stored, before, "deposit-retirement-failed");
                return graph.RevokeDeposit(operationId, itemId, "deposit-retirement-failed");
            }
            UnityEngine.Object.Destroy(item.gameObject);
        }
        graph.FinalizeDepositRetirement(operationId, itemId);
        Publish("container.deposit-committed", shellNetId, result, new()
        {
            ["itemNetId"] = itemNetId,
            ["slot"] = slot,
            ["persistentItemId"] = itemId.ToString("D")
        });
        return result;
    }

    // A deposit that went no further: the ledger puts the item back where it was.
    private static void RollBackDeposit(Guid itemId, string prefab, Home stored, Home before, string why) =>
        ServerLedger.CommitStored(itemId, prefab, stored, before,
            ItemChangeLog.Move(ItemMoveReason.HostCorrection, detail: "deposit rolled back: " + why));

    /// <summary>
    /// A withdrawal into the actor's inventory (docs/design/items/containers.md, "Withdrawal"). The
    /// server picks the slot (OI-ACC-04, BR LF3): the item's claim when the actor owns it, else the
    /// lowest free one. With none free, an owned item goes to its owner's Lost and Found and an
    /// unowned one to the actor's feet. The client's slot isn't used.
    /// </summary>
    public static IEnumerator Withdraw(ServerPlayer actor, Guid operationId,
        uint containerHandle, uint expectedRevision, int sourceSlot,
        Action<ContainerOperationResult, ushort> completed)
    {
        if (!ServerLedger.Loaded)
        {
            completed?.Invoke(Failure(ContainerOperationKind.Withdraw, ContainerOperationStatus.NotFound, Loading,
                operationId), 0);
            yield break;
        }
        if (actor == null || !graph.TryGetContainer(containerHandle, out ContainerRecord container))
        {
            completed?.Invoke(Failure(ContainerOperationKind.Withdraw,
                ContainerOperationStatus.NotFound, "container-handle-not-found", operationId), 0);
            yield break;
        }
        WithdrawalCommand command = new()
        {
            OperationId = operationId,
            ActorIdentity = actor.Guid.ToString("D"),
            SourceContainerId = container.PersistentContainerId,
            ExpectedSourceRevision = expectedRevision,
            SourceSlot = sourceSlot
        };
        ContainerOperationResult prepared = graph.PrepareWithdrawal(command,
            out StoredItemRecord record);
        if (!prepared.Accepted)
        {
            completed?.Invoke(prepared, 0);
            yield break;
        }
        // Its owner is the ledger's (OI-CON-02): somebody else's item comes out only while they're
        // connected, and one nobody owns comes out nobody's.
        Guid.TryParse(graph.OwnerOf(record.PersistentItemId), out Guid owner);
        string refused = owner != Guid.Empty && owner != actor.Guid && !PlayerNumbers.IsConnected(owner)
            ? "withdraw-persistent-owner-offline"
            : !ItemAccessServer.AllowsRow(actor, ItemAccessAction.Withdraw, owner, record.PrefabName,
                record.AuthoredItemKey, FindItemSpec(record.PrefabName)?.IsEssential == true, out string denied)
                ? denied : string.Empty;
        if (refused.Length > 0)
        {
            graph.AbortWithdrawal(operationId, refused);
            completed?.Invoke(Failure(ContainerOperationKind.Withdraw,
                refused == "withdraw-persistent-owner-offline" ? ContainerOperationStatus.InvalidState
                    : ContainerOperationStatus.Unauthorized, refused, operationId), 0);
            yield break;
        }

        // Into the inventory as a row (D14, OI-COLD-11): the item is kept as data from the start, in
        // its slot, and the withdrawer's game draws its stand-in. No object is made on the server,
        // unless no slot is free and it goes loose. The ledger moves first, and back if the item
        // can't be made or the graph refuses (OI-CON-03).
        Home inBox = Home.Container(container.PersistentContainerId, sourceSlot), taken = default;
        bool ledgerMoved = false;
        LiveItems.LiveItem loose = null;
        try
        {
            if (actor.PlayerId == 0)
                throw new InvalidOperationException("withdraw-actor-left");
            if (FindItemSpec(record.PrefabName) == null)
                throw new InvalidOperationException("withdraw-prefab-not-found");
            SavedItemRow row = ItemSaveLoad.RowOfStored(record);
            row.OwnerIdentity = owner;
            int slot = ServerLedger.FreeSlot(row.PersistentItemId, owner, ServerLedger.ClaimOf(row.PersistentItemId),
                actor.Guid);
            ChangeCause<ItemMoveReason> cause = ItemChangeLog.Move(ItemMoveReason.WithdrawnFromContainer, actor,
                ItemChangeCauses.Detail(ItemChangeLog.Describe(
                        new ItemWhereabouts(LivePlacement.Container, Guid.Empty, owner),
                        slot >= 0 ? new ItemWhereabouts(LivePlacement.PlayerInventory, actor.Guid, owner)
                            : new ItemWhereabouts(LivePlacement.World, Guid.Empty, owner)),
                    slot >= 0 ? "slot " + slot : "no slot free"));
            if (slot < 0)
            {
                // Nowhere in the inventory: loose at the withdrawer's feet, in sight.
                row.Placement = ItemPlacementKind.World;
                row.WorldParentKind = ItemWorldParentKind.World;
                row.Position = actor.AbsoluteWorldPosition + Vector3.up * 0.3f;
                row.Rotation = Quaternion.identity;
            }
            taken = slot >= 0 ? Home.InSlot(actor.Guid, slot) : Home.World;
            string ledgerRefused = ServerLedger.CommitStored(record.PersistentItemId, record.PrefabName, inBox, taken, cause);
            if (ledgerRefused.Length > 0)
                throw new InvalidOperationException("withdraw-ledger-refused:" + ledgerRefused);
            ledgerMoved = true;
            ContainerOperationResult materialized = graph.MarkWithdrawalMaterialized(operationId);
            if (!materialized.Accepted)
                throw new InvalidOperationException("withdraw-materialization-phase-rejected:" +
                    materialized.Reason);
            // A scene's own item stays taken from the scene, as the object it was would have.
            ItemSaveLoad.RecordAuthoredTaken(row);
            // Its entry is where it goes already: the row is only placed, not settled again.
            LiveItems.LiveItem made = slot >= 0
                ? PlayerItems.Issue(actor, row, slot, cause, own: _ => string.Empty)
                : ItemSaveLoad.MakeLive(row, owner != Guid.Empty, out loose) ? loose : null;
            if (made == null)
                throw new InvalidOperationException("withdraw-authoritative-registration-failed");
            ContainerOperationResult committed = graph.CommitWithdrawal(operationId);
            if (!committed.Accepted)
            {
                if (slot >= 0)
                    PlayerItems.Unissue(made, "its withdrawal wasn't committed");
                throw new InvalidOperationException("withdraw-logical-commit-failed:" + committed.Reason);
            }
            loose = null;
            // An owned item with no slot goes on to its owner's Lost and Found; if that refuses it,
            // it stays at the withdrawer's feet.
            if (slot < 0 && owner != Guid.Empty && NetworkedItem.TryGet(made.NetId, out NetworkedItem item) && item != null &&
                !LostAndFoundServer.Collect(item, LostItemReason.InventoryOverflow, "withdrawn into a full inventory"))
                Multiplayer.LogWarning($"Cold-container withdrawal: {record.PrefabName} ({record.PersistentItemId:D}) had no " +
                                       $"slot in {actor.Username}'s inventory and Lost and Found refused it; it is at their feet");
            Publish("container.withdrawal-committed", 0, committed, new()
            {
                ["itemNetId"] = made.NetId,
                ["sourceSlot"] = sourceSlot,
                ["destinationInventorySlot"] = slot,
                ["containerHandle"] = containerHandle
            });
            completed?.Invoke(committed, made.NetId);
        }
        catch (Exception exception)
        {
            // A loose object made for a withdrawal that didn't commit goes back as a deposit's does.
            if (loose != null && NetworkedItem.TryGet(loose.NetId, out NetworkedItem made) && made != null)
            {
                made.NoteRemovalCause(ItemChangeLog.Removal(ItemRemovalReason.DepositedInContainer, actor,
                    "its withdrawal wasn't committed"));
                made.BeginColdStorageRetirement();
                UnityEngine.Object.Destroy(made.gameObject);
            }
            if (ledgerMoved)
                ServerLedger.CommitStored(record.PersistentItemId, record.PrefabName, taken, inBox,
                    ItemChangeLog.Move(ItemMoveReason.HostCorrection, actor, "withdrawal rolled back: " + exception.Message));
            graph.AbortWithdrawal(operationId, exception.Message);
            if (exception.Message != "withdraw-actor-left")
                Multiplayer.LogException("Cold-container withdrawal failed", exception);
            completed?.Invoke(Failure(ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidState, exception.Message, operationId), 0);
        }
        yield break;
    }

    /// <summary>Where an item made only to go into Lost and Found is made: DV's own safety position.</summary>
    internal static Vector3 SafetyPosition => StartingItemsController.ITEM_INSTANTIATION_SAFETY_POSITION - WorldMover.currentMove;

    /// <summary>
    /// The server takes a settled row out of its box with no player's request (a retrieval:
    /// docs/design/items/containers.md, "Taken out by the server"), made an object again loose at
    /// <paramref name="position"/>. Its ledger entry moves from the box to the world first. Null, with
    /// the reason, when it isn't settled (OI-LIST-18) or can't be made; then nothing has changed.
    /// </summary>
    internal static NetworkedItem TakeOutLoose(Guid itemId, Vector3 position, ChangeCause<ItemMoveReason> cause,
        out string reason)
    {
        reason = string.Empty;
        if (!graph.TryGetItem(itemId, out StoredItemRecord stored))
        {
            reason = "container-item-not-found";
            return null;
        }
        if (!graph.IsSettled(itemId))
        {
            reason = "container-item-busy";
            return null;
        }
        NetworkedItem item = MakeLoose(stored, position, cause);
        if (item == null)
        {
            reason = "container-item-not-made";
            return null;
        }
        if (graph.TakeOut(itemId, out reason) != null)
            return item;
        // Settled a moment ago, so this doesn't happen; if it does, the row stays the item.
        item.NoteRemovalCause(ItemChangeLog.Removal(ItemRemovalReason.DepositedInContainer, detail: "it couldn't be taken out: " + reason));
        item.BeginColdStorageRetirement();
        UnityEngine.Object.Destroy(item.gameObject);
        MoveBack(stored, "it couldn't be taken out: " + reason);
        return null;
    }

    // A stored row made an object again, loose: an owned one's ledger entry moves from its box to
    // the world first, and back if it can't be made. The graph's row is the caller's.
    private static NetworkedItem MakeLoose(StoredItemRecord stored, Vector3 position, ChangeCause<ItemMoveReason> cause)
    {
        Guid.TryParse(graph.OwnerOf(stored.PersistentItemId), out Guid owner);
        SavedItemRow row = ItemSaveLoad.RowOfStored(stored);
        row.OwnerIdentity = owner;
        row.Placement = ItemPlacementKind.World;
        row.WorldParentKind = ItemWorldParentKind.World;
        row.Position = position;
        row.Rotation = Quaternion.identity;
        if (owner != Guid.Empty)
        {
            string refused = ServerLedger.CommitStored(stored.PersistentItemId, stored.PrefabName,
                Home.Container(stored.ParentContainerId, stored.Slot), Home.World, cause);
            if (refused.Length > 0)
            {
                Multiplayer.LogWarning($"Cold container: {stored.PrefabName} ({stored.PersistentItemId:D}) couldn't come out: the ledger refused, {refused}");
                return null;
            }
        }
        ItemSaveLoad.RecordAuthoredTaken(row);
        if (ItemSaveLoad.MakeLive(row, owner != Guid.Empty, out LiveItems.LiveItem made) &&
            NetworkedItem.TryGet(made.NetId, out NetworkedItem item) && item != null)
            return item;
        Multiplayer.LogWarning($"Cold container: {stored.PrefabName} ({stored.PersistentItemId:D}) couldn't be made");
        MoveBack(stored, "it couldn't be made");
        return null;
    }

    private static void MoveBack(StoredItemRecord stored, string why)
    {
        if (!string.IsNullOrEmpty(graph.OwnerOf(stored.PersistentItemId)))
            ServerLedger.CommitStored(stored.PersistentItemId, stored.PrefabName, Home.World,
                Home.Container(stored.ParentContainerId, stored.Slot),
                ItemChangeLog.Move(ItemMoveReason.HostCorrection, detail: "taken back into its box: " + why));
    }

    /// <summary>
    /// Host: an item went away (<see cref="ItemChangeLog.RecordRemoval"/>). A box's shell destroyed
    /// for good takes its container with it (OI-CON-11, docs/design/items/containers.md, "A box
    /// destroyed"): an owned item in it goes to its owner's Lost and Found, whole if it's a box, and
    /// an unowned one goes with the box, its own contents dealt with the same way. A shell put in a
    /// box, collected, gone cold or remade keeps its container.
    /// </summary>
    internal static void OnRemoved(NetworkedItem shell, ChangeCause<ItemRemovalReason> cause)
    {
        if (!ServerLedger.Loaded || shell == null || !IsShell(shell))
            return;
        // A tool's ammo goes with it, or out of it if it's gone for good (MagazineServer).
        MagazineServer.OnToolRemoved(shell, LiveItems.TryGet(shell.NetId, out LiveItems.LiveItem tool) ? tool.PersistentItemId : Guid.Empty, cause);
        switch (cause.Reason)
        {
            case ItemRemovalReason.DepositedInContainer:
            case ItemRemovalReason.CollectedToLostAndFound:
            case ItemRemovalReason.LeftInterest:
            case ItemRemovalReason.WentCold:
            case ItemRemovalReason.RemadeFresh:
            case ItemRemovalReason.AuthoredSlotReplaced:
                return;
        }
        // Unloading the world or quitting destroys every item without ending any of them.
        if (UnloadWatcher.isUnloading || UnloadWatcher.isQuitting || !shell.gameObject.scene.isLoaded)
            return;
        Guid id = shellByNetId.TryGetValue(shell.NetId, out Guid bound) ? bound
            : LiveItems.TryGet(shell.NetId, out LiveItems.LiveItem record) ? record.PersistentItemId : Guid.Empty;
        shellByNetId.Remove(shell.NetId);
        if (id == Guid.Empty || !graph.IsContainer(id))
            return;
        Vector3 at = shell.transform.position - WorldMover.currentMove;
        int lost = 0, gone = 0;
        Clear(id, at, cause, ref lost, ref gone);
        Multiplayer.Log($"Cold container: {shell.name} ({id:D}) was destroyed ({cause.Reason}): " +
                        $"{lost} of its items went to their owners' Lost and Found, {gone} unowned went with it");
    }

    private static void Clear(Guid containerId, Vector3 at, ChangeCause<ItemRemovalReason> cause, ref int lost, ref int gone)
    {
        foreach (StoredItemRecord row in graph.RemoveContainer(containerId))
        {
            if (string.IsNullOrEmpty(graph.OwnerOf(row.PersistentItemId)))
            {
                gone++;
                if (graph.IsContainer(row.PersistentItemId))
                    Clear(row.PersistentItemId, at, cause, ref lost, ref gone);
                continue;
            }
            // Owned: out where the box was, then on to its owner's Lost and Found, whole.
            lost++;
            NetworkedItem item = MakeLoose(row, at, ItemChangeLog.Move(ItemMoveReason.CollectedToLostAndFound,
                detail: "its box was destroyed: " + cause.Reason));
            if (item == null)
            {
                // Its box is gone, so it can't go back: its entry ends with it.
                ServerLedger.Ledger.End(row.PersistentItemId, cause.Reason, detail: "its box was destroyed and it couldn't be made");
                continue;
            }
            if (!LostAndFoundServer.Collect(item, LostItemReason.ContainerCleared, "its box was destroyed"))
                Multiplayer.LogWarning($"Cold container: {row.PrefabName} ({row.PersistentItemId:D}) came out of a destroyed box " +
                                       "and Lost and Found refused it; it is where the box was");
        }
    }

    internal static void PublishMaterializedItem(ItemUpdate snapshot, NetworkedItem item)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (NetworkServer.Instance == null || snapshot == null || item == null)
            return;
        snapshot.UpdateType = ItemUpdate.ItemUpdateType.Create;
        snapshot.PrefabName = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name;
        foreach (ServerPlayer recipient in NetworkServer.Instance.ServerPlayers)
        {
            if (recipient.Peer == NetworkServer.Instance.SelfPeer ||
                recipient.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;
            NetworkServer.Instance.SendItemUpdates(new List<ItemUpdate> { snapshot },
                recipient);
            recipient.KnownItems[item] = lifecycle.Tick;
        }
    }

    public static ContainerOperationResult Move(ServerPlayer actor, Guid operationId,
        uint sourceHandle, uint expectedSourceRevision, int sourceSlot,
        uint destinationHandle, uint expectedDestinationRevision, int destinationSlot)
    {
        if (!ServerLedger.Loaded)
            return Failure(ContainerOperationKind.Move, ContainerOperationStatus.NotFound, Loading, operationId);
        if (actor == null || !graph.TryGetContainer(sourceHandle, out ContainerRecord source) ||
            !graph.TryGetContainer(destinationHandle, out ContainerRecord destination))
            return Failure(ContainerOperationKind.Move, ContainerOperationStatus.NotFound,
                "container-handle-not-found", operationId);
        MoveCommand command = new()
        {
            OperationId = operationId,
            ActorIdentity = actor.Guid.ToString("D"),
            SourceContainerId = source.PersistentContainerId,
            ExpectedSourceRevision = expectedSourceRevision,
            SourceSlot = sourceSlot,
            DestinationContainerId = destination.PersistentContainerId,
            ExpectedDestinationRevision = expectedDestinationRevision,
            DestinationSlot = destinationSlot
        };
        ContainerOperationResult check = graph.CheckMove(command);
        if (!check.Accepted)
            return check;
        // Cold records have no netId; the line says which containers and slots instead.
        ChangeCause<ItemMoveReason> cause = ItemChangeLog.Move(ItemMoveReason.Moved, actor,
            $"{source.PrefabName} slot {sourceSlot} -> {destination.PrefabName} slot {destinationSlot}");
        // The ledger first, and back if the graph refuses after all.
        Home from = Home.Container(source.PersistentContainerId, sourceSlot);
        Home to = Home.Container(destination.PersistentContainerId, destinationSlot);
        graph.TryGetItem(check.ItemId, out StoredItemRecord item);
        string prefab = item?.PrefabName ?? string.Empty;
        string refused = ServerLedger.CommitStored(check.ItemId, prefab, from, to, cause);
        if (refused.Length > 0)
            return Failure(ContainerOperationKind.Move, ContainerOperationStatus.InvalidState,
                "move-ledger-refused:" + refused, operationId);
        ContainerOperationResult moved = graph.Move(command);
        if (moved.Accepted)
            Multiplayer.Log("Cold item moved: " + cause);
        else
            ServerLedger.CommitStored(check.ItemId, prefab, to, from,
                ItemChangeLog.Move(ItemMoveReason.HostCorrection, actor, "move rolled back: " + moved.Reason));
        return moved;
    }

    public static Dictionary<string, object>[] DebugSnapshot()
    {
        ContainerGraphSnapshot snapshot = graph.ExportSnapshot();
        return snapshot.Containers.Select(container => new Dictionary<string, object>
        {
            ["debugKey"] = $"container:{container.SessionHandle}",
            ["persistentContainerId"] = container.PersistentContainerId.ToString("D"),
            ["keeper"] = graph.KeeperOf(container.PersistentContainerId),
            ["capacity"] = container.Capacity,
            ["revision"] = container.Revision,
            ["sessionHandle"] = container.SessionHandle,
            ["stored"] = snapshot.Items.Any(item => item.PersistentItemId == container.PersistentContainerId),
            ["coldItemCount"] = snapshot.Items.Count(item =>
                item.ParentContainerId == container.PersistentContainerId)
        }).ToArray();
    }

    private static void BindPhysicalShells()
    {
        HashSet<ushort> seen = new();
        foreach (NetworkedItem shell in NetworkedItem.GetAll().Where(item =>
                     item != null && item.NetId != 0 && IsShell(item))
                 .Distinct().ToArray())
        {
            seen.Add(shell.NetId);
            // A tool whose container has rows (its ammo while it was a row) loads them, kept or not.
            if (TryBindShell(shell, out Guid id) ||
                LiveItems.TryGet(shell.NetId, out LiveItems.LiveItem record) && (id = record.PersistentItemId) != Guid.Empty)
                MagazineServer.LoadFromRows(shell, id);
        }
        foreach (ushort stale in shellByNetId.Keys.Where(id => !seen.Contains(id)).ToArray())
            shellByNetId.Remove(stale);
    }

    // A container's id is its shell's item id (OI-CON-09): the id the item has from its birth or
    // its saved row, before its state loads, so a restored shell can't mint another.
    private static bool TryBindShell(NetworkedItem shell, out Guid id)
    {
        id = Guid.Empty;
        if (!LiveItems.TryGet(shell.NetId, out LiveItems.LiveItem record) || record.PersistentItemId == Guid.Empty)
            return false;
        // Bound once it has a keeper (an owner, or a holder: OI-CON-08), so a box nobody has
        // touched lying in the world adds no row to the save.
        if (!HasKeeper(shell)) return false;
        EnsureShellRecord(shell, record.PersistentItemId);
        shellByNetId[shell.NetId] = id = record.PersistentItemId;
        return true;
    }

    /// <summary>The live shell (a container item in the world or with a player) of a cold container, if it has one.</summary>
    internal static bool TryGetShellNetId(Guid containerId, out ushort shellNetId)
    {
        shellNetId = shellByNetId.FirstOrDefault(pair => pair.Value == containerId).Key;
        if (shellNetId != 0) return true;
        LiveItems.LiveItem shell = containerId == Guid.Empty ? null : LiveItems.GetAll().FirstOrDefault(record =>
            record.PersistentItemId == containerId && NetworkedItem.TryGet(record.NetId, out NetworkedItem item) && item != null);
        shellNetId = shell?.NetId ?? PlayerItems.DataOnlyShell(containerId);
        return shellNetId != 0;
    }

    // Shells are bound by a once-a-second scan; one picked up and opened straight away (a new
    // purchase) wasn't bound yet and the request failed. Bind it on demand.
    private static bool TryGetOrBindShell(ushort shellNetId, out Guid id)
    {
        if (shellByNetId.TryGetValue(shellNetId, out id)) return true;
        return NetworkedItem.TryGet(shellNetId, out NetworkedItem shell) && shell != null &&
               IsShell(shell) && TryBindShell(shell, out id);
    }

    // An item vanilla gives a container of its own (a toolbox; a tool's magazine too).
    private static bool IsShell(NetworkedItem item) => item.GetComponent<AItemContainer>() != null;

    /// <summary>The shell's container in the graph, made from its id and capacity if it has none yet.</summary>
    internal static void EnsureContainer(NetworkedItem shell, Guid id) => EnsureShellRecord(shell, id);

    private static void EnsureShellRecord(NetworkedItem shell, Guid id)
    {
        if (graph.TryGetContainer(id, out _)) return;
        AItemContainer container = shell?.GetComponent<AItemContainer>();
        if (container == null) return;
        ContainerOperationResult result = graph.AddContainer(new ContainerRecord
        {
            PersistentContainerId = id,
            PrefabName = shell.Item?.InventorySpecs?.ItemPrefabName ?? shell.name,
            Capacity = container.Capacity
        });
        if (!result.Accepted)
            Multiplayer.LogWarning($"Unable to bind cold container {id:D}: {result.Reason}");
    }

    private static bool HasKeeper(NetworkedItem shell) =>
        LiveItems.TryGet(shell.NetId, out LiveItems.LiveItem authority) &&
        (authority.Owner != Guid.Empty || authority.Holder != Guid.Empty);

    // Its owner opens a shell; so does whoever holds one nobody owns (OI-CON-08).
    private static bool ActorOwnsPhysicalShell(ServerPlayer actor, ushort shellNetId) =>
        actor != null && shellNetId != 0 &&
        LiveItems.TryGet(shellNetId,
            out LiveItems.LiveItem authority) &&
        (authority.Owner == actor.Guid ||
         authority.Owner == Guid.Empty && authority.Holder == actor.Guid);

    private static InventoryItemSpec FindItemSpec(string prefabName) =>
        Globals.G?.Items?.items?.FirstOrDefault(spec => spec != null &&
            string.Equals(spec.ItemPrefabName, prefabName, StringComparison.Ordinal));

    private static ContainerOperationResult Failure(ContainerOperationKind kind,
        ContainerOperationStatus status, string reason, Guid operationId = default) => new()
    {
        OperationId = operationId,
        Kind = kind,
        Status = status,
        Reason = reason
    };

    private static void Publish(string eventName, ushort shellNetId,
        ContainerOperationResult result, Dictionary<string, object> data,
        DebugSeverity severity = DebugSeverity.Info)
    {
        if (!DebugRuntime.EnabledFor("inventory")) return;
        data["operationId"] = result.OperationId.ToString("D");
        data["status"] = result.Status.ToString();
        data["sourceRevision"] = result.SourceRevision;
        data["destinationRevision"] = result.DestinationRevision;
        DebugRuntime.Publish("inventory", eventName, DebugRuntimeSide.Server, severity,
            "Container", shellNetId.ToString(),
            correlationId: result.OperationId == Guid.Empty ? string.Empty :
                result.OperationId.ToString("D"), data: data);
    }

    /// <summary>Uses the running game's prefabs as the compatibility policy for cold moves.</summary>
    private sealed class HostRuntimeCompatibility : IContainerCompatibilityPolicy
    {
        private readonly Dictionary<string, bool> cache = new(StringComparer.Ordinal);

        public bool IsCompatible(StoredItemRecord item, ContainerRecord destination)
        {
            if (item == null || destination == null || string.IsNullOrWhiteSpace(item.PrefabName) ||
                string.IsNullOrWhiteSpace(destination.PrefabName))
                return false;
            string key = destination.PrefabName + "\n" + item.PrefabName;
            if (cache.TryGetValue(key, out bool accepted)) return accepted;
            GameObject containerPrefab = FindItemSpec(destination.PrefabName)?.gameObject;
            GameObject itemPrefab = FindItemSpec(item.PrefabName)?.gameObject;
            AItemContainer runtime = containerPrefab?.GetComponent<AItemContainer>();
            accepted = runtime != null && itemPrefab != null && runtime.ValidItem(itemPrefab);
            cache[key] = accepted;
            return accepted;
        }

        public void Remember(string destinationPrefabName, string itemPrefabName, bool accepted)
        {
            if (string.IsNullOrWhiteSpace(destinationPrefabName) ||
                string.IsNullOrWhiteSpace(itemPrefabName))
                return;
            cache[destinationPrefabName + "\n" + itemPrefabName] = accepted;
        }

        public void Clear() => cache.Clear();
    }

    /// <summary>The graph's owners are the ledger's, and a live shell's holder its live record's.</summary>
    private sealed class LedgerOwners : IContainerOwners
    {
        public string OwnerOf(Guid itemId) =>
            ServerLedger.Ledger.TryGet(itemId, out LedgerEntry entry) && entry.IsLive && entry.Owner.PlayerId != Guid.Empty
                ? entry.Owner.PlayerId.ToString("D") : string.Empty;

        public string HolderOf(Guid containerId) =>
            TryGetShellNetId(containerId, out ushort netId) && LiveItems.TryGet(netId, out LiveItems.LiveItem shell) &&
            shell.Holder != Guid.Empty && shell.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory
                ? shell.Holder.ToString("D") : string.Empty;
    }
}
