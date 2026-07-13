using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Multiplayer.Debugging.Protocol;

public sealed class DebugEventStore
{
    private readonly object gate = new();
    private readonly LinkedList<DebugEvent> events = new();
    private int capacity;
    private long sequence;

    public event Action<DebugEvent> Published;

    public DebugEventStore(int capacity) => this.capacity = Math.Max(100, capacity);

    public void Resize(int newCapacity)
    {
        lock (gate)
        {
            capacity = Math.Max(100, newCapacity);
            while (events.Count > capacity) events.RemoveFirst();
        }
    }

    public DebugEvent Publish(DebugEvent debugEvent)
    {
        if (debugEvent.SourceSequence == 0) debugEvent.SourceSequence = debugEvent.Sequence;
        if (string.IsNullOrEmpty(debugEvent.EventKey) && !string.IsNullOrEmpty(debugEvent.SessionId) && debugEvent.SourceSequence > 0)
            debugEvent.EventKey = $"{debugEvent.SessionId}:{debugEvent.SourceSequence}";
        debugEvent.Sequence = Interlocked.Increment(ref sequence);
        if (debugEvent.SourceSequence == 0) debugEvent.SourceSequence = debugEvent.Sequence;
        if (string.IsNullOrEmpty(debugEvent.EventKey)) debugEvent.EventKey = $"{debugEvent.SessionId}:{debugEvent.SourceSequence}";
        if (debugEvent.TimestampUtc == default) debugEvent.TimestampUtc = DateTime.UtcNow;
        lock (gate)
        {
            events.AddLast(debugEvent);
            while (events.Count > capacity)
            {
                LinkedListNode<DebugEvent> candidate = events.First;
                while (candidate != null && !candidate.Value.HighFrequency && candidate.Value.Severity > DebugSeverity.Trace)
                    candidate = candidate.Next;
                events.Remove(candidate ?? events.First);
            }
        }
        Published?.Invoke(debugEvent);
        return debugEvent;
    }

    public DebugEvent[] Snapshot() { lock (gate) return events.ToArray(); }
    public DebugEvent[] Since(DateTime cutoffUtc) { lock (gate) return events.Where(item => item.TimestampUtc >= cutoffUtc).ToArray(); }
    public bool TryGet(long eventSequence, out DebugEvent result)
    {
        lock (gate)
        {
            result = events.FirstOrDefault(item => item.Sequence == eventSequence);
            return result != null;
        }
    }

    public void Clear() { lock (gate) events.Clear(); }
}

public static class DebugPayloadFingerprint
{
    public static string Compute(byte[] buffer, int offset, int count)
    {
        if (buffer == null || count <= 0) return string.Empty;
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        int end = Math.Min(buffer.Length, offset + count);
        for (int index = Math.Max(0, offset); index < end; index++) { hash ^= buffer[index]; hash *= prime; }
        return hash.ToString("X16");
    }
}
