using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Shared.Debugging;
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
    private static readonly List<LostItemData> items = new();

    // The session's items are gone with the item manager: so is this list.
    static LostAndFoundList() => NetworkedItemManager.Cleared += Clear;

    public static event Action Changed;
    public static IReadOnlyList<LostItemData> Items => items;

    public static void Apply(LostItemData[] received)
    {
        items.Clear();
        if (received != null)
            items.AddRange(received.Where(item => item != null));
        Changed?.Invoke();
    }

    public static void Clear()
    {
        items.Clear();
        Changed?.Invoke();
    }

    /// <summary>The list's rows for the in-game observability overlay, on a game with a client.</summary>
    public static IEnumerable<Dictionary<string, object>> DebugSnapshot()
    {
        if (NetworkLifecycle.Instance?.LocalSession == null)
            yield break;
        foreach (LostItemData item in items)
        {
            yield return new Dictionary<string, object>
            {
                ["debugKey"] = $"client-handle:{item.Handle}",
                ["source"] = "client-authoritative-list",
                ["status"] = "ListedByHost",
                ["lostHandle"] = item.Handle,
                ["netId"] = item.NetId,
                ["revision"] = item.Revision,
                ["prefabName"] = item.PrefabName ?? string.Empty,
                ["displayName"] = item.DisplayName ?? string.Empty,
                ["ownerPlayerId"] = DebugRuntime.Session?.PlayerId ?? 0,
                ["reason"] = ((LostItemReason)item.Reason).ToString(),
                ["lostUtc"] = FormatUtc(item.LostUtcTicks)
            };
        }
    }

    private static string FormatUtc(long ticks)
    {
        try { return new DateTime(ticks, DateTimeKind.Utc).ToString("O"); }
        catch { return $"invalid-ticks:{ticks}"; }
    }
}
