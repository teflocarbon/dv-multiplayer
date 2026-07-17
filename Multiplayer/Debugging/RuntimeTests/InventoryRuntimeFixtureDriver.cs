#if DEBUG
using DV;
using DV.Common;
using DV.Interaction;
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Core.Items;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Integrations.Inventory;
using Multiplayer.Integrations.Storage;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests;

/// <summary>
/// Debug-build arrangement boundary for inventory scenarios. Fixture creation and retirement are
/// host authoritative; local placement deliberately uses Derail Valley's inventory/grabber APIs.
/// </summary>
internal sealed class InventoryRuntimeFixtureDriver
{
    private static readonly Dictionary<ushort, Guid> fixtureTokens = new();
    private static readonly Dictionary<Guid, NetworkedItem> fixtureItems = new();
    private static readonly Dictionary<Guid, Guid> coldFixtureTokens = new();

    private sealed class RuntimeFixtureMarker : MonoBehaviour
    {
        public Guid Token;
    }

    internal static void TrackLocalFixture(NetworkedItem item, string tokenText)
    {
        if (item == null || !Guid.TryParse(tokenText, out Guid token) || token == Guid.Empty)
            return;
        RuntimeFixtureMarker marker = item.GetComponent<RuntimeFixtureMarker>() ??
            item.gameObject.AddComponent<RuntimeFixtureMarker>();
        marker.Token = token;
        fixtureItems[token] = item;
    }

    internal static void TransferFixtureToCold(ushort retiredNetId, Guid persistentItemId)
    {
        if (retiredNetId == 0 || persistentItemId == Guid.Empty ||
            !fixtureTokens.TryGetValue(retiredNetId, out Guid token))
            return;
        fixtureTokens.Remove(retiredNetId);
        fixtureItems.Remove(token);
        coldFixtureTokens[persistentItemId] = token;
    }

    internal static void TransferFixtureFromCold(Guid persistentItemId,
        ushort materializedNetId)
    {
        if (persistentItemId == Guid.Empty || materializedNetId == 0 ||
            !coldFixtureTokens.TryGetValue(persistentItemId, out Guid token))
            return;
        coldFixtureTokens.Remove(persistentItemId);
        fixtureTokens[materializedNetId] = token;
        if (NetworkedItem.TryGet(materializedNetId, out NetworkedItem materialized) &&
            materialized != null)
        {
            fixtureItems[token] = materialized;
            TrackLocalFixture(materialized, token.ToString("D"));
        }
    }

