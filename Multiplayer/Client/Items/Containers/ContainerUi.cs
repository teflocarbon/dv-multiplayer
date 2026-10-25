using System;
using System.Collections.Generic;
using System.Linq;
using DV.Common;
using DV.CabControls;
using DV;
using DV.InventorySystem;
using DV.UI.Inventory;
using DV.Util;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;
using UnityEngine;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Items.Containers;

internal sealed class ColdContainerItemProxy : MonoBehaviour, IInventoryItemSpec
{
    private IInventoryItemSpec source;
    /// <summary>The handle of the container the row is in.</summary>
    public uint ContainerHandle { get; private set; }
    /// <summary>The row's own handle: its item, and for a container item what opens it.</summary>
    public uint ItemHandle { get; private set; }
    public int Slot { get; private set; }
    public bool IsContainer { get; private set; }
    public bool ForeignOwned { get; private set; }

    public void Initialize(IInventoryItemSpec sourceSpec, ClientboundItemListPacket view, ItemRow row)
    {
        source = sourceSpec;
        ContainerHandle = view.Container;
        ItemHandle = row.Handle;
        Slot = row.Slot;
        IsContainer = (row.Flags & ItemRowFlags.Container) != 0;
        ForeignOwned = (row.Flags & ItemRowFlags.SomeoneElses) != 0;
        name = $"ColdContainerProxy:{row.PrefabName}:{ItemHandle}";
        if (IsContainer)
            gameObject.AddComponent<ColdProxyContainer>().Initialize(ItemHandle,
                source?.LocalizedName ?? row.DisplayName);
    }

    public GameObject GetGameObject() => gameObject;
    public bool BelongsToPlayer { get => source?.BelongsToPlayer ?? false; set { } }
    public bool ImmuneToDumpster { get => source?.ImmuneToDumpster ?? false; set { } }
    public bool IsEssential { get => source?.IsEssential ?? false; set { } }
    public string ItemPrefabName => source?.ItemPrefabName ?? name;
    public string LocalizationKey => source?.LocalizationKey ?? string.Empty;
    public string LocalizedName => source?.LocalizedName ?? ItemPrefabName;
    public string LocalizedDescription => source?.LocalizedDescription ?? string.Empty;
    public GameObject PreviewPrefab { get => source?.PreviewPrefab; set { } }
    public Bounds PreviewBounds { get => source?.PreviewBounds ?? default; set { } }
    public Vector3 PreviewRotation => source?.PreviewRotation ?? Vector3.zero;
    public Sprite ItemIconSpriteSimple => source?.ItemIconSpriteSimple;
    public Sprite ItemIconSprite => source?.ItemIconSprite;
    public Sprite ItemIconSpriteDropped => source?.ItemIconSpriteDropped;
}

internal sealed class ColdProxyContainer : AItemContainer
{
    private uint handle;
    private string localizedName = string.Empty;
    /// <summary>The stored box's row handle: what opens its contents.</summary>
    public uint Handle => handle;
    protected override string ContainerName { get; set; } = "ColdProxy";
    public override string ContainerNameLocalized { get => localizedName; protected set => localizedName = value; }
    public override bool DirectInteractionAllowed => true;

    protected override void Awake() { }
    public void Initialize(uint itemHandle, string displayName)
    {
        handle = itemHandle;
        localizedName = displayName ?? string.Empty;
    }
    public override bool ValidItem(GameObject item) => false;
    public override void Clear() { }
    protected override bool InitializeSpecific() => true;
    public override void PlayInteractionEffects(bool open) { }
    public override void ToggleContainerAccess() => ContainerUi.OpenChild(handle);
}

/// <summary>
/// Detached-row adapter for Derail Valley's existing non-VR and VR inventory views: a cold
/// container's list (docs/design/items/wire.md, "One list") shown as its slots, and the player's
/// drags in it sent as item requests.
/// </summary>
internal static class ContainerUi
{
    private static readonly List<GameObject> proxies = new();
    // This UI's requests still waiting for their result: each one's answer reloads the view.
    private static readonly HashSet<uint> pendingMutations = new();
    private static ClientboundItemListPacket activeView;
    // The box the player opened: vanilla's active container while the view shows it or a box in it.
    private static AItemContainer shellContainer;
    private static ushort activeShellNetId;
    private static ushort requestedShellNetId;
    private static uint pendingRequestId;
    private static bool initialized;
#if DEBUG
    private static uint mutationSequence;
    private static ClientboundItemResultPacket lastMutation;
    private static uint viewSequence;
    private static ClientboundItemListPacket lastView;
#endif

