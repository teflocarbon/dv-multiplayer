using System;
using System.Collections.Generic;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Shared.Items.Inventory;

namespace Multiplayer.Server.Items.Inventory;

internal static partial class PlayerItems
{
    // A stashed alarm clock counts down on the server's copy of its row too (D14, OI-COLD-12): its
    // holder's game rings it from its stand-in (StashedItems) and the server runs the same tick
    // (AlarmRows) on the row, so a clock made from the row later (a take-out, a rejoin) is the
    // one that rang. Only the rows of players here: a cold row's clock is as its holder left it,
    // and works out its due time again when it's next made, as vanilla's load does.
    private static readonly AlarmRows.Ticker<Guid> rowAlarms = new();

    /// <summary>Host, every frame: on each of the world's minutes, the alarms in the rows of players here tick.</summary>
    public static void UpdateAlarms()
    {
        if (!rowAlarms.NextMinute(out DateTime now))
            return;
        HashSet<Guid> live = new();
        foreach (LiveItems.LiveItem record in LiveItems.Records)
        {
            if (!IsDataOnly(record) || !AlarmRows.IsAlarmClock(record.PrefabName) ||
                !ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow row) || row.State == null)
                continue;
            live.Add(row.PersistentItemId);
            if (rowAlarms.Advance(row.PersistentItemId, row.State, now) == AlarmRows.Tick.Rang)
                Multiplayer.Log($"Inventory: the alarm clock {record.NetId} in slot {record.Slot} of {record.Holder} rang (its row)");
        }
        rowAlarms.KeepOnly(live);
    }
}