    public IEnumerator Inspect(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        Inventory inventory = Inventory.Instance ??
            throw new RuntimeTestUnsupportedException("inventory-unavailable");
        yield return null;
        GameObject[] includingDropped = inventory.GetItemsArray(true) ?? Array.Empty<GameObject>();
        GameObject[] activeOnly = inventory.GetItemsArray(false) ?? Array.Empty<GameObject>();
        HashSet<GameObject> activeSet = new(activeOnly.Where(item => item != null));
        List<Dictionary<string, object>> items = includingDropped
            .Where(item => item != null).Distinct()
            .Select(item => SnapshotInventoryItem(inventory, item, activeSet.Contains(item)))
            .OrderBy(item => (int)item["slot"])
            .ToList();
        HashSet<ushort> requestedNetIds = new();
        foreach (string key in new[] { "shellNetId", "itemNetId", "materializedNetId" })
            if (command.Parameters.TryGetValue(key, out string value) &&
                ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out ushort netId) && netId != 0)
                requestedNetIds.Add(netId);
        List<Dictionary<string, object>> representations = NetworkedItem.GetAll()
            .Where(item => item != null && requestedNetIds.Contains(item.NetId))
            .Distinct()
            .Select(item => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["netId"] = item.NetId,
                ["prefabName"] = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name,
                ["activeSelf"] = item.gameObject.activeSelf,
                ["activeInHierarchy"] = item.gameObject.activeInHierarchy,
                ["inventorySlot"] = inventory.IndexOf(item.gameObject),
                ["itemState"] = item.DebugCurrentState.ToString(),
                ["position"] = DebugValueSnapshotter.Snapshot(item.transform.position)
            }).ToList();
        lock (run)
        {
            run.Result["capacity"] = inventory.Capacity;
            run.Result["includingDroppedCount"] = items.Count;
            run.Result["activeCount"] = activeSet.Count;
            run.Result["items"] = items;
            run.Result["representations"] = representations;
            run.Result["heldNetId"] = HeldNetworkedItem()?.NetId ?? 0;
        }
    }

    public IEnumerator PrefabCatalog(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        string filter = OptionalString(command, "filter", string.Empty);
        int limit = Mathf.Clamp(OptionalInt(command, "limit", 100), 1, 1000);
        IEnumerable<InventoryItemSpec> specs = Globals.G?.Items?.items ??
            Enumerable.Empty<InventoryItemSpec>();
        List<Dictionary<string, object>> matches = specs
            .Where(spec => spec != null && (string.IsNullOrWhiteSpace(filter) ||
                (spec.ItemPrefabName ?? string.Empty).IndexOf(filter,
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                (spec.LocalizedName ?? string.Empty).IndexOf(filter,
                    StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(spec => spec.ItemPrefabName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(spec => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["prefabName"] = spec.ItemPrefabName ?? string.Empty,
                ["localizedName"] = spec.LocalizedName ?? string.Empty,
                ["isContainer"] = spec.gameObject?.GetComponent<ItemContainer>() != null,
                ["belongsToPlayerDefault"] = spec.BelongsToPlayer,
                ["isEssentialDefault"] = spec.IsEssential
            }).ToList();
        yield return null;
        lock (run)
        {
            run.Result["filter"] = filter;
            run.Result["limit"] = limit;
            run.Result["items"] = matches;
            run.Result["returnedCount"] = matches.Count;
        }
    }

    public IEnumerator CreateFixture(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        NetworkLifecycle lifecycle = RequireHost();
        string prefabName = RequiredString(command, "prefabName");
        InventoryItemSpec spec = (Globals.G?.Items?.items ?? Enumerable.Empty<InventoryItemSpec>())
            .FirstOrDefault(item => item != null && string.Equals(item.ItemPrefabName,
                prefabName, StringComparison.OrdinalIgnoreCase));
        if (spec?.gameObject == null)
            throw new ArgumentException("unknown-item-prefab:" + prefabName);

        byte ownerId = OptionalByte(command, "ownerPlayerId", lifecycle.Server.SelfId);
        byte holderId = OptionalByte(command, "holderPlayerId", ownerId);
        string placement = OptionalString(command, "placement", "inventory")
            .Trim().ToLowerInvariant();
        if (!lifecycle.Server.TryGetServerPlayer(ownerId, out ServerPlayer owner))
            throw new ArgumentException("owner-player-not-found:" + ownerId);
        ServerPlayer holder = null;
        if (placement != "world" &&
            !lifecycle.Server.TryGetServerPlayer(holderId, out holder))
            throw new ArgumentException("holder-player-not-found:" + holderId);

        Vector3 absolute = Position(command, holder?.AbsoluteWorldPosition ??
            PlayerManager.PlayerTransform.GetWorldAbsolutePosition());
        // Keep the representation outside every player's interest radius while its real Unity
        // Start path runs. NetworkedItem registers from Awake, so initializing it at the target
        // position would let the normal relevance loop publish a provisional Dropped Create
        // before this driver installs the requested owner and inventory placement.
        Vector3 quarantineAbsolute = absolute + Vector3.up * 10000f;
        GameObject instance = UnityEngine.Object.Instantiate(spec.gameObject,
            quarantineAbsolute + WorldMover.currentMove, Quaternion.identity);
        NetworkedItem item = instance.GetOrAddComponent<NetworkedItem>();
        if (item == null || item.NetId == 0)
        {
            UnityEngine.Object.Destroy(instance);
            throw new InvalidOperationException("fixture-netid-allocation-failed");
        }
        if (item.Item?.InventorySpecs != null)
            item.Item.InventorySpecs.BelongsToPlayer = OptionalBool(command,
                "belongsToPlayer", ownerId != 0);
        item.BeginColdMaterialization();
        bool interactionAllowed = item.Item?.InteractionAllowed == true;
        Rigidbody body = item.Item?.ItemRigidbody;
        bool bodyWasKinematic = body != null && body.isKinematic;
        if (item.Item != null) item.Item.InteractionAllowed = false;
        if (body != null) body.isKinematic = true;
        // Let the real item Start/initialization path run before inventory deactivation. Several
        // save-data providers (notably Flashlight) bind their state during Start and cannot be
        // faithfully serialized if a fixture is hidden in the same frame it is instantiated.
        yield return null;
        yield return new WaitForEndOfFrame();
        if (item == null || item.Item == null)
            throw new InvalidOperationException("fixture-destroyed-during-initialization");
        item.Item.InteractionAllowed = interactionAllowed;
        if (body != null) body.isKinematic = bodyWasKinematic;
        instance.transform.position = absolute + WorldMover.currentMove;
        item.CompleteColdMaterialization();
        NetworkedItemManager.Instance.ScheduleTrackedValueFinalization(item);

        ItemUpdateData snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create) ??
            throw new InvalidOperationException("fixture-snapshot-unavailable");
        ApplyRequestedPlacement(snapshot, placement, holderId, absolute,
            OptionalInt(command, "slot", -1), item.Item?.InventorySpecs?.IsEssential == true);
        // NetworkedItem registers synchronously from Awake. For player-owned prefabs that
        // briefly seeds the host as owner before the debug fixture has supplied its requested
        // owner. Replace that provisional record instead of asking the normal adoption state
        // machine to transfer an already-owned item (which it intentionally refuses to do).
        foreach (ServerPlayer player in lifecycle.Server.ServerPlayers)
            player.RemoveOwnedItem(item.NetId);
        AuthoritativeItemRegistry.Remove(item.NetId);
        snapshot.AuthorityRevision = 0;
        item.ServerInitialiseAdoptedItem(owner, snapshot);
        if (placement != "world" && holderId != ownerId)
        {
            ItemUpdateData heldSnapshot = item.CreateUpdateData(
                ItemUpdateData.ItemUpdateType.FullSync) ??
                throw new InvalidOperationException("fixture-held-snapshot-unavailable");
            ApplyRequestedPlacement(heldSnapshot, placement, holderId, absolute,
                OptionalInt(command, "slot", -1),
                item.Item?.InventorySpecs?.IsEssential == true);
            heldSnapshot.PersistentOwnerPlayerId = ownerId;
            heldSnapshot.AuthorityRevision = item.AuthorityRevision;
            if (!AuthoritativeItemRegistry.TryApplyTransition(item, heldSnapshot, holder,
                    ItemTransitionReason.ClientState, true, out string rejection))
                throw new InvalidOperationException("fixture-holder-transition-rejected:" +
                    rejection);
            item.ApplyServerCanonicalSnapshot(heldSnapshot);
            snapshot = heldSnapshot;
        }
        snapshot.UpdateType = ItemUpdateData.ItemUpdateType.Create;
        snapshot.PrefabName = item.Item.InventorySpecs.ItemPrefabName;

        foreach (ServerPlayer player in lifecycle.Server.ServerPlayers)
        {
            if (player.Peer == lifecycle.Server.SelfPeer ||
                player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;
            player.KnownItems[item] = lifecycle.Tick;
            lifecycle.Server.SendItemsBulkUpdatePacket(new List<ItemUpdateData> { snapshot },
                player);
        }

        Guid token = Guid.NewGuid();
        fixtureTokens[item.NetId] = token;
        fixtureItems[token] = item;
        TrackLocalFixture(item, token.ToString("D"));
        yield return null;
        yield return new WaitForEndOfFrame();
        lock (run)
        {
            run.Result["fixtureToken"] = token.ToString("D");
            run.Result["netId"] = item.NetId;
            run.Result["prefabName"] = snapshot.PrefabName;
            run.Result["ownerPlayerId"] = ownerId;
            run.Result["holderPlayerId"] = snapshot.PlayerId;
            run.Result["placement"] = placement;
            run.Result["authorityRevision"] = item.AuthorityRevision;
            run.Result["isContainer"] = item.GetComponent<ItemContainer>() != null;
        }
    }

    public IEnumerator PlaceFixture(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        NetworkLifecycle lifecycle = RequireHost();
        NetworkedItem item = RequireFixture(command, out Guid token);
        string placement = OptionalString(command, "placement", "inventory")
            .Trim().ToLowerInvariant();
        byte holderId = OptionalByte(command, "holderPlayerId",
            item.PersistentOwnerPlayerId);
        ServerPlayer actor = ResolveFixtureActor(lifecycle, item);
        ServerPlayer holder = null;
        if (placement != "world" &&
            !lifecycle.Server.TryGetServerPlayer(holderId, out holder))
            throw new ArgumentException("holder-player-not-found:" + holderId);
        Vector3 absolute = Position(command, holder?.AbsoluteWorldPosition ??
            item.transform.GetWorldAbsolutePosition());
        ItemUpdateData snapshot = item.CreateUpdateData(
            ItemUpdateData.ItemUpdateType.FullSync) ??
            throw new InvalidOperationException("fixture-snapshot-unavailable");
        ApplyRequestedPlacement(snapshot, placement, holderId, absolute,
            OptionalInt(command, "slot", -1), item.Item?.InventorySpecs?.IsEssential == true);
        snapshot.AuthorityRevision = item.AuthorityRevision;
        if (!AuthoritativeItemRegistry.TryApplyTransition(item, snapshot, actor,
                ItemTransitionReason.HostLocalState, true, out string rejection))
            throw new InvalidOperationException("fixture-placement-rejected:" + rejection);
        item.ApplyServerCanonicalSnapshot(snapshot);
        lifecycle.Server.SendItemUpdatePacket(snapshot);
        yield return null;
        lock (run)
        {
            run.Result["fixtureToken"] = token.ToString("D");
            run.Result["netId"] = item.NetId;
            run.Result["placement"] = placement;
            run.Result["holderPlayerId"] = snapshot.PlayerId;
            run.Result["authorityRevision"] = item.AuthorityRevision;
        }
    }

    public IEnumerator PlaceLocal(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        ushort netId = RequiredUShort(command, "netId");
        if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null)
            throw new ArgumentException("item-not-found:" + netId);
        string placement = OptionalString(command, "placement", "inventory")
            .Trim().ToLowerInvariant();
        Inventory inventory = Inventory.Instance ??
            throw new RuntimeTestUnsupportedException("inventory-unavailable");
        int requestedSlot = OptionalInt(command, "slot", -1);

        switch (placement)
        {
            case "inventory":
                item.Item?.ForceEndInteraction();
                inventory.DropItemFromHandsOrInventory(item.gameObject);
                int inserted = requestedSlot >= 0
                    ? inventory.AddItemToInventory(item.gameObject, requestedSlot, false)
                    : inventory.AddItemToInventory(item.gameObject, false);
                if (inserted < 0)
                    throw new InvalidOperationException("inventory-placement-failed");
                break;
            case "world":
                item.Item?.ForceEndInteraction();
                inventory.DropItemFromHandsOrInventory(item.gameObject);
                Vector3 absolute = Position(command,
                    PlayerManager.PlayerTransform.GetWorldAbsolutePosition() +
                    PlayerManager.PlayerTransform.forward * 1.5f);
                item.transform.position = absolute + WorldMover.currentMove;
                break;
            case "hand":
                yield return ForceHold(item, inventory);
                break;
            default:
                throw new ArgumentException("invalid-placement:" + placement);
        }

        yield return null;
        yield return new WaitForEndOfFrame();
        item.ProcessLocalStateObservation("runtime-inventory-arrangement");
        lock (run)
        {
            run.Result["netId"] = netId;
            run.Result["placement"] = placement;
            run.Result["slot"] = inventory.IndexOf(item.gameObject);
            run.Result["equippedSlot"] = inventory.GetEquipSlotForItem(item.gameObject);
            run.Result["itemState"] = item.DebugCurrentState.ToString();
            run.Result["persistentOwnerPlayerId"] = item.PersistentOwnerPlayerId;
        }
    }

    public IEnumerator DestroyFixture(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireHost();
        string tokenText = RequiredString(command, "fixtureToken");
        if (!Guid.TryParse(tokenText, out Guid token) || token == Guid.Empty)
            throw new ArgumentException("invalid-fixture-token:" + tokenText);
        if (!fixtureItems.TryGetValue(token, out NetworkedItem item) || item == null)
        {
            bool stillCold = coldFixtureTokens.Values.Contains(token);
            if (stillCold)
                throw new InvalidOperationException("fixture-remains-in-cold-storage:" + tokenText);
            // Cleanup is deliberately idempotent. A scenario may have already retired the
            // materialized fixture before the dashboard's final cleanup command arrives.
            lock (run)
            {
                run.Result["fixtureToken"] = token.ToString("D");
                run.Result["alreadyAbsent"] = true;
                run.Result["destroyed"] = true;
            }
            yield break;
        }
        ushort netId = item.NetId;
        NetworkedLostAndFoundManager.RemoveRuntimeFixture(netId);
        ItemUpdateData destroy = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy) ??
            throw new InvalidOperationException("fixture-destroy-snapshot-unavailable");
        if (AuthoritativeItemRegistry.TryGet(netId,
                out AuthoritativeItemRegistry.Record authority))
            AuthoritativeItemRegistry.WriteToSnapshot(authority, destroy,
                ItemTransitionReason.HostLocalState);
        item.BeginColdStorageRetirement();
        foreach (ServerPlayer player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            player.NearbyItems.Remove(item);
            player.KnownItems.Remove(item);
        }
        NetworkLifecycle.Instance.Server.SendItemUpdatePacket(destroy);
        InventoryIntegration.RevokeMembership(item.gameObject, netId);
        fixtureTokens.Remove(netId);
        fixtureItems.Remove(token);
        UnityEngine.Object.Destroy(item.gameObject);
        yield return null;
        yield return new WaitForEndOfFrame();
        lock (run)
        {
            run.Result["fixtureToken"] = token.ToString("D");
            run.Result["netId"] = netId;
            run.Result["destroyed"] = !NetworkedItem.TryGet(netId, out _);
        }
        if (NetworkedItem.TryGet(netId, out _))
            throw new InvalidOperationException("fixture-destruction-not-observed");
    }

    public IEnumerator CleanupLocalFixtures(RuntimeTestCommandDto command,
        RuntimeTestRunDto run)
    {
        foreach (string key in new[] { "itemNetId", "materializedNetId" })
            if (command.Parameters.TryGetValue(key, out string idText) &&
                ushort.TryParse(idText, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out ushort id) && id != 0)
                NetworkedLostAndFoundManager.RemoveRuntimeFixture(id);
        HashSet<Guid> tokens = new();
        foreach (string key in new[] { "itemFixtureToken", "shellFixtureToken" })
            if (command.Parameters.TryGetValue(key, out string raw) &&
                Guid.TryParse(raw, out Guid token) && token != Guid.Empty)
                tokens.Add(token);
        if (tokens.Count == 0)
            throw new ArgumentException("fixture-cleanup-token-missing");

        RuntimeFixtureMarker[] markers = Resources
            .FindObjectsOfTypeAll<RuntimeFixtureMarker>()
            .Where(marker => marker != null && tokens.Contains(marker.Token))
            .Distinct().ToArray();
        int purged = 0;
        foreach (RuntimeFixtureMarker marker in markers)
        {
            NetworkedItem item = marker.GetComponent<NetworkedItem>();
            if (item == null) continue;
            NetworkedLostAndFoundManager.RemoveRuntimeFixture(item.NetId);
            if (NetworkLifecycle.Instance.IsHost())
                PurgeHostFixtureRepresentation(item, marker.Token);
            else
                NetworkedItemManager.Instance.PurgeClientFixtureRepresentation(item);
            purged++;
        }
        foreach (Guid token in tokens)
            fixtureItems.Remove(token);
        yield return null;
        yield return new WaitForEndOfFrame();
        int remaining = Resources.FindObjectsOfTypeAll<RuntimeFixtureMarker>()
            .Count(marker => marker != null && tokens.Contains(marker.Token));
        lock (run)
        {
            run.Result["fixtureTokens"] = tokens.Select(token => token.ToString("D")).ToArray();
            run.Result["representationsFound"] = markers.Length;
            run.Result["representationsPurged"] = purged;
            run.Result["representationsRemaining"] = remaining;
        }
        if (remaining != 0)
            throw new InvalidOperationException("fixture-representations-remain:" + remaining);
    }

    private static void PurgeHostFixtureRepresentation(NetworkedItem item, Guid token)
    {
        if (item == null) return;
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        ushort netId = item.NetId;
        bool isCanonical = netId != 0 && NetworkedItem.TryGet(netId,
            out NetworkedItem canonical) && canonical == item;

        item.BeginColdStorageRetirement();
        if (isCanonical)
        {
            ItemUpdateData destroy = item.CreateUpdateData(
                ItemUpdateData.ItemUpdateType.Destroy);
            if (destroy != null)
            {
                if (AuthoritativeItemRegistry.TryGet(netId,
                        out AuthoritativeItemRegistry.Record authority))
                    AuthoritativeItemRegistry.WriteToSnapshot(authority, destroy,
                        ItemTransitionReason.HostLocalState);
                lifecycle.Server.SendItemUpdatePacket(destroy);
            }
            foreach (ServerPlayer player in lifecycle.Server.ServerPlayers)
            {
                player.NearbyItems.Remove(item);
                player.KnownItems.Remove(item);
                player.RemoveOwnedItem(netId);
            }
            AuthoritativeItemRegistry.Remove(netId);
            fixtureTokens.Remove(netId);
        }

        // If a retired projection retained a NetId that has since been reused, only remove
        // this exact Unity object. Never purge inventory claims or authority by the reused ID.
        InventoryIntegration.RevokeMembership(item.gameObject,
            isCanonical ? netId : (ushort)0);
        InventoryIntegration.PurgeContainerMembership(item.gameObject);
        StorageIntegration.MoveTo(item.Item, StorageMembership.None);
        fixtureItems.Remove(token);
        item.gameObject.SetActive(false);
        item.NetId = 0;
        UnityEngine.Object.Destroy(item.gameObject);
    }

    private NetworkedItem RequireFixture(RuntimeTestCommandDto command, out Guid token)
    {
        ushort netId = RequiredUShort(command, "netId");
        string tokenText = RequiredString(command, "fixtureToken");
        if (!Guid.TryParse(tokenText, out token) || token == Guid.Empty)
            throw new ArgumentException("invalid-runtime-fixture-token:" + tokenText);
        if (!fixtureItems.TryGetValue(token, out NetworkedItem item) || item == null)
        {
            fixtureTokens.Remove(netId);
            fixtureItems.Remove(token);
            throw new ArgumentException("fixture-item-not-found:" + tokenText);
        }
        if (item.NetId != netId || !fixtureTokens.TryGetValue(netId, out Guid indexedToken) ||
            indexedToken != token)
            throw new ArgumentException("fixture-identity-mismatch:" + netId + ":" + tokenText);
        return item;
    }

    private static ServerPlayer ResolveFixtureActor(NetworkLifecycle lifecycle,
        NetworkedItem item)
    {
        byte actorId = item.PersistentOwnerPlayerId != 0
            ? item.PersistentOwnerPlayerId : lifecycle.Server.SelfId;
        if (!lifecycle.Server.TryGetServerPlayer(actorId, out ServerPlayer actor))
            throw new InvalidOperationException("fixture-owner-not-connected:" + actorId);
        return actor;
    }

    private static IEnumerator ForceHold(NetworkedItem item, Inventory inventory)
    {
        Grabber grabber = UnityEngine.Object.FindObjectsOfType<Grabber>()
            .FirstOrDefault(candidate => candidate != null &&
                candidate.GetComponent<GrabberInteractionHandlerDV>() != null);
        GrabberInteractionHandlerDV interaction =
            grabber?.GetComponent<GrabberInteractionHandlerDV>();
        GrabHandlerItem handler = item.GetComponent<GrabHandlerItem>();
        if (grabber == null || interaction == null || handler == null)
            throw new RuntimeTestUnsupportedException("local-grabber-or-item-handler-unavailable");
        if (grabber.CurrentItemHeld != null && grabber.CurrentItemHeld != handler)
            throw new InvalidOperationException("grabber-already-holding-item");
        if (grabber.CurrentItemHeld == handler)
            yield break;
        inventory.DropItemFromHandsOrInventory(item.gameObject);
        interaction.RequestForceHold(handler);
        yield return null;
        if (grabber.CurrentItemHeld != handler || !handler.IsGrabbed())
            throw new InvalidOperationException("hand-placement-failed");
    }

    private Dictionary<string, object> SnapshotInventoryItem(Inventory inventory,
        GameObject item, bool returnedByActiveQuery)
    {
        int slot = inventory.IndexOf(item);
        NetworkedItem networked = item.GetComponent<NetworkedItem>();
        bool dropped = slot >= 0 && inventory.GetSlotDroppedState(slot);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["slot"] = slot,
            ["netId"] = networked?.NetId ?? 0,
            ["prefabName"] = networked?.Item?.InventorySpecs?.ItemPrefabName ?? item.name,
            ["persistentOwnerPlayerId"] = networked?.PersistentOwnerPlayerId ?? 0,
            ["isContainer"] = item.GetComponent<ItemContainer>() != null,
            ["returnedByActiveQuery"] = returnedByActiveQuery,
            ["normalVisibleSlot"] = slot >= 0 && !dropped,
            ["dropped"] = dropped,
            ["reserved"] = slot >= 0 && inventory.GetSlotReservedState(slot),
            ["locked"] = slot >= 0 && inventory.GetSlotLockState(slot),
            ["equippedSlot"] = inventory.GetEquipSlotForItem(item),
            ["activeSelf"] = item.activeSelf,
            ["fixtureToken"] = networked != null && fixtureTokens.TryGetValue(networked.NetId,
                out Guid token) ? token.ToString("D") : string.Empty
        };
    }

    private static void ApplyRequestedPlacement(ItemUpdateData snapshot, string placement,
        byte holderId, Vector3 absolute, int slot, bool essential)
    {
        snapshot.ItemPosition = absolute;
        snapshot.ItemRotation = Quaternion.identity;
        snapshot.ThrowDirection = Vector3.zero;
        snapshot.InventoryClaimSlot = essential && slot >= 0 ? slot : -1;
        snapshot.InventoryClaimFlags = ItemInventoryClaimFlags.None;
        switch (placement)
        {
            case "inventory":
                snapshot.ItemState = ItemState.InInventory;
                snapshot.PlayerId = holderId;
                break;
            case "hand":
                snapshot.ItemState = ItemState.InHand;
                snapshot.PlayerId = holderId;
                break;
            case "world":
                snapshot.ItemState = ItemState.Dropped;
                snapshot.PlayerId = 0;
                break;
            default:
                throw new ArgumentException("invalid-placement:" + placement);
        }
    }

    private static NetworkLifecycle RequireHost()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle?.IsServerRunning != true || lifecycle.Server == null)
            throw new RuntimeTestUnsupportedException("host-runtime-required");
        return lifecycle;
    }

    private static NetworkedItem HeldNetworkedItem()
    {
        Grabber grabber = UnityEngine.Object.FindObjectsOfType<Grabber>()
            .FirstOrDefault(candidate => candidate?.CurrentItemHeld != null);
        return grabber?.CurrentItemHeld?.GetComponentInParent<NetworkedItem>();
    }

    private static Vector3 Position(RuntimeTestCommandDto command, Vector3 fallback) =>
        TryFloat(command, "x", out float x) && TryFloat(command, "y", out float y) &&
        TryFloat(command, "z", out float z) ? new Vector3(x, y, z) : fallback;

    private static bool TryFloat(RuntimeTestCommandDto command, string key, out float value)
    {
        value = 0f;
        return command.Parameters.TryGetValue(key, out string text) &&
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
    private static int OptionalInt(RuntimeTestCommandDto command, string key, int fallback) =>
        command.Parameters.TryGetValue(key, out string text) &&
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : fallback;
    private static byte OptionalByte(RuntimeTestCommandDto command, string key, byte fallback) =>
        command.Parameters.TryGetValue(key, out string text) &&
        byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte value)
            ? value : fallback;
    private static bool OptionalBool(RuntimeTestCommandDto command, string key, bool fallback) =>
        command.Parameters.TryGetValue(key, out string text) &&
        bool.TryParse(text, out bool value) ? value : fallback;
    private static ushort RequiredUShort(RuntimeTestCommandDto command, string key) =>
        command.Parameters.TryGetValue(key, out string text) &&
        ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out ushort value) && value != 0 ? value :
            throw new ArgumentException("missing-or-invalid-parameter:" + key);
    private static string RequiredString(RuntimeTestCommandDto command, string key) =>
        command.Parameters.TryGetValue(key, out string value) &&
        !string.IsNullOrWhiteSpace(value) ? value.Trim() :
            throw new ArgumentException("missing-or-invalid-parameter:" + key);
    private static string OptionalString(RuntimeTestCommandDto command, string key,
        string fallback) => command.Parameters.TryGetValue(key, out string value)
            ? value : fallback;
}
#endif
