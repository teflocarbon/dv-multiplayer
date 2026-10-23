using System.Collections.Generic;
using System.Threading;
using Multiplayer.Client.Items.LostAndFound;
using Multiplayer.Client.Items.OwnedList;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Client.Items.Requests;

/// <summary>
///     The local player's item requests and the server's answers, numbered, built and applied the
///     same way whichever session carries them (docs/design/items/wire.md): a remote client's
///     <c>NetworkClient</c> sends and receives them as packets, the host's <c>LocalPlayerSession</c>
///     submits them in-process and reads the answers from its <c>LocalPeer</c>.
/// </summary>
public static class ItemExchange
{
    private static int lastRequestId;

    /// <summary>
    /// The next request id: counting up for as long as the game runs, so a new session never
    /// reuses an id the server could still remember from this one.
    /// </summary>
    public static uint NextRequestId()
    {
        uint id;
        do id = unchecked((uint)Interlocked.Increment(ref lastRequestId));
        while (id == 0);
        return id;
    }

    /// <summary>Numbers a request that has no id yet.</summary>
    public static ItemRequest Number(ItemRequest request)
    {
        if (request != null && request.RequestId == 0)
            request.RequestId = NextRequestId();
        return request;
    }

    /// <summary>
    /// A Lost and Found row into the local inventory: the row's handle and revision, the slot
    /// asked for (-1 for its claim slot or the first free one), and the inventory as it is now,
    /// until the server keeps it (R3).
    /// </summary>
    public static ItemRequest CollectLost(ItemRow row, int requestedSlot = -1)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        int capacity = inventory?.Capacity ?? 0;
        List<int> occupied = new();
        for (int slot = 0; slot < capacity; slot++)
            if (inventory.PeekItemAtSlot(slot, true) != null)
                occupied.Add(slot);
        global::Multiplayer.Multiplayer.Log($"[LostAndFound] Collecting row {row?.Handle ?? 0} (revision {row?.Revision ?? 0}) " +
            $"into slot {requestedSlot}: capacity={capacity}, occupied=[{string.Join(",", occupied)}]");
        return new ItemRequest
        {
            ItemHandle = row?.Handle ?? 0,
            ExpectedRevision = row?.Revision ?? 0,
            Action = ItemAction.CollectLost,
            Slot = requestedSlot,
            InventoryCapacity = capacity,
            OccupiedSlots = occupied.ToArray()
        };
    }

    /// <summary>A list the server sent: Lost and Found and owned items become this game's lists; the session then raises its event.</summary>
    public static void Receive(ClientboundItemListPacket packet)
    {
        if (packet == null)
            return;
        if (packet.Kind == ItemListKind.LostAndFound)
            LostAndFoundList.Apply(packet.Rows);
        else if (packet.Kind == ItemListKind.Owned)
            OwnedItemsList.Apply(packet);
    }

    /// <summary>Traces a request's answer; the session then raises its event.</summary>
    public static void Receive(ClientboundItemResultPacket packet)
    {
        if (packet == null)
            return;
        DebugRuntime.Publish("item", packet.Accepted ? "item.request-accepted" : "item.request-refused",
            DebugRuntimeSide.Client, packet.Accepted ? DebugSeverity.Info : DebugSeverity.Warning,
            "Item", packet.ItemNetId.ToString(), new()
            {
                ["requestId"] = packet.RequestId,
                ["refusal"] = packet.Refusal.ToString(),
                ["detail"] = packet.Detail ?? string.Empty,
                ["revision"] = packet.Revision
            });
    }
}
