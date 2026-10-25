namespace Multiplayer.Core.Items.Projection;

/// <summary>What a client says of an item the server sent it a Create or a retirement for.</summary>
public enum ProjectionAck : byte
{
    /// <summary>It has the item, at the revision named.</summary>
    Projected = 0,
    /// <summary>It took the item out of its game, as the server's retirement said.</summary>
    Retired = 1,
    /// <summary>It was sent a Create it couldn't make: the item isn't in its game.</summary>
    Failed = 2
}

public enum ProjectionAckOutcome
{
    /// <summary>The recipient has the item: updates and requests about it are taken.</summary>
    Acknowledged,
    /// <summary>An ack for an item the server didn't send it, or from beyond the server's revision.</summary>
    Rejected,
    /// <summary>The recipient no longer has the item acknowledged; what the server knows it was sent stays.</summary>
    Unacknowledged,
    /// <summary>The recipient doesn't have the item: the server forgets it sent the Create, and sends it again after a pause.</summary>
    Forgotten
}

/// <summary>
/// The server's answer to a projection acknowledgement (docs/design/items/projection.md,
/// "Acknowledgements"). A projection is known only once acknowledged. Before R7 an ack was a
/// bool, so a retirement's and a failure's looked the same: a retirement ack arriving after the
/// item had come back into the player's interest made the server forget the new Create and hold
/// it back for five seconds, and then send it again over the object the client had made (F45,
/// OI-SYNC-07). Only a failure is forgotten now; a retirement only takes the acknowledgement away.
/// </summary>
public static class ProjectionAcks
{
    /// <param name="itemExists">The server has the item.</param>
    /// <param name="revisionReached">The ack's revision is no later than the server's record (an older one still means the client has it: later revisions follow on the same ordered channel).</param>
    /// <param name="recipientKnows">The server has sent the recipient the item's Create (it's in its known items).</param>
    public static ProjectionAckOutcome Apply(ProjectionAck ack, bool itemExists, bool revisionReached,
        bool recipientKnows) => ack switch
    {
        ProjectionAck.Projected => itemExists && revisionReached && recipientKnows
            ? ProjectionAckOutcome.Acknowledged
            : ProjectionAckOutcome.Rejected,
        ProjectionAck.Failed => itemExists && recipientKnows
            ? ProjectionAckOutcome.Forgotten
            : ProjectionAckOutcome.Unacknowledged,
        _ => ProjectionAckOutcome.Unacknowledged
    };
}