    public static bool Active => activeView?.Accepted == true;
    public static uint ActiveHandle => activeView?.Container ?? 0;
    public static uint ActiveRevision => activeView?.Revision ?? 0;
#if DEBUG
    public static int ActiveCapacity => activeView?.Capacity ?? 0;
    public static int ActiveRowCount => activeView?.Rows?.Length ?? 0;
    public static int[] ActiveSlots => activeView?.Rows?.Select(row => row.Slot).ToArray() ?? Array.Empty<int>();
    /// <summary>The handles of the active view's container items: each opens its contents.</summary>
    public static uint[] ActiveChildContainerHandles => activeView?.Rows?
        .Select(row => (row.Flags & ItemRowFlags.Container) != 0 ? row.Handle : 0).ToArray() ?? Array.Empty<uint>();
    public static uint MutationSequence => mutationSequence;
    public static ClientboundItemResultPacket LastMutation => lastMutation;
    public static uint ViewSequence => viewSequence;
    public static ClientboundItemListPacket LastView => lastView;
#endif

    public static void Open(AItemContainer container)
    {
        if (container == null || ItemClient.Current == null ||
            !NetworkedItem.TryGetNetworkedItem(container.GetComponent<ItemBase>(), out NetworkedItem shell) ||
            shell.NetId == 0)
            return;
        EnsureInitialized();
        shellContainer = container;
        requestedShellNetId = shell.NetId;
        activeShellNetId = 0;
        activeView = null;
        pendingRequestId = ItemClient.Current.RequestItemList(ItemListKind.Container, requestedShellNetId);
        global::Multiplayer.Multiplayer.Log($"[Cold Container] Browse requested: " +
            $"request={pendingRequestId}, shellNetId={requestedShellNetId}, " +
            $"containerId={container.ContainerId}");
    }

    /// <summary>
    /// The box shown is no longer vanilla's active container (closed, or another opened): its view
    /// ends and its row proxies go with it (OI-CON-10). A late answer to its list is ignored. A
    /// stored box's row clicked on the inventory screen isn't a close: vanilla makes the row's
    /// <see cref="ColdProxyContainer"/> active, so the box shown is made active again and the
    /// view moves to the stored box's list.
    /// </summary>
    public static void Closed(AItemContainer nowActive)
    {
        if (activeShellNetId == 0 && requestedShellNetId == 0 && proxies.Count == 0)
            return;
        if (nowActive is ColdProxyContainer child && activeShellNetId != 0 && shellContainer != null &&
            proxies.Contains(child.gameObject))
        {
            // Back to the box shown first: its own Closed call returns at the next check.
            DV.InventorySystem.Inventory.Instance.ItemContainerRegistry.ActiveContainer = shellContainer;
            OpenChild(child.Handle);
            return;
        }
        if (nowActive != null &&
            NetworkedItem.TryGetNetworkedItem(nowActive.GetComponent<ItemBase>(), out NetworkedItem shell) &&
            shell.NetId != 0 && (shell.NetId == activeShellNetId || shell.NetId == requestedShellNetId))
            return;
        global::Multiplayer.Multiplayer.Log($"[Cold Container] Closed: shellNetId={activeShellNetId}, " +
            $"proxies={proxies.Count}");
        activeView = null;
        shellContainer = null;
        activeShellNetId = 0;
        requestedShellNetId = 0;
        pendingRequestId = 0;
        ClearProxies();
    }

#if DEBUG
    /// <summary>The row proxies alive now (0 once the box closes).</summary>
    public static int ProxyCount => proxies.Count(proxy => proxy != null);
#endif

    /// <summary>Opens a container item's contents, by its row's handle.</summary>
    public static void OpenChild(uint handle)
    {
        if (handle == 0 || ItemClient.Current == null) return;
        EnsureInitialized();
        pendingRequestId = ItemClient.Current.RequestItemList(ItemListKind.Container, 0, handle);
    }

    public static bool TryInterceptAdd(AItemContainer destination, GameObject item, int slot)
    {
        if (!IsActive(destination) || item == null)
        {
            global::Multiplayer.Multiplayer.Log($"[Cold Container] Deposit not intercepted: " +
                $"active={IsActive(destination)}, destination={destination?.ContainerId ?? "<null>"}, " +
                $"item={item?.name ?? "<null>"}, slot={slot}, shellNetId={activeShellNetId}, " +
                $"viewAccepted={activeView?.Accepted == true}");
            return false;
        }
        int requestedSlot = slot;
        slot = ResolveFreeDestinationSlot(slot);
        if (slot < 0)
        {
            global::Multiplayer.Multiplayer.LogWarning($"[Cold Container] Deposit suppressed: " +
                $"containerHandle={ActiveHandle}, requestedSlot={requestedSlot}, " +
                "reason=container-full");
            return true;
        }
        EnsureInitialized();
        ColdContainerItemProxy proxy = item.GetComponent<ColdContainerItemProxy>();
        ItemRequest request = proxy != null
            ? new ItemRequest
            {
                // A row dragged from one open container into this one.
                Action = ItemAction.MoveInContainer,
                ItemHandle = proxy.ItemHandle,
                ExpectedRevision = ActiveRevision,
                Container = ActiveHandle,
                ContainerRevision = ActiveRevision,
                Slot = slot
            }
            : new ItemRequest
            {
                Action = ItemAction.Deposit,
                // An inventory row's stand-in (D14) names its item as the object would.
                ItemNetId = item.GetComponent<InventoryStandIn>()?.NetId ?? item.GetComponent<NetworkedItem>()?.NetId ?? 0,
                ExpectedRevision = ActiveRevision,
                Container = ActiveHandle,
                ShellNetId = activeShellNetId,
                Slot = slot
            };
        Send(request, $"requestedSlot={requestedSlot}");
        return true;
    }

