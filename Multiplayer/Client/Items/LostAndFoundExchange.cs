using System.Collections.Generic;
using System.Linq;
using DV.InventorySystem;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Client.Items;

/// <summary>
///     The local player's Lost and Found requests and the server's answers, built and applied the
///     same way whichever session carries them: a remote client's <c>NetworkClient</c> sends and
///     receives them as packets, the host's <c>LocalPlayerSession</c> submits them in-process and
///     reads the answers from its <c>LocalPeer</c>.
/// </summary>
public static class LostAndFoundExchange
{
    /// <summary>A retrieval of a Lost and Found item into the local inventory, with the inventory as it is now.</summary>
    public static ServerboundLostItemRetrievePacket Retrieval(uint requestId, uint lostHandle,
        ushort currentNetId, uint expectedRevision, int requestedSlot)
    {
        Inventory inventory = Inventory.Instance;
        int capacity = inventory?.Capacity ?? 0;
        int existingItemSlot = -1;
        if (NetworkedItem.TryGet(currentNetId, out NetworkedItem existingItem) && existingItem != null)
            existingItemSlot = inventory?.IndexOf(existingItem.gameObject) ?? -1;
        List<int> occupied = new();
        for (int slot = 0; slot < capacity; slot++)
            if (inventory.PeekItemAtSlot(slot, true) != null)
                occupied.Add(slot);
        Multiplayer.Log($"[LostAndFound Recall] Preparing retrieval packet: request={requestId}, " +
            $"handle={lostHandle}, currentNetId={currentNetId}, " +
            $"revision={expectedRevision}, requestedSlot={requestedSlot}, existingSlot={existingItemSlot}, " +
            $"capacity={capacity}, occupied=[{string.Join(",", occupied)}]");
        return new ServerboundLostItemRetrievePacket
        {
            RequestId = requestId,
            LostHandle = lostHandle,
            ExpectedRevision = expectedRevision,
            RequestedSlot = requestedSlot,
            ExistingItemSlot = existingItemSlot,
            InventoryCapacity = capacity,
            OccupiedSlots = occupied.ToArray()
        };
    }

    /// <summary>The server's list of the local player's Lost and Found items: it becomes <see cref="LostAndFoundList"/>.</summary>
    public static void ApplySnapshot(ClientboundLostItemsSnapshotPacket packet)
    {
        int count = new[]
        {
            packet.Handles?.Length ?? 0,
            packet.NetIds?.Length ?? 0,
            packet.Revisions?.Length ?? 0,
            packet.PrefabNames?.Length ?? 0,
            packet.DisplayNames?.Length ?? 0,
            packet.Reasons?.Length ?? 0,
            packet.LostUtcTicks?.Length ?? 0
        }.Min();
        LostItemData[] items = new LostItemData[count];
        for (int i = 0; i < count; i++)
            items[i] = new LostItemData
            {
                Handle = packet.Handles[i],
                NetId = packet.NetIds[i],
                Revision = packet.Revisions[i],
                PrefabName = packet.PrefabNames[i],
                DisplayName = packet.DisplayNames[i],
                Reason = packet.Reasons[i],
                LostUtcTicks = packet.LostUtcTicks[i]
            };
        LostAndFoundList.Apply(items);
    }

    /// <summary>Logs and traces a retrieval's answer; the session then raises its event.</summary>
    public static void ReportRetrieveResult(ClientboundLostItemRetrieveResultPacket packet)
    {
        Multiplayer.Log($"[LostAndFound Recall] Lost item retrieval result received: request={packet.RequestId}, " +
            $"handle={packet.LostHandle}, accepted={packet.Accepted}, revision={packet.AuthorityRevision}, " +
            $"reason={packet.RejectionReason ?? string.Empty}");
        DebugRuntime.Publish("item", packet.Accepted ? "item.lost-and-found-recall-confirmed" :
            "item.lost-and-found-recall-rejected", DebugRuntimeSide.Client,
            packet.Accepted ? DebugSeverity.Info : DebugSeverity.Warning,
            "LostItem", packet.LostHandle.ToString(), new()
            {
                ["requestId"] = packet.RequestId,
                ["lostHandle"] = packet.LostHandle,
                ["authorityRevision"] = packet.AuthorityRevision,
                ["rejectionReason"] = packet.RejectionReason ?? string.Empty
            });
    }
}
