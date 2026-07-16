using System;
using System.Collections.Generic;
using System.Linq;
using DV.Common;
using DV.CabControls;
using DV;
using DV.InventorySystem;
using DV.UI.Inventory;
using DV.Util;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Core.Containers;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Serverbound;
using UnityEngine;

namespace Multiplayer.Integrations.Inventory;

internal sealed class ColdContainerItemProxy : MonoBehaviour, IInventoryItemSpec
{
    private IInventoryItemSpec source;
    public uint ContainerHandle { get; private set; }
    public uint ItemHandle { get; private set; }
    public int Slot { get; private set; }
    public uint ChildContainerHandle { get; private set; }
    public bool ForeignOwned { get; private set; }

    public void Initialize(IInventoryItemSpec sourceSpec, ClientboundContainerViewPacket view, int row)
    {
        source = sourceSpec;
        ContainerHandle = view.ContainerHandle;
        ItemHandle = view.ItemHandles[row];
        Slot = view.Slots[row];
        ChildContainerHandle = view.ChildContainerHandles[row];
        ForeignOwned = view.ForeignOwned[row];
        name = $"ColdContainerProxy:{view.PrefabNames[row]}:{ItemHandle}";
        if (ChildContainerHandle != 0)
            gameObject.AddComponent<ColdProxyContainer>().Initialize(ChildContainerHandle,
                source?.LocalizedName ?? view.DisplayNames[row]);
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
    protected override string ContainerName { get; set; } = "ColdProxy";
    public override string ContainerNameLocalized { get => localizedName; protected set => localizedName = value; }
    public override bool DirectInteractionAllowed => true;

    protected override void Awake() { }
    public void Initialize(uint containerHandle, string displayName)
    {
        handle = containerHandle;
        localizedName = displayName ?? string.Empty;
    }
    public override bool ValidItem(GameObject item) => false;
    public override void Clear() { }
    protected override bool InitializeSpecific() => true;
    public override void PlayInteractionEffects(bool open) { }
    public override void ToggleContainerAccess() => ColdContainerUiIntegration.OpenChild(handle);
}

/// <summary>Detached-row adapter for Derail Valley's existing non-VR and VR inventory views.</summary>
internal static class ColdContainerUiIntegration
{
    private static readonly List<GameObject> proxies = new();
    private static ClientboundContainerViewPacket activeView;
    private static ushort activeShellNetId;
    private static ushort requestedShellNetId;
    private static uint pendingRequestId;
    private static uint requestId;
    private static bool initialized;
#if DEBUG
    private static uint mutationSequence;
    private static ClientboundContainerMutationResultPacket lastMutation;
    private static uint viewSequence;
    private static ClientboundContainerViewPacket lastView;
#endif

    public static bool Active => activeView?.Accepted == true;
    public static uint ActiveHandle => activeView?.ContainerHandle ?? 0;
    public static uint ActiveRevision => activeView?.Revision ?? 0;
#if DEBUG
    public static int ActiveCapacity => activeView?.Capacity ?? 0;
    public static int ActiveRowCount => activeView?.Slots?.Length ?? 0;
    public static int[] ActiveSlots => activeView?.Slots?.ToArray() ?? Array.Empty<int>();
    public static uint[] ActiveChildContainerHandles =>
        activeView?.ChildContainerHandles?.ToArray() ?? Array.Empty<uint>();
    public static uint MutationSequence => mutationSequence;
    public static ClientboundContainerMutationResultPacket LastMutation => lastMutation;
    public static uint ViewSequence => viewSequence;
    public static ClientboundContainerViewPacket LastView => lastView;
#endif

    public static void Open(AItemContainer container)
    {
        if (container == null || NetworkLifecycle.Instance?.Client == null ||
            !NetworkedItem.TryGetNetworkedItem(container.GetComponent<ItemBase>(), out NetworkedItem shell) ||
            shell.NetId == 0)
            return;
        EnsureInitialized();
        requestedShellNetId = shell.NetId;
        activeShellNetId = 0;
        activeView = null;
        pendingRequestId = ++requestId;
        global::Multiplayer.Multiplayer.Log($"[Cold Container] Browse requested: " +
            $"request={pendingRequestId}, shellNetId={requestedShellNetId}, " +
            $"containerId={container.ContainerId}");
        NetworkLifecycle.Instance.Client.RequestContainerView(pendingRequestId,
            requestedShellNetId);
    }

