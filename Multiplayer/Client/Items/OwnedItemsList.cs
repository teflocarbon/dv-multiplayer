using System;
using System.Collections.Generic;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound;

namespace Multiplayer.Client.Items;

/// <summary>One row of the local player's owned-items list.</summary>
public sealed class OwnedItemListRow
{
    public uint Handle;
    public string PrefabName = string.Empty, DisplayName = string.Empty, Where = string.Empty;
    public bool Retrievable;
}

/// <summary>
/// The local player's owned items as the server last sent them (owned-items.md, "The owned-items
/// list"): what the career manager's Owned Items page shows.
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

    public static void Apply(ClientboundOwnedItemsPacket packet)
    {
        rows.Clear();
        RequestId = packet?.RequestId ?? 0;
        int count = packet?.Handles?.Length ?? 0;
        for (int i = 0; i < count; i++)
            rows.Add(new OwnedItemListRow
            {
                Handle = packet.Handles[i],
                PrefabName = At(packet.PrefabNames, i),
                DisplayName = At(packet.DisplayNames, i),
                Where = At(packet.Wheres, i),
                Retrievable = packet.Retrievable != null && i < packet.Retrievable.Length && packet.Retrievable[i]
            });
        Changed?.Invoke();
    }

    public static void Clear()
    {
        rows.Clear();
        Changed?.Invoke();
    }

    private static string At(string[] values, int i) => values != null && i < values.Length ? values[i] ?? string.Empty : string.Empty;
}
