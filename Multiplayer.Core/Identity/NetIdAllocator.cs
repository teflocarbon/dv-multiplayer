using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Identity;

/// <summary>
/// Collision-safe allocator for numeric-like value IDs. The caller supplies increment semantics
/// so the core remains independent of dynamic runtime binder and any specific integer width.
/// </summary>
public sealed class NetIdAllocator<T> where T : struct
{
    private readonly Func<T, T> increment;
    private readonly EqualityComparer<T> comparer = EqualityComparer<T>.Default;
    private readonly HashSet<T> live = new();
    private readonly HashSet<T> releasedSet = new();
    private readonly Queue<T> released = new();
    private T cursor;

    public NetIdAllocator(Func<T, T> increment)
    {
        this.increment = increment ?? throw new ArgumentNullException(nameof(increment));
    }

    public int LiveCount => live.Count;
    public bool IsLive(T id) => live.Contains(id);

    public bool TryAllocate(out T id)
    {
        // Prefer an ID that has never been issued in this allocator lifetime. Reusing a
        // recently released network ID while reliable packets for its previous object may
        // still be in flight can bind those packets to an unrelated replacement object.
        // Released IDs remain available as an exhaustion fallback for bounded ID spaces.
        T next = increment(cursor);
        while (!IsZero(next))
        {
            cursor = next;
            if (live.Add(next))
            {
                id = next;
                return true;
            }
            next = increment(cursor);
        }

        while (released.Count > 0)
        {
            T candidate = released.Dequeue();
            releasedSet.Remove(candidate);
            if (IsZero(candidate) || !live.Add(candidate)) continue;
            id = candidate;
            return true;
        }

        id = default;
        return false;
    }

    public bool TryReserve(T id)
    {
        if (IsZero(id) || !live.Add(id)) return false;
        // A previously released explicit ID must not remain eligible for allocation.
        releasedSet.Remove(id);
        return true;
    }

    public bool Release(T id)
    {
        if (IsZero(id) || !live.Remove(id) || !releasedSet.Add(id)) return false;
        released.Enqueue(id);
        return true;
    }

    public void Reset()
    {
        cursor = default;
        live.Clear();
        released.Clear();
        releasedSet.Clear();
    }

    private bool IsZero(T value) => comparer.Equals(value, default);
}