    private static int ResolveFreeDestinationSlot(int requestedSlot)
    {
        if (activeView?.Accepted != true || activeView.Capacity <= 0)
            return -1;
        HashSet<int> occupied = new(activeView.Rows?.Select(row => row.Slot) ?? Enumerable.Empty<int>());
        if (requestedSlot >= 0 && requestedSlot < activeView.Capacity &&
            !occupied.Contains(requestedSlot))
            return requestedSlot;
        for (int slot = 0; slot < activeView.Capacity; slot++)
            if (!occupied.Contains(slot))
                return slot;
        return -1;
    }

    public static bool TryInterceptRemove(AItemContainer source, int slot,
        int destinationInventorySlot)
    {
        if (!IsActive(source)) return false;
        Send(new ItemRequest
        {
            Action = ItemAction.Withdraw,
            ItemHandle = RowAt(slot)?.Handle ?? 0,
            ExpectedRevision = ActiveRevision,
            Slot = destinationInventorySlot
        }, $"sourceSlot={slot}");
        return true;
    }

    public static bool TryInterceptRemove(AItemContainer source, int slot) =>
        TryInterceptRemove(source, slot,
            DV.InventorySystem.Inventory.Instance?.GetFirstFreeSlot() ?? -1);

    public static bool TryInterceptMove(AItemContainer source, int from, int to)
    {
        if (!IsActive(source)) return false;
        Send(new ItemRequest
        {
            Action = ItemAction.MoveInContainer,
            ItemHandle = RowAt(from)?.Handle ?? 0,
            ExpectedRevision = ActiveRevision,
            Container = ActiveHandle,
            ContainerRevision = ActiveRevision,
            Slot = to
        }, $"sourceSlot={from}");
        return true;
    }

    private static ItemRow RowAt(int slot) => activeView?.Rows?.FirstOrDefault(row => row.Slot == slot);

    private static void Send(ItemRequest request, string detail)
    {
        uint requestId = ItemClient.Current?.SendItemRequest(request) ?? 0;
        if (requestId == 0)
        {
            global::Multiplayer.Multiplayer.LogWarning($"[Cold Container] {request.Action} not sent: " +
                $"no item or row ({detail})");
            return;
        }
        pendingMutations.Add(requestId);
        global::Multiplayer.Multiplayer.Log($"[Cold Container] {request.Action} requested: " +
            $"request={requestId}, containerHandle={request.Container}, itemNetId={request.ItemNetId}, " +
            $"itemHandle={request.ItemHandle}, slot={request.Slot}, revision={request.ExpectedRevision}, {detail}");
        PublishMutation("container.mutation-requested", requestId, new()
        {
            ["requestId"] = requestId,
            ["kind"] = request.Action.ToString(),
            ["shellNetId"] = request.ShellNetId,
            ["containerHandle"] = request.Container,
            ["itemNetId"] = request.ItemNetId,
            ["itemHandle"] = request.ItemHandle,
            ["destinationSlot"] = request.Slot
        });
    }

    private static bool IsActive(AItemContainer container) => container != null &&
        DV.InventorySystem.Inventory.Instance?.ItemContainerRegistry?.ActiveContainer == container &&
        activeShellNetId != 0;

    private static void EnsureInitialized()
    {
        if (initialized || ItemClient.Current == null) return;
        initialized = true;
        NetworkedItemManager.Cleared += Reset;
        ItemClient.Current.ItemListReceived += OnView;
        ItemClient.Current.ItemResultReceived += OnMutation;
    }

