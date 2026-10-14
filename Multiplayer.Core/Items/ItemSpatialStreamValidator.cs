namespace Multiplayer.Core.Items;

public readonly struct ItemSpatialLeaseToken
{
    public readonly byte SimulatorPlayerId;
    public readonly uint AuthorityRevision;
    public readonly uint SimulationEpoch;
    public readonly uint LastSequence;
    public readonly bool PlacementAllowsSimulation;

    public ItemSpatialLeaseToken(byte simulatorPlayerId, uint authorityRevision, uint simulationEpoch,
        uint lastSequence, bool placementAllowsSimulation)
    {
        SimulatorPlayerId = simulatorPlayerId;
        AuthorityRevision = authorityRevision;
        SimulationEpoch = simulationEpoch;
        LastSequence = lastSequence;
        PlacementAllowsSimulation = placementAllowsSimulation;
    }
}

public readonly struct ItemSpatialSampleToken
{
    public readonly byte SenderPlayerId;
    public readonly byte SimulatorPlayerId;
    public readonly uint AuthorityRevision;
    public readonly uint SimulationEpoch;
    public readonly uint SampleSequence;

    public ItemSpatialSampleToken(byte senderPlayerId, byte simulatorPlayerId, uint authorityRevision,
        uint simulationEpoch, uint sampleSequence)
    {
        SenderPlayerId = senderPlayerId;
        SimulatorPlayerId = simulatorPlayerId;
        AuthorityRevision = authorityRevision;
        SimulationEpoch = simulationEpoch;
        SampleSequence = sampleSequence;
    }
}

/// <summary>
/// Pure ordering and capability gate for transient item motion. Geometry and parent-anchor
/// validation remain in the Unity integration layer.
/// </summary>
public static class ItemSpatialStreamValidator
{
    public static string Validate(ItemSpatialLeaseToken lease, ItemSpatialSampleToken sample)
    {
        if (!lease.PlacementAllowsSimulation)
            return "spatial-placement-not-simulatable";
        if (sample.SenderPlayerId != lease.SimulatorPlayerId ||
            sample.SimulatorPlayerId != lease.SimulatorPlayerId)
            return "sender-not-simulator";
        if (sample.SimulationEpoch != lease.SimulationEpoch)
            return "stale-simulation-epoch";
        if (sample.AuthorityRevision != lease.AuthorityRevision)
            return "stale-spatial-authority-revision";
        if (sample.SampleSequence <= lease.LastSequence)
            return "stale-sample-sequence";
        return string.Empty;
    }
}
