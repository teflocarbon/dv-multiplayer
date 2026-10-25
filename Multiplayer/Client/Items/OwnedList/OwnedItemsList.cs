using System;
using System.Collections.Generic;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;

namespace Multiplayer.Client.Items.OwnedList;

/// <summary>One row of the local player's owned-items list.</summary>
public sealed class OwnedItemListRow
{
    public uint Handle;
    public string PrefabName = string.Empty, DisplayName = string.Empty, Where = string.Empty;
    public bool Retrievable;
}

/// <summary>
/// The local player's owned items as the server last sent them (owned-list.md, "The
/// page"): what the career manager's Owned Items page shows.
/// </summary>
public static class OwnedItemsList
{
    private static readonly List<OwnedItemListRow> rows = new();

    // The session's items are gone with the item manager: so is this list.
    static OwnedItemsList() => NetworkedItemManager.Cleared += Clear;

    public static event Action Changed;
    public static IReadOnlyList<OwnedItemListRow> Rows => rows;
    /// <summary>The request the last list answered.</summary>
    public static uint RequestId { get; private set; }

    public static void Apply(ClientboundItemListPacket packet)
    {
        rows.Clear();
        RequestId = packet?.RequestId ?? 0;
        foreach (ItemRow row in packet?.Rows ?? Array.Empty<ItemRow>())
            if (row != null)
                rows.Add(new OwnedItemListRow
                {
                    Handle = row.Handle,
                    PrefabName = row.PrefabName ?? string.Empty,
                    DisplayName = row.DisplayName ?? string.Empty,
                    Where = row.Where ?? string.Empty,
                    Retrievable = (row.Flags & ItemRowFlags.Retrievable) != 0
                });
        Changed?.Invoke();
    }

    public static void Clear()
    {
        rows.Clear();
        Changed?.Invoke();
    }
}
