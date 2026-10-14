#if DEBUG
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests.TrainFixtures;

internal enum DebugTrainFixtureState
{
    Allocated,
    Spawning,
    Ready,
    Running,
    Stopping,
    Cleaning,
    Clean,
    Failed
}

internal enum DebugTrainSpeedControllerState
{
    Idle,
    Starting,
    Accelerating,
    Cruising,
    Braking,
    Stopped,
    EmergencyStop
}

[DisallowMultipleComponent]
internal sealed class DebugTrainFixtureTag : MonoBehaviour
{
    public string FixtureId;
    public string RunId;
    public int CarIndex;
    public string Role;
}

internal sealed class DebugTrainFixtureCarRecord
{
    public int Index;
    public string Role;
    public string LiveryId;
    public TrainCar Car;
    public ushort NetId;
    public string CarId;
    public string CarGuid;
}

internal sealed class DebugTrainRouteJunctionRecord
{
    public ushort NetId;
    public Junction Junction;
    public int OriginalBranch;
    public int LeasedBranch;
}

internal sealed class DebugTrainFixtureRecord
{
    public string FixtureId;
    public string RunId;
    public DateTime CreatedUtc;
    public DebugTrainFixtureState State;
    public DebugTrainTrackRecord Track;
    public string LocationId;
    public int PointIndex;
    public bool WithTrackDirection;
    public bool PreventDerailment;
    public bool ProtectFuses;
    public bool SustainEngine;
    public bool PreventDamage;
    public float MaximumSpeedKph;
    public readonly List<DebugTrainFixtureCarRecord> Cars = new();
    public readonly List<DebugTrainRouteJunctionRecord> Junctions = new();
    public DebugTrainSpeedControllerState ControllerState;
    public float TargetSpeedKph;
    public string ControllerReason;
    public float NextEngineSustainTime;
    public uint RelocationRevision;
    public string LastRelocationOperationId;
    public string Failure;
}
#endif
