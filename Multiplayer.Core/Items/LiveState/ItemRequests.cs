using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Items.LiveState;

/// <summary>
/// Why the server refused an item request (docs/design/items/wire.md, "Refusals"). Byte-sized on
/// the wire and append-only: a result carries one of these and the refusal's detail string.
/// </summary>
public enum ItemRefusal : byte
{
    /// <summary>Not refused: the request was carried out.</summary>
    None,
    /// <summary>Refused for a reason the detail names.</summary>
    Refused,
    /// <summary>The item, row or container isn't known (any more).</summary>
    UnknownItem,
    /// <summary>It changed since the revision the request expected.</summary>
    StaleRevision,
    /// <summary>It's someone else's, or the requester may not do this to it.</summary>
    NotYours,
    /// <summary>Something else is happening to it: try again.</summary>
    Busy,
    /// <summary>The request itself makes no sense (bad arguments, an unknown action).</summary>
    Invalid,
    /// <summary>There's no room where it would go.</summary>
    NoRoom,
    /// <summary>The requester can't reach it, or hasn't been shown it.</summary>
    NotHere,
    /// <summary>That kind of item can't do this.</summary>
    NotAllowed,
    /// <summary>The request id is older than any the server still remembers.</summary>
    Expired
}

public static class ItemRefusals
{
    /// <summary>
    /// The code for a refusal's detail, the reason string the server's code gives. The detail
    /// stays the precise reason; the code is what a client can act on.
    /// </summary>
    public static ItemRefusal Classify(string detail)
    {
        if (string.IsNullOrEmpty(detail))
            return ItemRefusal.Refused;
        if (detail.Contains("busy") || detail.Contains("pending") || detail.Contains("already-active"))
            return ItemRefusal.Busy;
        if (detail.Contains("unknown") || detail.Contains("nothing-to-") || detail.Contains("missing") ||
            detail.Contains("not-in-lost-and-found") || detail.Contains("unavailable"))
            return ItemRefusal.UnknownItem;
        if (detail.Contains("revision") || detail.Contains("stale") || detail.Contains("registry-changed"))
            return ItemRefusal.StaleRevision;
        if (detail.Contains("owner") || detail.Contains("not-your") || detail.Contains("with-a-player") ||
            detail.Contains("not-current-possessor") || detail.Contains("foreign") ||
            detail.Contains("from-player"))
            return ItemRefusal.NotYours;
        if (detail.Contains("full") || detail.Contains("no-free") || detail.Contains("capacity") ||
            detail.Contains("slot-occupied"))
            return ItemRefusal.NoRoom;
        if (detail.Contains("not-projected") || detail.Contains("distant") || detail.Contains("outside") ||
            detail.Contains("range"))
            return ItemRefusal.NotHere;
        if (detail.Contains("invalid") || detail.Contains("kind"))
            return ItemRefusal.Invalid;
        if (detail.Contains("not-recallable") || detail.Contains("not-retrievable") || detail.Contains("fixture") ||
            detail.Contains("not-allowed") || detail.Contains("not-permitted") || detail.Contains("essential-not"))
            return ItemRefusal.NotAllowed;
        return ItemRefusal.Refused;
    }
}

public enum ItemRequestStart : byte
{
    /// <summary>A request not seen before: carry it out, then <see cref="ItemRequestMemory{TResult}.Complete"/>.</summary>
    New,
    /// <summary>A repeat of a request still being carried out: its one answer is on its way.</summary>
    Pending,
    /// <summary>A repeat of a finished request: send its remembered answer, do nothing else.</summary>
    Repeat,
    /// <summary>Older than every request still remembered: refuse it, it may have run already.</summary>
    Expired
}

/// <summary>
/// One player's recent item requests and their answers, so a repeated <c>RequestId</c> gets the
/// same answer and does nothing twice (F21, OI-CON-07; docs/design/items/wire.md). A client counts
/// its request ids up from 1; id 0 is never remembered. The oldest answers are forgotten past
/// <see cref="Capacity"/>, and any id at or below the newest forgotten one is
/// <see cref="ItemRequestStart.Expired"/>.
/// </summary>
public sealed class ItemRequestMemory<TResult>
{
    public const int DefaultCapacity = 256;

    private readonly Dictionary<uint, (bool done, TResult result)> requests = new();
    private readonly Queue<uint> order = new();
    private uint forgottenUpTo;

    public ItemRequestMemory(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count => requests.Count;

    public ItemRequestStart Begin(uint requestId, out TResult remembered)
    {
        remembered = default;
        if (requestId == 0)
            return ItemRequestStart.New;
        if (requests.TryGetValue(requestId, out var known))
        {
            remembered = known.result;
            return known.done ? ItemRequestStart.Repeat : ItemRequestStart.Pending;
        }
        if (requestId <= forgottenUpTo)
            return ItemRequestStart.Expired;
        requests[requestId] = (false, default);
        order.Enqueue(requestId);
        Trim();
        return ItemRequestStart.New;
    }

    /// <summary>Remembers a request's answer. A request never begun (id 0, or forgotten) isn't remembered.</summary>
    public void Complete(uint requestId, TResult result)
    {
        if (requestId != 0 && requests.ContainsKey(requestId))
            requests[requestId] = (true, result);
    }

    private void Trim()
    {
        while (order.Count > Capacity)
        {
            uint oldest = order.Dequeue();
            requests.Remove(oldest);
            if (oldest > forgottenUpTo)
                forgottenUpTo = oldest;
        }
    }
}
