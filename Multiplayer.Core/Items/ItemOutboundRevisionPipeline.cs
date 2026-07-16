namespace Multiplayer.Core.Items;

/// <summary>
/// Reserves expected authority revisions for reliable-ordered client transitions. This permits
/// rapid local transitions to be pipelined without every packet reusing the last acknowledged
/// revision. A host-authored correction resets speculative reservations.
/// </summary>
public sealed class ItemOutboundRevisionPipeline
{
    private uint nextRevision;
    private bool initialized;

    public uint Reserve(uint canonicalRevision)
    {
        if (!initialized || nextRevision < canonicalRevision)
        {
            nextRevision = canonicalRevision;
            initialized = true;
        }

        uint reserved = nextRevision;
        if (nextRevision != uint.MaxValue)
            nextRevision++;
        return reserved;
    }

    public void ObserveCanonical(uint canonicalRevision, bool preserveReservations)
    {
        if (!preserveReservations || !initialized)
        {
            nextRevision = canonicalRevision;
            initialized = true;
            return;
        }

        if (nextRevision < canonicalRevision)
            nextRevision = canonicalRevision;
    }

    /// <summary>
    /// Starts a new network identity lifetime. Pooled Unity representations must not carry
    /// speculative or acknowledged revisions from the item that previously occupied them.
    /// </summary>
    public void Reset()
    {
        nextRevision = 0;
        initialized = false;
    }
}
