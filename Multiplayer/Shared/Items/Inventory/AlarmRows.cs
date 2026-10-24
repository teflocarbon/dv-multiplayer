using System;
using System.Collections.Generic;
using DV.JObjectExtstensions;
using DV.TimeKeeping;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// An alarm clock kept as a row (D14, OI-COLD-12): vanilla's <c>AnalogAlarmClock.AlarmTick</c> run
/// on the row's own state (<c>alarmTime</c>, <c>alarmSet</c>), so a stashed alarm counts down and
/// goes off as one in a pocket does. The holder's game runs it on its stand-in and sounds it
/// (<see cref="StashedItems"/>); the server runs it on its copy of the row, so both have the same
/// state when the clock is next made. Its due time is worked out the first time a row is ticked, as
/// vanilla's load does, from the minute before that tick.
/// </summary>
internal static class AlarmRows
{
    private const string AlarmTimeKey = "alarmTime";
    private const string AlarmSetKey = "alarmSet";
    // AnalogAlarmClock's own limits: 12 hours a turn, at most 4308 minutes ahead.
    private const int MinutesPerTurn = 720;
    private const int MaxAlarmMinutes = 4308;

    internal enum Tick
    {
        None,
        /// <summary>A 12-hour turn of its increment passed: its <c>alarmTime</c> went down by a turn.</summary>
        Turned,
        /// <summary>It went off: it's no longer set.</summary>
        Rang
    }

    private static readonly Dictionary<string, bool> alarmPrefabs = new();

    /// <summary>Whether items of this prefab are alarm clocks.</summary>
    public static bool IsAlarmClock(string prefabName)
    {
        if (string.IsNullOrEmpty(prefabName))
            return false;
        if (!alarmPrefabs.TryGetValue(prefabName, out bool alarm))
        {
            InventoryItemSpec spec = InventoryStandIn.FindSpec(prefabName);
            alarm = spec != null && spec.GetComponentInChildren<AnalogAlarmClock>(true) != null;
            alarmPrefabs[prefabName] = alarm;
        }
        return alarm;
    }

    /// <summary>
    /// One minute of the world's clock on an alarm clock's state: as <c>AlarmTick</c>, a turn of
    /// its increment passing lowers <c>alarmTime</c> by 12 hours, and reaching the due time unsets it.
    /// <paramref name="due"/> is the row's due time, kept by the caller between ticks; a row not
    /// ticked before works it out from <paramref name="since"/>, the minute before this tick, so a
    /// time skip right after a stash (sleep, /time) rings it as vanilla's clock would.
    /// </summary>
    public static Tick Advance(JObject state, DateTime since, DateTime now, ref DateTime? due)
    {
        if (state == null || state.GetBool(AlarmSetKey) != true)
        {
            due = null;
            return Tick.None;
        }
        int minutes = state.GetInt(AlarmTimeKey) ?? 0;
        if (minutes <= -MinutesPerTurn)
            minutes = 0;
        else if (minutes > MaxAlarmMinutes)
            minutes = 3600;
        int usable = minutes < 0 ? MinutesPerTurn + minutes : minutes;
        due ??= DueTime(usable, since);
        TimeSpan left = due.Value - now;
        Tick tick = Tick.None;
        if (Math.Max(0, usable / MinutesPerTurn) > Mathf.FloorToInt((float)left.TotalHours) / 12)
        {
            state.SetInt(AlarmTimeKey, minutes - MinutesPerTurn);
            tick = Tick.Turned;
        }
        if (!(left.TotalMinutes > 0.0))
        {
            state.SetBool(AlarmSetKey, false);
            due = null;
            tick = Tick.Rang;
        }
        return tick;
    }

    /// <summary>
    /// When an alarm set to <paramref name="alarmMinutes"/> on its face goes off, seen at
    /// <paramref name="from"/>: vanilla's <c>WorldClockController.CalculateAlarmTime</c>, which
    /// reads the time now, from a given time.
    /// </summary>
    public static DateTime DueTime(int alarmMinutes, DateTime from)
    {
        int hour = alarmMinutes / 60 % 12 + (from.Hour >= 12 ? 12 : 0);
        DateTime due = new(from.Year, from.Month, from.Day, hour, alarmMinutes % 60, 0);
        int turns = alarmMinutes / MinutesPerTurn;
        if ((due - from).TotalMinutes < 0.0)
            turns++;
        return due.AddMinutes(turns * MinutesPerTurn);
    }

    /// <summary>The world's time now, or null before its clock runs.</summary>
    public static DateTime? Now
    {
        get
        {
            WorldClockController clock = WorldClockController.Instance;
            if (clock == null)
                return null;
            (bool valid, _, _, DateTime time) = clock.GetCurrentAnglesAndTimeOfDay();
            return valid ? time : null;
        }
    }

    /// <summary>
    /// Ticks rows once per minute of the world's clock, as vanilla's clocks tick on its minute (a
    /// time jump is one tick): each caller keeps its own, polled every frame.
    /// </summary>
    internal sealed class Ticker<TKey>
    {
        private readonly Dictionary<TKey, DateTime?> due = new();
        private DateTime lastMinute;
        private DateTime since;

        /// <summary>The world's time now, if its minute moved on since the last call.</summary>
        public bool NextMinute(out DateTime now)
        {
            now = default;
            if (Now is not { } time)
                return false;
            DateTime minute = time.AddSeconds(-time.Second).AddMilliseconds(-time.Millisecond);
            if (minute == lastMinute)
                return false;
            // The minute before this tick; the first tick has none but its own.
            since = lastMinute == default ? time : lastMinute;
            lastMinute = minute;
            now = time;
            return true;
        }

        public Tick Advance(TKey key, JObject state, DateTime now)
        {
            due.TryGetValue(key, out DateTime? kept);
            Tick tick = AlarmRows.Advance(state, since, now, ref kept);
            if (kept.HasValue)
                due[key] = kept;
            else
                due.Remove(key);
            return tick;
        }

        /// <summary>Forgets the rows not in <paramref name="live"/>: a row seen again works out its due time anew.</summary>
        public void KeepOnly(ICollection<TKey> live)
        {
            List<TKey> gone = null;
            foreach (TKey key in due.Keys)
                if (!live.Contains(key))
                    (gone ??= new List<TKey>()).Add(key);
            if (gone != null)
                foreach (TKey key in gone)
                    due.Remove(key);
        }
    }
}