    public static void OpenChild(uint containerHandle)
    {
        if (containerHandle == 0 || NetworkLifecycle.Instance?.Client == null) return;
        EnsureInitialized();
        pendingRequestId = ++requestId;
        NetworkLifecycle.Instance.Client.RequestContainerView(pendingRequestId, 0,
            containerHandle);
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
        ServerboundContainerMutationPacket packet = NewMutation(ContainerOperationKind.Deposit);
        packet.DestinationContainerHandle = ActiveHandle;
        packet.ExpectedDestinationRevision = ActiveRevision;
        packet.DestinationSlot = slot;
        ColdContainerItemProxy proxy = item.GetComponent<ColdContainerItemProxy>();
        if (proxy != null)
        {
            packet.Kind = (byte)ContainerOperationKind.Move;
            packet.SourceContainerHandle = proxy.ContainerHandle;
            packet.ExpectedSourceRevision = ActiveRevision;
            packet.SourceSlot = proxy.Slot;
        }
        else
        {
            packet.ShellNetId = activeShellNetId;
            packet.ItemNetId = item.GetComponent<NetworkedItem>()?.NetId ?? 0;
        }
        global::Multiplayer.Multiplayer.Log($"[Cold Container] Deposit requested: " +
            $"request={packet.RequestId}, shellNetId={packet.ShellNetId}, " +
            $"containerHandle={packet.DestinationContainerHandle}, itemNetId={packet.ItemNetId}, " +
            $"slot={packet.DestinationSlot}, requestedSlot={requestedSlot}, " +
            $"revision={packet.ExpectedDestinationRevision}");
        PublishMutation("container.mutation-requested", packet.OperationId, new()
        {
            ["requestId"] = packet.RequestId,
            ["kind"] = ((ContainerOperationKind)packet.Kind).ToString(),
            ["shellNetId"] = packet.ShellNetId,
            ["containerHandle"] = packet.DestinationContainerHandle,
            ["itemNetId"] = packet.ItemNetId,
            ["sourceSlot"] = packet.SourceSlot,
            ["destinationSlot"] = packet.DestinationSlot
        });
        NetworkLifecycle.Instance.Client.RequestContainerMutation(packet);
        return true;
    }

    private static int ResolveFreeDestinationSlot(int requestedSlot)
    {
        if (activeView?.Accepted != true || activeView.Capacity <= 0)
            return -1;
        HashSet<int> occupied = new(activeView.Slots ?? Array.Empty<int>());
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
        ServerboundContainerMutationPacket packet = NewMutation(ContainerOperationKind.Withdraw);
        packet.SourceContainerHandle = ActiveHandle;
        packet.ExpectedSourceRevision = ActiveRevision;
        packet.SourceSlot = slot;
        packet.DestinationSlot = destinationInventorySlot;
        global::Multiplayer.Multiplayer.Log($"[Cold Container] Withdrawal requested: " +
            $"request={packet.RequestId}, containerHandle={packet.SourceContainerHandle}, " +
            $"sourceSlot={packet.SourceSlot}, inventorySlot={packet.DestinationSlot}, " +
            $"revision={packet.ExpectedSourceRevision}");
        PublishMutation("container.mutation-requested", packet.OperationId, new()
        {
            ["requestId"] = packet.RequestId,
            ["kind"] = ((ContainerOperationKind)packet.Kind).ToString(),
            ["containerHandle"] = packet.SourceContainerHandle,
            ["sourceSlot"] = packet.SourceSlot,
            ["destinationSlot"] = packet.DestinationSlot
        });
        NetworkLifecycle.Instance.Client.RequestContainerMutation(packet);
        return true;
    }

    public static bool TryInterceptRemove(AItemContainer source, int slot) =>
        TryInterceptRemove(source, slot,
            DV.InventorySystem.Inventory.Instance?.GetFirstFreeSlot() ?? -1);

    public static bool TryInterceptMove(AItemContainer source, int from, int to)
    {
        if (!IsActive(source)) return false;
        ServerboundContainerMutationPacket packet = NewMutation(ContainerOperationKind.Move);
        packet.SourceContainerHandle = ActiveHandle;
        packet.DestinationContainerHandle = ActiveHandle;
        packet.ExpectedSourceRevision = ActiveRevision;
        packet.ExpectedDestinationRevision = ActiveRevision;
        packet.SourceSlot = from;
        packet.DestinationSlot = to;
        NetworkLifecycle.Instance.Client.RequestContainerMutation(packet);
        return true;
    }

    private static bool IsActive(AItemContainer container) => container != null &&
        DV.InventorySystem.Inventory.Instance?.ItemContainerRegistry?.ActiveContainer == container &&
        activeShellNetId != 0;

    private static ServerboundContainerMutationPacket NewMutation(ContainerOperationKind kind) => new()
    {
        RequestId = ++requestId,
        OperationId = Guid.NewGuid().ToByteArray(),
        Kind = (byte)kind
    };

    private static void EnsureInitialized()
    {
        if (initialized || NetworkLifecycle.Instance?.Client == null) return;
        initialized = true;
        NetworkLifecycle.Instance.Client.ContainerViewReceived += OnView;
        NetworkLifecycle.Instance.Client.ContainerMutationCompleted += OnMutation;
    }

