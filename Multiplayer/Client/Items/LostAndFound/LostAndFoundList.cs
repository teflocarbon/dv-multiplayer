using System;
using System.Collections.Generic;
using System.Linq;

using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Items.LostAndFound;

/// <summary>
///     The local player's Lost and Found as the server last sent it: what the career manager's
///     screen lists and the inventory's Get button retrieves. The server's own Lost and Found is
///     <c>LostAndFoundServer</c>, which (like the overlay) reads this list's rows through
///     <see cref="IItemClient.LostAndFoundDebugSnapshot"/>.
/// </summary>
public static class LostAndFoundList
{
    private static readonly List<ItemRow> items = new();

    // The session's items are gone with the item manager: so is this list.
    static LostAndFoundList() => NetworkedItemManager.Cleared += Clear;

    public static event Action Changed;
    /// <summary>The rows, oldest first: each a handle, its revision, why it's there and, for a lost essential, its claim slot and the NetId its silhouette is kept by.</summary>
    public static IReadOnlyList<ItemRow> Items => items;

    public static void Apply(ItemRow[] received)
    {
        items.Clear();
        if (received != null)
            items.AddRange(received.Where(item => item != null));
        // A lost essential keeps its silhouette, made from its row (inventory.md, "Silhouettes").
        LostAndFoundSilhouettes.Apply(items);
        Changed?.Invoke();
    }

    public static void Clear()
    {
        items.Clear();
        LostAndFoundSilhouettes.Clear();
        Changed?.Invoke();
    }

    /// <summary>The list's rows for the in-game observability overlay, on a game with a client.</summary>
    public static IEnumerable<Dictionary<string, object>> DebugSnapshot()
    {
        if (NetworkLifecycle.Instance?.LocalSession == null)
            yield break;
        foreach (ItemRow item in items)
        {
            yield return new Dictionary<string, object>
            {
                ["debugKey"] = $"client-handle:{item.Handle}",
                ["source"] = "client-authoritative-list",
                ["status"] = "ListedByHost",
                ["lostHandle"] = item.Handle,
                ["revision"] = item.Revision,
                ["prefabName"] = item.PrefabName ?? string.Empty,
                ["displayName"] = item.DisplayName ?? string.Empty,
                ["ownerPlayerId"] = DebugRuntime.Session?.PlayerId ?? 0,
                ["reason"] = item.Where ?? string.Empty,
                ["claimSlot"] = item.ClaimSlot
            };
        }
    }
}
