using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Items.Inventory;

internal static partial class PlayerItems
{
    // A stashed alarm clock counts down on the server's copy of its row too (D14, OI-COLD-12): its
    // holder's game rings it from its stand-in (StashedItems) and the server runs the same tick
    // (AlarmRows) on the row, so a clock made from the row later (a take-out, a rejoin) is the
    // one that rang. Only the rows of players here: a cold row's clock is as its holder left it,
    // and works out its due time again when it's next made, as vanilla's load does. As it rings
    // or turns, the players near its holder hear it at the holder's avatar (HeardStashedSounds),
    // as they hear a stashed boombox.
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
            AlarmRows.Tick tick = rowAlarms.Advance(row.PersistentItemId, row.State, now);
            if (tick == AlarmRows.Tick.Rang)
                Multiplayer.Log($"Inventory: the alarm clock {record.NetId} in slot {record.Slot} of {record.Holder} rang (its row)");
            if (tick != AlarmRows.Tick.None)
                AlarmHeard(record, tick == AlarmRows.Tick.Rang ? ClientboundStashedSoundPacket.AlarmRang : ClientboundStashedSoundPacket.AlarmTurned);
        }
        rowAlarms.KeepOnly(live);
    }

    // The players near the clock's holder hear it once, where the holder is.
    private static void AlarmHeard(LiveItems.LiveItem record, byte once)
    {
        NetworkServer server = NetworkServer.Instance;
        ServerPlayer holder = server?.ServerPlayers.FirstOrDefault(player => player.Guid == record.Holder);
        if (holder == null)
            return;
        float range = HearingOf(record.PrefabName);
        foreach (ServerPlayer other in server.ServerPlayers)
            if (other != holder && other.LoadingState >= PlayerLoadingState.ReadyForItems &&
                (other.WorldPosition - holder.WorldPosition).sqrMagnitude <= range * range)
                server.SendStashedSound(other, new ClientboundStashedSoundPacket
                {
                    HolderId = holder.PlayerId,
                    NetId = record.NetId,
                    PrefabName = record.PrefabName,
                    Once = once
                });
    }
}
