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
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.Access;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
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
    internal const string SaveKey = "ColdContainers";
    private const uint ScanIntervalTicks = 24;
    private static uint lastScanTick; // the tick can advance by more than one a frame
    private static readonly HostRuntimeCompatibility compatibility = new();
    private static ContainerGraph graph = new(compatibility: compatibility);
    private static readonly Dictionary<ushort, Guid> shellByNetId = new();
    private static bool saveImportAttempted;

    /// <summary>The save's records have been read (the owned-items ledger is checked against them after).</summary>
    internal static bool ImportAttempted => saveImportAttempted;

    public static ContainerGraph Graph => graph;

    public static void HostTick(uint tick)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true || unchecked(tick - lastScanTick) < ScanIntervalTicks) return;
        lastScanTick = tick;
        TryImportSave();
        BindPhysicalShells();
    }

    public static void Clear()
    {
        compatibility.Clear();
        graph = new ContainerGraph(compatibility: compatibility);
        shellByNetId.Clear();
        saveImportAttempted = false;
    }

    public static void WriteSave(JObject multiplayerRoot)
    {
        if (multiplayerRoot == null) return;
        TryImportSave();
        if (!saveImportAttempted) return;
        multiplayerRoot[SaveKey] = Convert.ToBase64String(
            ContainerSnapshotCodec.Encode(graph.ExportSnapshot()));
    }

    public static bool TryResolveShell(ushort shellNetId, out Guid containerId) =>
        TryGetOrBindShell(shellNetId, out containerId);

    public static ContainerOperationResult Browse(ServerPlayer actor, ushort shellNetId,
        int offset, int count, out ContainerView view)
    {
        view = null;
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

        Guid? childId = item.GetComponent<ContainerIdentity>()?.PersistentId;
        if (childId.HasValue)
            EnsureShellRecord(item, childId.Value,
                ResolveShellOwner(item) ?? actor.Guid.ToString("D"));
        JObject detached = item.GetComponent<ItemSaveData>()?.SaveItemData();
        StoredItemRecord record = new()
        {
            // The item's own id, not one of the record's: one id for life (owned-items.md).
            PersistentItemId = authority.PersistentItemId != Guid.Empty ? authority.PersistentItemId : Guid.NewGuid(),
            PrefabName = item.Item.InventorySpecs?.ItemPrefabName ?? item.name,
            DisplayName = item.Item.InventorySpecs?.LocalizedName ?? item.name,
            PersistentOwnerIdentity = (authority.Owner != Guid.Empty ? authority.Owner : actor.Guid).ToString("D"),
            AuthoredItemKey = item.AuthoredItemKey ?? string.Empty,
            ChildContainerId = childId,
            StateVersion = 1,
            DetachedState = detached == null ? Array.Empty<byte>() :
                Encoding.UTF8.GetBytes(detached.ToString(Formatting.None))
        };
        // The live container and item are the strongest compatibility oracle. Prefab registry
        // objects are not fully initialized, so calling ValidItem on them can incorrectly reject
        // combinations that the running game has already accepted.
        if (destinationIsOpenedShell)
            compatibility.Remember(destination.PrefabName, record.PrefabName, true);
        ContainerOperationResult result = graph.CommitDeposit(new DepositCommand
        {
            OperationId = operationId,
            ActorIdentity = actor.Guid.ToString("D"),
            DestinationContainerId = destinationId,
            ExpectedDestinationRevision = expectedRevision,
            DestinationSlot = slot,
            PhysicalNetId = itemNetId,
            Item = record
        });
        if (!result.Accepted) return result;

        try
        {
            ItemUpdate destroy = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Destroy);
            if (destroy == null)
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
            // the representation and its interest membership have already been removed.
            foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
            {
                player.NearbyItems.Remove(item);
                player.KnownItems.Remove(item);
                player.AcknowledgedWorldItems.Remove(item.NetId);
            }
            NetworkServer.Instance.SendItemUpdate(destroy);
            item.Item.ForceEndInteraction();
            InventoryIntegration.RevokeMembership(item.gameObject, item.NetId);
            InventoryIntegration.PurgeContainerMembership(item.gameObject);
            StorageIntegration.MoveTo(item.Item, StorageMembership.None);
            UnityEngine.Object.Destroy(item.gameObject);
            graph.FinalizeDepositRetirement(operationId, record.PersistentItemId);
            ServerLedger.OnDeposited(record.PersistentItemId, destinationId);
            Publish("container.deposit-committed", shellNetId, result, new()
            {
                ["itemNetId"] = itemNetId,
                ["slot"] = slot,
                ["persistentItemId"] = record.PersistentItemId.ToString("D")
            });
        }
        catch (Exception exception)
        {
            item.Item.InteractionAllowed = false;
            item.gameObject.SetActive(false);
            Multiplayer.LogException("Cold-container representation retirement failed", exception);
            Publish("container.deposit-retirement-pending", shellNetId, result, new()
            {
                ["itemNetId"] = itemNetId,
                ["error"] = exception.Message
            }, DebugSeverity.Error);
        }
        return result;
    }

    public static IEnumerator Withdraw(ServerPlayer actor, Guid operationId,
        uint containerHandle, uint expectedRevision, int sourceSlot, int destinationInventorySlot,
        Action<ContainerOperationResult, ushort> completed)
    {
        if (destinationInventorySlot < 0)
        {
            completed?.Invoke(Failure(ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidRecord, "withdraw-destination-slot-missing",
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
        // Somebody else's item comes out only while they're connected; one with no owner
        // recorded is the withdrawer's.
        if (!Guid.TryParse(record.PersistentOwnerIdentity, out Guid owner) || owner == Guid.Empty)
            owner = actor.Guid;
        if (owner != actor.Guid && !PlayerNumbers.IsConnected(owner))
        {
            graph.AbortWithdrawal(operationId, "withdraw-persistent-owner-offline");
            completed?.Invoke(Failure(ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidState, "withdraw-persistent-owner-offline",
                operationId), 0);
            yield break;
        }

        GameObject instance = null;
        NetworkedItem item = null;
        try
        {
            GameObject prefab = FindItemSpec(record.PrefabName)?.gameObject;
            if (prefab == null) throw new InvalidOperationException("withdraw-prefab-not-found");
            instance = UnityEngine.Object.Instantiate(prefab,
                StartingItemsController.ITEM_INSTANTIATION_SAFETY_POSITION, Quaternion.identity);
            instance.name = record.PrefabName;
            InventoryItemSpec spec = instance.GetComponent<InventoryItemSpec>();
            if (spec != null)
                spec.BelongsToPlayer = string.Equals(record.PersistentOwnerIdentity,
                    actor.Guid.ToString("D"), StringComparison.Ordinal);
            item = instance.GetOrAddComponent<NetworkedItem>();
            if (WorldItemStableIdentity.IsValid(record.AuthoredItemKey))
                item.AdoptAuthoredItemKey(record.AuthoredItemKey);
            item?.BeginColdMaterialization();
        }
        catch (Exception exception)
        {
            item?.AbortColdMaterialization();
            if (instance != null) UnityEngine.Object.Destroy(instance);
            graph.AbortWithdrawal(operationId, exception.Message);
            Multiplayer.LogException("Cold-container withdrawal preparation failed", exception);
            completed?.Invoke(Failure(ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidState, exception.Message, operationId), 0);
            yield break;
        }

        // Derail Valley's own StartingItemsController waits one frame after instantiation
        // before invoking ItemSaveData.LoadItemData. Components such as Lantern populate
        // dependencies in Awake/Start and throw when state is loaded in the creation frame.
        yield return null;

        if (actor.PlayerId == 0)
        {
            // The withdrawer left while the item woke (owned-items.md, 2a): it stays in the box,
            // rather than becoming a held item with nobody holding it.
            item?.AbortColdMaterialization();
            UnityEngine.Object.Destroy(instance);
            graph.AbortWithdrawal(operationId, "withdraw-actor-left");
            completed?.Invoke(Failure(ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidState, "withdraw-actor-left", operationId), 0);
            yield break;
        }

        try
        {
            ItemSaveData saveData = instance.GetComponent<ItemSaveData>();
            if (saveData != null && record.DetachedState?.Length > 0)
            {
                JObject state = JObject.Parse(Encoding.UTF8.GetString(record.DetachedState));
                saveData.LoadItemData(state);
                saveData.PostLoadItemData();
            }
            ItemBase itemBase = instance.GetComponent<ItemBase>();
            if (item == null || itemBase == null || item.NetId == 0)
                throw new InvalidOperationException("withdraw-authoritative-registration-failed");
            item.Initialize(itemBase, item.NetId, true);
            item.FinaliseTrackedValuesAutomatically();
            ContainerOperationResult materialized = graph.MarkWithdrawalMaterialized(operationId);
            if (!materialized.Accepted)
                throw new InvalidOperationException("withdraw-materialization-phase-rejected:" +
                    materialized.Reason);

            ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Create);
            snapshot.ItemState = ItemState.InInventory;
            snapshot.PlayerId = actor.PlayerId;
            snapshot.InventoryClaimSlot = destinationInventorySlot;
            snapshot.InventoryClaimFlags = ItemInventoryClaimFlags.None;
            // Its stored state brought its id back (ItemIdentityPatch.Load); a record
            // from before the item's id was the record's key is the fallback.
            ItemIdentity identity = instance.GetComponent<ItemIdentity>() ??
                                                   instance.AddComponent<ItemIdentity>();
            if (identity.Value == Guid.Empty)
                identity.Value = ServerLedger.ItemIdOf(record);
            if (LiveItems.RegisterMaterializedColdItem(item, actor,
                    owner, snapshot, identity.Value) == null)
                throw new InvalidOperationException("withdraw-authoritative-registration-failed");
            item.ApplyServerCanonicalSnapshot(snapshot);
            // The item was indexed by its spawn position when it woke, before it had a record;
            // re-index it as held. Left in that cell, it was never sent to the holder again
            // (after a rejoin, the withdrawn item was missing from their inventory).
            ServerItemManager.Instance?.RegisterHostWorldItem(item);
            if (actor.PlayerId == InventoryIntegration.LocalPlayerId &&
                !InventoryIntegration.ContainsActive(instance))
                throw new InvalidOperationException("withdraw-local-inventory-projection-failed");
            ContainerOperationResult committed = graph.CommitWithdrawal(operationId);
            if (!committed.Accepted)
                throw new InvalidOperationException("withdraw-logical-commit-failed:" + committed.Reason);
            item.CompleteColdMaterialization();
            PublishMaterializedItem(snapshot, item);
            Publish("container.withdrawal-committed", 0, committed, new()
            {
                ["itemNetId"] = item.NetId,
                ["sourceSlot"] = sourceSlot,
                ["destinationInventorySlot"] = destinationInventorySlot,
                ["containerHandle"] = containerHandle
            });
            completed?.Invoke(committed, item.NetId);
        }
        catch (Exception exception)
        {
            item?.AbortColdMaterialization();
            if (instance != null) UnityEngine.Object.Destroy(instance);
            graph.AbortWithdrawal(operationId, exception.Message);
            Multiplayer.LogException("Cold-container withdrawal failed", exception);
            completed?.Invoke(Failure(ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidState, exception.Message, operationId), 0);
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
        if (actor == null || !graph.TryGetContainer(sourceHandle, out ContainerRecord source) ||
            !graph.TryGetContainer(destinationHandle, out ContainerRecord destination))
            return Failure(ContainerOperationKind.Move, ContainerOperationStatus.NotFound,
                "container-handle-not-found", operationId);
        ContainerOperationResult moved = graph.Move(new MoveCommand
        {
            OperationId = operationId,
            ActorIdentity = actor.Guid.ToString("D"),
            SourceContainerId = source.PersistentContainerId,
            ExpectedSourceRevision = expectedSourceRevision,
            SourceSlot = sourceSlot,
            DestinationContainerId = destination.PersistentContainerId,
            ExpectedDestinationRevision = expectedDestinationRevision,
            DestinationSlot = destinationSlot
        });
        // Cold records have no netId; the line says which containers and slots instead.
        if (moved.Accepted)
            Multiplayer.Log("Cold item moved: " + ItemChangeLog.Move(ItemMoveReason.Moved, actor,
                $"{source.PrefabName} slot {sourceSlot} -> {destination.PrefabName} slot {destinationSlot}"));
        return moved;
    }

    public static Dictionary<string, object>[] DebugSnapshot()
    {
        ContainerGraphSnapshot snapshot = graph.ExportSnapshot();
        return snapshot.Containers.Select(container => new Dictionary<string, object>
        {
            ["debugKey"] = $"container:{container.SessionHandle}",
            ["persistentContainerId"] = container.PersistentContainerId.ToString("D"),
            ["ownerIdentity"] = container.OwnerIdentity,
            ["capacity"] = container.Capacity,
            ["revision"] = container.Revision,
            ["sessionHandle"] = container.SessionHandle,
            ["parentItemId"] = container.ParentItemId?.ToString("D") ?? string.Empty,
            ["coldItemCount"] = snapshot.Items.Count(item =>
                item.ParentContainerId == container.PersistentContainerId)
        }).ToArray();
    }

    private static void BindPhysicalShells()
    {
        HashSet<ushort> seen = new();
        foreach (NetworkedItem shell in NetworkedItem.GetAll().Where(item =>
                     item != null && item.NetId != 0 && item.GetComponent<AItemContainer>() != null)
                 .Distinct().ToArray())
        {
            seen.Add(shell.NetId);
            TryBindShell(shell, out _);
        }
        foreach (ushort stale in shellByNetId.Keys.Where(id => !seen.Contains(id)).ToArray())
            shellByNetId.Remove(stale);
    }

    private static bool TryBindShell(NetworkedItem shell, out Guid id)
    {
        id = Guid.Empty;
        ContainerIdentity identity = shell.gameObject.GetOrAddComponent<ContainerIdentity>();
        string owner = ResolveShellOwner(shell);
        if (string.IsNullOrEmpty(owner)) return false;
        EnsureShellRecord(shell, identity.PersistentId, owner);
        shellByNetId[shell.NetId] = id = identity.PersistentId;
        return true;
    }

    /// <summary>The live shell (a container item in the world or with a player) of a cold container, if it has one.</summary>
    internal static bool TryGetShellNetId(Guid containerId, out ushort shellNetId)
    {
        shellNetId = shellByNetId.FirstOrDefault(pair => pair.Value == containerId).Key;
        if (shellNetId != 0) return true;
        NetworkedItem shell = NetworkedItem.GetAll().FirstOrDefault(item => item != null &&
            item.GetComponent<ContainerIdentity>() is ContainerIdentity identity && identity.PersistentId == containerId);
        shellNetId = shell?.NetId ?? 0;
        return shellNetId != 0;
    }

    // Shells are bound by a once-a-second scan; one picked up and opened straight away (a new
    // purchase) wasn't bound yet and the request failed. Bind it on demand.
    private static bool TryGetOrBindShell(ushort shellNetId, out Guid id)
    {
        if (shellByNetId.TryGetValue(shellNetId, out id)) return true;
        return NetworkedItem.TryGet(shellNetId, out NetworkedItem shell) && shell != null &&
               shell.GetComponent<AItemContainer>() != null && TryBindShell(shell, out id);
    }

    private static void EnsureShellRecord(NetworkedItem shell, Guid id, string owner)
    {
        if (graph.TryGetContainer(id, out _)) return;
        AItemContainer container = shell?.GetComponent<AItemContainer>();
        if (container == null) return;
        ContainerOperationResult result = graph.AddContainer(new ContainerRecord
        {
            PersistentContainerId = id,
            OwnerIdentity = owner,
            PrefabName = shell.Item?.InventorySpecs?.ItemPrefabName ?? shell.name,
            Capacity = container.Capacity
        });
        if (!result.Accepted)
            Multiplayer.LogWarning($"Unable to bind cold container {id:D}: {result.Reason}");
    }

    private static string ResolveShellOwner(NetworkedItem shell)
    {
        if (!LiveItems.TryGet(shell.NetId, out LiveItems.LiveItem authority))
            return null;
        Guid owner = authority.Owner != Guid.Empty ? authority.Owner : authority.Holder;
        return owner != Guid.Empty ? owner.ToString("D") : null;
    }

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

    private static void TryImportSave()
    {
        if (saveImportAttempted || StartingItemsController.Instance == null ||
            !StartingItemsController.Instance.itemsLoaded || SaveGameManager.Instance?.data == null)
            return;
        saveImportAttempted = true;
        string encoded = (string)SaveGameManager.Instance.data.GetJObject("Multiplayer")?[SaveKey];
        if (string.IsNullOrWhiteSpace(encoded)) return;
        try
        {
            if (!ContainerSnapshotCodec.TryDecode(Convert.FromBase64String(encoded),
                    out ContainerGraphSnapshot snapshot, out string reason))
            {
                Multiplayer.LogWarning($"Cold-container save rejected: {reason}");
                return;
            }
            ContainerOperationResult imported = graph.ImportSnapshot(snapshot);
            if (!imported.Accepted)
                Multiplayer.LogWarning($"Cold-container graph rejected: {imported.Reason}");
        }
        catch (FormatException exception)
        {
            Multiplayer.LogException("Cold-container save encoding is invalid", exception);
        }
    }

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
}
