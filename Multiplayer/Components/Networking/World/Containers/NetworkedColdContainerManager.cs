using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DV.CabControls;
using DV;
using DV.InventorySystem;
using DV.JObjectExtstensions;
using Multiplayer.Core.Containers;
using Multiplayer.Core.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Integrations.Inventory;
using Multiplayer.Integrations.Storage;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.Containers;

/// <summary>Host authority for detached container contents; shells remain ordinary network items.</summary>
public static class NetworkedColdContainerManager
{
    private const string SaveKey = "ColdContainers";
    private const uint ScanIntervalTicks = 24;
    private static readonly HostRuntimeCompatibility compatibility = new();
    private static ColdContainerGraph graph = new(compatibility: compatibility);
    private static readonly Dictionary<ushort, Guid> shellByNetId = new();
    private static bool saveImportAttempted;

    public static ColdContainerGraph Graph => graph;

    public static void HostTick(uint tick)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true || tick % ScanIntervalTicks != 0) return;
        TryImportSave();
        BindPhysicalShells();
    }

    public static void Clear()
    {
        ColdContainerUiIntegration.Reset();
        compatibility.Clear();
        graph = new ColdContainerGraph(compatibility: compatibility);
        shellByNetId.Clear();
        saveImportAttempted = false;
    }

    public static void WriteSave(JObject multiplayerRoot)
    {
        if (multiplayerRoot == null) return;
        TryImportSave();
        if (!saveImportAttempted) return;
        multiplayerRoot[SaveKey] = Convert.ToBase64String(
            ColdContainerSnapshotCodec.Encode(graph.ExportSnapshot()));
    }

    public static bool TryResolveShell(ushort shellNetId, out Guid containerId) =>
        shellByNetId.TryGetValue(shellNetId, out containerId);

    public static ContainerOperationResult Browse(ServerPlayer actor, ushort shellNetId,
        int offset, int count, out ColdContainerView view)
    {
        view = null;
        if (actor == null || !shellByNetId.TryGetValue(shellNetId, out Guid id))
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
        int offset, int count, out ColdContainerView view)
    {
        view = null;
        if (actor == null || !graph.TryGetContainer(containerHandle, out ColdContainerRecord container))
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
        ColdContainerRecord destination = null;
        bool destinationResolved = destinationHandle != 0
            ? graph.TryGetContainer(destinationHandle, out destination) &&
              (destinationId = destination.PersistentContainerId) != Guid.Empty
            : shellByNetId.TryGetValue(shellNetId, out destinationId);
        if (destinationResolved && destination == null)
            destinationResolved = graph.TryGetContainer(destinationId, out destination);
        if (actor == null || !destinationResolved ||
            !NetworkedItem.TryGet(itemNetId, out NetworkedItem item) || item?.Item == null)
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.NotFound,
                "deposit-runtime-object-not-found", operationId);
        if (shellNetId != 0 && !ActorOwnsPhysicalShell(actor, shellNetId))
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.Unauthorized,
                "container-private", operationId);
        bool possessed = AuthoritativeItemRegistry.TryGet(itemNetId,
                out AuthoritativeItemRegistry.Record authority) &&
            authority.PlacementPlayerId == actor.PlayerId && authority.Placement is
                (ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory);
        if (!possessed && actor.PlayerId == InventoryIntegration.LocalPlayerId &&
            InventoryIntegration.ContainsActive(item.gameObject))
        {
            ItemUpdateData reconciliation = item.CreateUpdateData(
                ItemUpdateData.ItemUpdateType.ItemState);
            if (reconciliation != null)
            {
                reconciliation.ItemState = ItemState.InInventory;
                reconciliation.PlayerId = actor.PlayerId;
                reconciliation.AuthorityRevision = authority?.Revision ?? 0;
                possessed = AuthoritativeItemRegistry.TryApplyTransition(item, reconciliation,
                    actor, ItemTransitionReason.HostLocalState, true, out _);
                if (possessed)
                {
                    item.ApplyAuthorityMetadata(reconciliation);
                    AuthoritativeItemRegistry.TryGet(itemNetId, out authority);
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
        if (authority.PersistentOwnerPlayerId != actor.PlayerId)
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.Unauthorized,
                "deposit-item-not-owned", operationId);
        NetworkedItem.TryGet(shellNetId, out NetworkedItem shell);
        AItemContainer runtimeContainer = shell?.GetComponent<AItemContainer>();
        // The opened root shell is the game's compatibility oracle. Nested shells may be
        // detached, but graph validation still enforces their capacity/cycle/depth rules.
        bool destinationIsOpenedShell = shellNetId != 0 &&
            shellByNetId.TryGetValue(shellNetId, out Guid shellContainerId) &&
            shellContainerId == destinationId;
        if (destinationIsOpenedShell &&
            (runtimeContainer == null || !runtimeContainer.ValidItem(item.gameObject)))
            return Failure(ContainerOperationKind.Deposit, ContainerOperationStatus.Incompatible,
                "game-container-rejected-item", operationId);

        Guid? childId = item.GetComponent<ColdContainerIdentity>()?.PersistentId;
        if (childId.HasValue)
            EnsureShellRecord(item, childId.Value,
                ResolveShellOwner(item) ?? actor.Guid.ToString("D"));
        JObject detached = item.GetComponent<ItemSaveData>()?.SaveItemData();
        ColdStoredItemRecord record = new()
        {
            PersistentItemId = Guid.NewGuid(),
            PrefabName = item.Item.InventorySpecs?.ItemPrefabName ?? item.name,
            DisplayName = item.Item.InventorySpecs?.LocalizedName ?? item.name,
            PersistentOwnerIdentity = ResolveOwnerIdentity(authority.PersistentOwnerPlayerId) ??
                actor.Guid.ToString("D"),
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

#if DEBUG
        global::Multiplayer.Debugging.RuntimeTests.InventoryRuntimeFixtureDriver
            .TransferFixtureToCold(itemNetId, record.PersistentItemId);
#endif
        try
        {
            ItemUpdateData destroy = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
            if (destroy == null)
                throw new InvalidOperationException("deposit-destroy-projection-failed");
            AuthoritativeItemRegistry.WriteToSnapshot(authority, destroy,
                ItemTransitionReason.ContainerDeposit);
            item.BeginColdStorageRetirement();
            // Retirement is a transactional boundary, not an ordinary dirty-state update.
            // Publish it immediately on the reliable ordered channel before the Unity object
            // is torn down; waiting for the next item-manager tick can lose the Destroy after
            // the representation and its interest membership have already been removed.
            foreach (ServerPlayer player in NetworkLifecycle.Instance.Server.ServerPlayers)
            {
                player.NearbyItems.Remove(item);
                player.KnownItems.Remove(item);
            }
            NetworkLifecycle.Instance.Server.SendItemUpdatePacket(destroy);
            item.Item.ForceEndInteraction();
            InventoryIntegration.RevokeMembership(item.gameObject, item.NetId);
            InventoryIntegration.PurgeContainerMembership(item.gameObject);
            StorageIntegration.MoveTo(item.Item, StorageMembership.None);
            UnityEngine.Object.Destroy(item.gameObject);
            graph.FinalizeDepositRetirement(operationId, record.PersistentItemId);
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
        if (actor == null || !graph.TryGetContainer(containerHandle, out ColdContainerRecord container))
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
            out ColdStoredItemRecord record);
        if (!prepared.Accepted)
        {
            completed?.Invoke(prepared, 0);
            yield break;
        }
        // Two local debug instances intentionally share the same saved DV identity. The
        // authenticated actor is the unambiguous owner when its identity matches the cold
        // record; only use the server-wide GUID lookup for somebody else's record.
        byte persistentOwnerPlayerId = string.Equals(record.PersistentOwnerIdentity,
                actor.Guid.ToString("D"), StringComparison.Ordinal)
            ? actor.PlayerId
            : ResolvePlayerId(record.PersistentOwnerIdentity);
        if (persistentOwnerPlayerId == 0 &&
            !string.Equals(record.PersistentOwnerIdentity, actor.Guid.ToString("D"),
                StringComparison.Ordinal))
        {
            graph.AbortWithdrawal(operationId, "withdraw-persistent-owner-offline");
            completed?.Invoke(Failure(ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidState, "withdraw-persistent-owner-offline",
                operationId), 0);
            yield break;
        }
        if (persistentOwnerPlayerId == 0)
            persistentOwnerPlayerId = actor.PlayerId;

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

            ItemUpdateData snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create);
            snapshot.ItemState = ItemState.InInventory;
            snapshot.PlayerId = actor.PlayerId;
            snapshot.InventoryClaimPlayerId = actor.PlayerId;
            snapshot.InventoryClaimSlot = destinationInventorySlot;
            snapshot.InventoryClaimFlags = ItemInventoryClaimFlags.None;
            if (AuthoritativeItemRegistry.RegisterMaterializedColdItem(item, actor,
                    persistentOwnerPlayerId, snapshot) == null)
                throw new InvalidOperationException("withdraw-authoritative-registration-failed");
            item.ApplyServerCanonicalSnapshot(snapshot);
            if (actor.PlayerId == InventoryIntegration.LocalPlayerId &&
                !InventoryIntegration.ContainsActive(instance))
                throw new InvalidOperationException("withdraw-local-inventory-projection-failed");
            ContainerOperationResult committed = graph.CommitWithdrawal(operationId);
            if (!committed.Accepted)
                throw new InvalidOperationException("withdraw-logical-commit-failed:" + committed.Reason);
            item.CompleteColdMaterialization();
#if DEBUG
            global::Multiplayer.Debugging.RuntimeTests.InventoryRuntimeFixtureDriver
                .TransferFixtureFromCold(record.PersistentItemId, item.NetId);
#endif
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

    private static void PublishMaterializedItem(ItemUpdateData snapshot, NetworkedItem item)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle?.Server == null || snapshot == null || item == null)
            return;
        snapshot.UpdateType = ItemUpdateData.ItemUpdateType.Create;
        snapshot.PrefabName = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name;
        foreach (ServerPlayer recipient in lifecycle.Server.ServerPlayers)
        {
            if (recipient.Peer == lifecycle.Server.SelfPeer ||
                recipient.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;
            lifecycle.Server.SendItemsBulkUpdatePacket(new List<ItemUpdateData> { snapshot },
                recipient);
            recipient.KnownItems[item] = lifecycle.Tick;
        }
    }

    public static ContainerOperationResult Move(ServerPlayer actor, Guid operationId,
        uint sourceHandle, uint expectedSourceRevision, int sourceSlot,
        uint destinationHandle, uint expectedDestinationRevision, int destinationSlot)
    {
        if (actor == null || !graph.TryGetContainer(sourceHandle, out ColdContainerRecord source) ||
            !graph.TryGetContainer(destinationHandle, out ColdContainerRecord destination))
            return Failure(ContainerOperationKind.Move, ContainerOperationStatus.NotFound,
                "container-handle-not-found", operationId);
        return graph.Move(new MoveCommand
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
    }

    public static Dictionary<string, object>[] DebugSnapshot()
    {
        ColdContainerGraphSnapshot snapshot = graph.ExportSnapshot();
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
            ColdContainerIdentity identity = shell.gameObject.GetOrAddComponent<ColdContainerIdentity>();
            string owner = ResolveShellOwner(shell);
            if (string.IsNullOrEmpty(owner)) continue;
            EnsureShellRecord(shell, identity.PersistentId, owner);
            shellByNetId[shell.NetId] = identity.PersistentId;
        }
        foreach (ushort stale in shellByNetId.Keys.Where(id => !seen.Contains(id)).ToArray())
            shellByNetId.Remove(stale);
    }

    private static void EnsureShellRecord(NetworkedItem shell, Guid id, string owner)
    {
        if (graph.TryGetContainer(id, out _)) return;
        AItemContainer container = shell?.GetComponent<AItemContainer>();
        if (container == null) return;
        ContainerOperationResult result = graph.AddContainer(new ColdContainerRecord
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
        if (!AuthoritativeItemRegistry.TryGet(shell.NetId, out AuthoritativeItemRegistry.Record authority))
            return null;
        byte owner = authority.PersistentOwnerPlayerId != 0
            ? authority.PersistentOwnerPlayerId : authority.PlacementPlayerId;
        return ResolveOwnerIdentity(owner);
    }

    private static bool ActorOwnsPhysicalShell(ServerPlayer actor, ushort shellNetId) =>
        actor != null && shellNetId != 0 &&
        AuthoritativeItemRegistry.TryGet(shellNetId,
            out AuthoritativeItemRegistry.Record authority) &&
        authority.PersistentOwnerPlayerId == actor.PlayerId;

    private static string ResolveOwnerIdentity(byte playerId) =>
        playerId != 0 && NetworkLifecycle.Instance.Server.TryGetServerPlayer(playerId,
            out ServerPlayer player) ? player.Guid.ToString("D") : null;

    private static byte ResolvePlayerId(string identity)
    {
        if (!Guid.TryParse(identity, out Guid guid) || NetworkLifecycle.Instance?.Server == null)
            return 0;
        ServerPlayer player = NetworkLifecycle.Instance.Server.ServerPlayers
            .FirstOrDefault(candidate => candidate != null && candidate.Guid == guid);
        return player?.PlayerId ?? 0;
    }

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
            if (!ColdContainerSnapshotCodec.TryDecode(Convert.FromBase64String(encoded),
                    out ColdContainerGraphSnapshot snapshot, out string reason))
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
    private sealed class HostRuntimeCompatibility : IColdContainerCompatibilityPolicy
    {
        private readonly Dictionary<string, bool> cache = new(StringComparer.Ordinal);

        public bool IsCompatible(ColdStoredItemRecord item, ColdContainerRecord destination)
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