    private static void OnMutation(ClientboundItemResultPacket result)
    {
        if (result == null || !pendingMutations.Remove(result.RequestId))
            return;
#if DEBUG
        lastMutation = result;
        mutationSequence++;
#endif
        global::Multiplayer.Multiplayer.Log($"[Cold Container] Request completed: " +
            $"request={result.RequestId}, accepted={result.Accepted}, " +
            $"refusal={result.Refusal}, detail={result.Detail ?? string.Empty}, " +
            $"revision={result.Revision}, itemNetId={result.ItemNetId}");
        PublishMutation("container.mutation-completed", result.RequestId, new()
        {
            ["requestId"] = result.RequestId,
            ["accepted"] = result.Accepted,
            ["refusal"] = result.Refusal.ToString(),
            ["reason"] = result.Detail ?? string.Empty,
            ["revision"] = result.Revision,
            ["materializedItemNetId"] = result.ItemNetId
        }, result.Accepted ? DebugSeverity.Info : DebugSeverity.Warning);
        if (activeShellNetId == 0) return;
        pendingRequestId = ItemClient.Current.RequestItemList(ItemListKind.Container, 0, ActiveHandle);
    }

    private static void OnView(ClientboundItemListPacket view)
    {
        if (view == null || view.Kind != ItemListKind.Container)
            return;
#if DEBUG
        lastView = view;
        viewSequence++;
#endif
        if (view.RequestId != pendingRequestId)
        {
            global::Multiplayer.Multiplayer.Log($"[Cold Container] Browse response ignored: " +
                $"response={view.RequestId}, pending={pendingRequestId}");
            return;
        }
        global::Multiplayer.Multiplayer.Log($"[Cold Container] Browse response: " +
            $"request={view.RequestId}, accepted={view.Accepted}, " +
            $"reason={view.Detail ?? string.Empty}, handle={view.Container}, " +
            $"revision={view.Revision}, rows={view.Rows?.Length ?? 0}");
        activeView = view;
        if (!view.Accepted)
        {
            activeShellNetId = 0;
            ClearProxies();
            return;
        }
        if (activeShellNetId == 0)
            activeShellNetId = requestedShellNetId;
        Project(view);
    }

    private static void Project(ClientboundItemListPacket view)
    {
        ClearProxies();
        AItemContainer active = DV.InventorySystem.Inventory.Instance?.ItemContainerRegistry?.ActiveContainer;
        if (active == null) return;
        ObservableCollectionExt<InventorySlotDisplayData> model = new();
        Dictionary<int, ItemRow> rowBySlot = (view.Rows ?? Array.Empty<ItemRow>())
            .Where(row => row != null && row.Slot >= 0 && row.Slot < view.Capacity)
            .GroupBy(row => row.Slot).ToDictionary(group => group.Key, group => group.First());
        for (int slot = 0; slot < view.Capacity; slot++)
        {
            IInventoryItemSpec spec = null;
            if (rowBySlot.TryGetValue(slot, out ItemRow row))
            {
                IInventoryItemSpec source = Globals.G?.Items?.items?.FirstOrDefault(spec =>
                    spec != null && string.Equals(spec.ItemPrefabName, row.PrefabName,
                        StringComparison.Ordinal));
                GameObject proxyObject = new($"ColdContainerProxy:{row.Handle}");
                proxyObject.hideFlags = HideFlags.HideAndDontSave;
                ColdContainerItemProxy proxy = proxyObject.AddComponent<ColdContainerItemProxy>();
                proxy.Initialize(source, view, row);
                proxies.Add(proxyObject);
                spec = proxy;
            }
            model.Add(new InventorySlotDisplayData(spec, true, false, true));
        }
        foreach (ItemContainerProvider provider in UnityEngine.Object.FindObjectsOfType<ItemContainerProvider>())
        {
            if (provider.ActiveContainer != active) continue;
            provider.containerToInventoryModel[active] = model;
            provider.ActiveModel = model;
        }
    }

    private static void ClearProxies()
    {
        foreach (GameObject proxy in proxies)
            if (proxy != null) UnityEngine.Object.Destroy(proxy);
        proxies.Clear();
    }

    private static void Reset()
    {
        NetworkedItemManager.Cleared -= Reset;
        if (initialized && ItemClient.Current != null)
        {
            ItemClient.Current.ItemListReceived -= OnView;
            ItemClient.Current.ItemResultReceived -= OnMutation;
        }
        initialized = false;
        activeView = null;
        shellContainer = null;
        activeShellNetId = 0;
        requestedShellNetId = 0;
        pendingRequestId = 0;
        pendingMutations.Clear();
#if DEBUG
        mutationSequence = 0;
        lastMutation = null;
        viewSequence = 0;
        lastView = null;
#endif
        ClearProxies();
    }

    private static void PublishMutation(string eventName, uint requestId,
        Dictionary<string, object> data, DebugSeverity severity = DebugSeverity.Info)
    {
        string request = requestId == 0 ? string.Empty : "request:" + requestId;
        DebugRuntime.Publish("inventory", eventName,
            NetworkRole.DebugSide, severity, "ContainerOperation", request,
            correlationId: request, data: data);
    }
}
