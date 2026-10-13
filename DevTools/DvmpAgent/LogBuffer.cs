using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dvmp.Agent;

/// <summary>Bounded ring buffer of Unity log messages (which includes Multiplayer's log output).</summary>
internal static class LogBuffer
{
    private const int Capacity = 5000;
    private const int MaxStackLength = 4000;

    internal sealed class Entry
    {
        public long Seq;
        public float Time;
        public string Level;
        public string Message;
        public string Stack;
    }

    private static readonly object gate = new();
    private static readonly Queue<Entry> entries = new();
    private static long nextSeq = 1;
    private static readonly DateTime start = DateTime.UtcNow;

    public static void Install() => Application.logMessageReceivedThreaded += OnLog;

    private static void OnLog(string message, string stack, LogType type)
    {
        string level = type switch
        {
            LogType.Error or LogType.Assert => "error",
            LogType.Exception => "exception",
            LogType.Warning => "warning",
            _ => "log"
        };
        bool keepStack = type is LogType.Exception or LogType.Error or LogType.Assert;
        Entry entry = new()
        {
            Time = (float)(DateTime.UtcNow - start).TotalSeconds,
            Level = level,
            Message = message,
            Stack = keepStack && !string.IsNullOrEmpty(stack)
                ? stack.Length > MaxStackLength ? stack.Substring(0, MaxStackLength) : stack
                : null
        };
        lock (gate)
        {
            entry.Seq = nextSeq++;
            entries.Enqueue(entry);
            while (entries.Count > Capacity) entries.Dequeue();
        }
    }

    public static (List<Entry> Items, long LastSeq, long Dropped) Query(long sinceSeq, string minLevel, string contains, int max)
    {
        int minRank = Rank(minLevel);
        List<Entry> result = new();
        long last, oldest;
        lock (gate)
        {
            last = nextSeq - 1;
            oldest = entries.Count > 0 ? entries.Peek().Seq : nextSeq;
            foreach (Entry entry in entries)
            {
                if (entry.Seq <= sinceSeq || Rank(entry.Level) < minRank) continue;
                if (!string.IsNullOrEmpty(contains) &&
                    entry.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(entry);
            }
        }
        // Keep the newest entries when over the limit.
        if (result.Count > max) result.RemoveRange(0, result.Count - max);
        long dropped = sinceSeq > 0 && oldest > sinceSeq + 1 ? oldest - sinceSeq - 1 : 0;
        return (result, last, dropped);
    }

    private static int Rank(string level) => level switch
    {
        "exception" => 3,
        "error" => 2,
        "warning" => 1,
        _ => 0
    };
}
