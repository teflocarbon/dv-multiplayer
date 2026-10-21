using System.Collections.Generic;

namespace Multiplayer.Core.Gadgets;

public enum GadgetChangeKind : byte
{
    Installed = 1,
    Removed = 2,
    /// <summary>A mount point bolted, taped or freed (PR 4).</summary>
    MountPoint = 3,
    HoleAdded = 4,
    HoleRemoved = 5,
    /// <summary>Two ports wired or unwired (PR 5): the gadget's port and the other gadget's.</summary>
    Wired = 6,
    Unwired = 7,
    /// <summary>A gadget's soldering units, absolute (PR 5).</summary>
    Soldered = 8
}

/// <summary>A change the server observed on one car's gadgets, waiting to be sent.</summary>
public sealed class GadgetChange<TGadget>
{
    public ushort Anchor;
    public uint Revision;
    public GadgetChangeKind Kind;
    public TGadget Gadget;
    public bool Forced;
    /// <summary>MountPoint: the point's index and its new state.</summary>
    public int Index;
    public byte Value;
    /// <summary>A hole: its position and direction in the car's parenting frame, six numbers.</summary>
    public float[] Point;
    /// <summary>Wired, Unwired: the other gadget's UID and its port.</summary>
    public int OtherUid;
    public int OtherIndex;
    /// <summary>The request that caused it: its player and operation, 0 for none.</summary>
    public byte PlayerId;
    public uint OperationId;
}

/// <summary>
/// The server's gadget change log before it is sent (gadgets.md, "The change log"). The server observes
/// the native primitives (link, unlink) rather than announcing requests, and records only top-level
/// changes: while one primitive runs, the ones it calls (a removal's unwiring, unmounting, the gadgets
/// riding a removed mount) are its consequences, which every game reproduces by running the same native
/// call. Each car has its own revision, given when a change is recorded, so a snapshot taken in the
/// same tick knows which entries it already contains.
/// </summary>
public sealed class GadgetChangeRecorder<TGadget>
{
    private readonly Dictionary<ushort, uint> revisions = new();
    private readonly List<GadgetChange<TGadget>> pending = new();
    private int depth;
    private byte operationPlayer;
    private uint operation;

    /// <summary>Whether a primitive is running: what happens now is its consequence.</summary>
    public bool Nested => depth > 0;

    /// <summary>A primitive starts. Returns whether it is top-level.</summary>
    public bool Enter() => depth++ == 0;

    public void Exit()
    {
        if (depth > 0)
            depth--;
    }

    /// <summary>The request the next changes answer, until <see cref="EndOperation"/>.</summary>
    public void BeginOperation(byte playerId, uint operationId)
    {
        operationPlayer = playerId;
        operation = operationId;
    }

    public void EndOperation()
    {
        operationPlayer = 0;
        operation = 0;
    }

    /// <summary>
    /// Records a top-level change, called from inside the primitive (after <see cref="Enter"/> said it is
    /// top-level). Returns the car's new revision.
    /// </summary>
    public uint Record(ushort anchor, GadgetChangeKind kind, TGadget gadget, bool forced = false,
        int index = 0, byte value = 0, float[] point = null, int otherUid = 0, int otherIndex = 0)
    {
        uint revision = Revision(anchor) + 1;
        revisions[anchor] = revision;
        pending.Add(new GadgetChange<TGadget>
        {
            Anchor = anchor,
            Revision = revision,
            Kind = kind,
            Gadget = gadget,
            Forced = forced,
            Index = index,
            Value = value,
            Point = point,
            OtherUid = otherUid,
            OtherIndex = otherIndex,
            PlayerId = operationPlayer,
            OperationId = operation
        });
        return revision;
    }

    /// <summary>The car's revision: how many changes have been recorded on it.</summary>
    public uint Revision(ushort anchor) => revisions.TryGetValue(anchor, out uint revision) ? revision : 0;

    /// <summary>The changes recorded since the last flush, in order, and forgets them.</summary>
    public List<GadgetChange<TGadget>> Flush()
    {
        List<GadgetChange<TGadget>> flushed = new(pending);
        pending.Clear();
        return flushed;
    }

    /// <summary>A car that's gone: its revision with it.</summary>
    public void Forget(ushort anchor) => revisions.Remove(anchor);

    public void Clear()
    {
        revisions.Clear();
        pending.Clear();
        depth = 0;
        EndOperation();
    }
}

public enum GadgetRevisionStep : byte
{
    /// <summary>Already applied (or in the snapshot): skip it.</summary>
    Skip,
    /// <summary>The next one: apply it.</summary>
    Apply,
    /// <summary>One or more are missing before it: ask for the car's snapshot.</summary>
    Gap
}

/// <summary>What a client does with a car's change at <paramref name="incoming"/> after applying up to <paramref name="applied"/>.</summary>
public static class GadgetRevisionOrder
{
    public static GadgetRevisionStep Decide(uint applied, uint incoming) =>
        incoming <= applied ? GadgetRevisionStep.Skip
        : incoming == applied + 1 ? GadgetRevisionStep.Apply
        : GadgetRevisionStep.Gap;
}