    private static void OnMutation(ClientboundContainerMutationResultPacket result)
    {
#if DEBUG
        lastMutation = result;
        mutationSequence++;
#endif
        global::Multiplayer.Multiplayer.Log($"[Cold Container] Mutation completed: " +
            $"request={result?.RequestId ?? 0}, accepted={result?.Accepted == true}, " +
            $"status={result?.Status ?? 0}, reason={result?.RejectionReason ?? string.Empty}, " +
            $"sourceRevision={result?.SourceRevision ?? 0}, " +
            $"destinationRevision={result?.DestinationRevision ?? 0}");
        if (result != null)
            PublishMutation("container.mutation-completed", result.OperationId, new()
            {
                ["requestId"] = result.RequestId,
                ["kind"] = ((ContainerOperationKind)result.Kind).ToString(),
                ["accepted"] = result.Accepted,
                ["status"] = ((ContainerOperationStatus)result.Status).ToString(),
                ["reason"] = result.RejectionReason ?? string.Empty,
                ["sourceRevision"] = result.SourceRevision,
                ["destinationRevision"] = result.DestinationRevision,
                ["materializedItemNetId"] = result.MaterializedItemNetId
            }, result.Accepted ? DebugSeverity.Info : DebugSeverity.Warning);
        if (activeShellNetId == 0) return;
        pendingRequestId = ++requestId;
        NetworkLifecycle.Instance.Client.RequestContainerView(pendingRequestId, 0,
            ActiveHandle);
    }

    private static void OnView(ClientboundContainerViewPacket view)
    {
#if DEBUG
        lastView = view;
        viewSequence++;
#endif
        if (view == null || view.RequestId != pendingRequestId)
        {
            global::Multiplayer.Multiplayer.Log($"[Cold Container] Browse response ignored: " +
                $"response={view?.RequestId ?? 0}, pending={pendingRequestId}");
            return;
        }
        global::Multiplayer.Multiplayer.Log($"[Cold Container] Browse response: " +
            $"request={view.RequestId}, accepted={view.Accepted}, " +
            $"reason={view.RejectionReason ?? string.Empty}, handle={view.ContainerHandle}, " +
            $"revision={view.Revision}, rows={view.Slots?.Length ?? 0}");
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

    private static void Project(ClientboundContainerViewPacket view)
    {
        ClearProxies();
        AItemContainer active = DV.InventorySystem.Inventory.Instance?.ItemContainerRegistry?.ActiveContainer;
        if (active == null) return;
        ObservableCollectionExt<InventorySlotDisplayData> model = new();
        int rows = new[]
        {
            view.Slots?.Length ?? 0,
            view.ItemHandles?.Length ?? 0,
            view.PrefabNames?.Length ?? 0,
            view.DisplayNames?.Length ?? 0,
            view.ForeignOwned?.Length ?? 0,
            view.ChildContainerHandles?.Length ?? 0
        }.Min();
        Dictionary<int, int> rowBySlot = Enumerable.Range(0, rows)
            .Where(row => view.Slots[row] >= 0 && view.Slots[row] < view.Capacity)
            .GroupBy(row => view.Slots[row]).ToDictionary(group => group.Key,
                group => group.First());
        for (int slot = 0; slot < view.Capacity; slot++)
        {
            IInventoryItemSpec spec = null;
            if (rowBySlot.TryGetValue(slot, out int row))
            {
                IInventoryItemSpec source = Globals.G?.Items?.items?.FirstOrDefault(spec =>
                    spec != null && string.Equals(spec.ItemPrefabName, view.PrefabNames[row],
                        StringComparison.Ordinal));
                GameObject proxyObject = new($"ColdContainerProxy:{view.ItemHandles[row]}");
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

    public static void Reset()
    {
        if (initialized && NetworkLifecycle.Instance?.Client != null)
        {
            NetworkLifecycle.Instance.Client.ContainerViewReceived -= OnView;
            NetworkLifecycle.Instance.Client.ContainerMutationCompleted -= OnMutation;
        }
        initialized = false;
        activeView = null;
        activeShellNetId = 0;
        requestedShellNetId = 0;
        pendingRequestId = 0;
#if DEBUG
        mutationSequence = 0;
        lastMutation = null;
        viewSequence = 0;
        lastView = null;
#endif
        ClearProxies();
    }

    private static void PublishMutation(string eventName, byte[] operationId,
        Dictionary<string, object> data, DebugSeverity severity = DebugSeverity.Info)
    {
        Guid operation = operationId?.Length == 16 ? new Guid(operationId) : Guid.Empty;
        DebugRuntime.Publish("inventory", eventName,
            NetworkLifecycle.Instance?.IsHost() == true ? DebugRuntimeSide.Server :
                DebugRuntimeSide.Client, severity, "ContainerOperation",
            operation == Guid.Empty ? string.Empty : operation.ToString("D"),
            correlationId: operation == Guid.Empty ? string.Empty : operation.ToString("D"),
            data: data);
    }
}
