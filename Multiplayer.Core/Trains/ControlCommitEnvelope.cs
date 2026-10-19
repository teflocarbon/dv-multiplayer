using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Trains;

/// <summary>Why the host refused a player's control change, or that it took it.</summary>
public enum ControlEnvelopeResult
{
    Accepted,
    /// <summary>The player is too far from the car to have changed its controls by hand.</summary>
    OutOfRange,
    /// <summary>Another player holds the lease on one of the controls (<see cref="ControlLeases{TPlayer}"/>).</summary>
    AuthorityDenied,
    /// <summary>The change was made against an older state of the car's controls, out of order.</summary>
    StaleRevision,
}

/// <summary>
/// The checks every player's control change (ports, fuses, handbrake, brake release) passes before
/// the host applies it. A change is made against the car's control revision the player last had;
/// one made against an older revision is stale, unless it's the next in that player's own ordered
/// stream of operations on that car: a player turning a lever sends several changes before the
/// first commit (and its new revision) comes back, and each of those is still theirs to make.
/// </summary>
public sealed class ControlCommitEnvelope
{
    private readonly Dictionary<(byte PlayerId, ushort CarNetId), ulong> lastOperation = new();

    /// <param name="inRange">Whether the player is close enough (or acting by remote, which isn't checked).</param>
    /// <param name="mayWrite">Whether nobody else holds a lease on the controls (true for changes that need none).</param>
    public ControlEnvelopeResult Validate(byte playerId, ushort carNetId, bool inRange, bool mayWrite,
        uint expectedRevision, uint currentRevision, ulong operationId)
    {
        if (!inRange)
            return ControlEnvelopeResult.OutOfRange;
        if (!mayWrite)
            return ControlEnvelopeResult.AuthorityDenied;
        var key = (playerId, carNetId);
        bool orderedOperationStream = operationId != 0 &&
            (!lastOperation.TryGetValue(key, out ulong previous) || operationId > previous);
        if (expectedRevision != currentRevision && !orderedOperationStream)
            return ControlEnvelopeResult.StaleRevision;
        lastOperation[key] = operationId;
        return ControlEnvelopeResult.Accepted;
    }

    /// <summary>A player who left: their operation streams start again if they come back.</summary>
    public void Forget(byte playerId)
    {
        foreach (var key in lastOperation.Keys.Where(key => key.PlayerId == playerId).ToArray())
            lastOperation.Remove(key);
    }
}
