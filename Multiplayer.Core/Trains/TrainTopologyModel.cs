using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Trains;

public enum TrainEnd : byte
{
    Rear = 0,
    Front = 1
}

[Flags]
public enum TrainEndpointCapabilities : byte
{
    None = 0,
    Mechanical = 1 << 0,
    Hose = 1 << 1,
    Cock = 1 << 2,
    MultipleUnit = 1 << 3
}

public enum TrainChainState : byte
{
    Parked = 0,
    Dangling = 1,
    BeingDragged = 2,
    AttachedLoose = 3,
    AttachedTight = 4,
    Disabled = 5
}

public enum TrainTopologyCommandKind : byte
{
    Couple = 0,
    Uncouple = 1,
    ConnectHose = 2,
    DisconnectHose = 3,
    SetCock = 4,
    ConnectMultipleUnit = 5,
    DisconnectMultipleUnit = 6,
    SetChainState = 7
}

public enum TrainTopologyResultCode : byte
{
    Accepted = 0,
    UnknownEndpoint = 1,
    UnsupportedEndpoint = 2,
    StaleRevision = 3,
    RevisionExhausted = 4,
    InvalidRelationship = 5,
    EndpointOccupied = 6,
    WouldCreateCycle = 7,
    NotConnected = 8,
    InvariantViolation = 9,
    NativeMutationFailed = 10,
    AuthorityDenied = 11,
    OutOfRange = 12,
    BaselineChanged = 13
}

public readonly struct TrainEndpointId : IEquatable<TrainEndpointId>, IComparable<TrainEndpointId>
{
    public readonly ushort CarNetId;
    public readonly TrainEnd End;

    public TrainEndpointId(ushort carNetId, TrainEnd end)
    {
        CarNetId = carNetId;
        End = end;
    }

    public bool IsValid => CarNetId != 0;

    public int CompareTo(TrainEndpointId other)
    {
        int car = CarNetId.CompareTo(other.CarNetId);
        return car != 0 ? car : End.CompareTo(other.End);
    }

    public bool Equals(TrainEndpointId other) => CarNetId == other.CarNetId && End == other.End;
    public override bool Equals(object obj) => obj is TrainEndpointId other && Equals(other);
    public override int GetHashCode() => (CarNetId * 397) ^ (int)End;
    public override string ToString() => $"{CarNetId}:{End}";
    public static bool operator ==(TrainEndpointId left, TrainEndpointId right) => left.Equals(right);
    public static bool operator !=(TrainEndpointId left, TrainEndpointId right) => !left.Equals(right);
}

public sealed class TrainEndpointState
{
    public TrainEndpointId Endpoint { get; set; }
    public TrainEndpointCapabilities Capabilities { get; set; }
    public TrainEndpointId? MechanicalPartner { get; set; }
    public TrainEndpointId? HosePartner { get; set; }
    public TrainEndpointId? MultipleUnitPartner { get; set; }
    public TrainChainState ChainState { get; set; }
    public bool CockOpen { get; set; }
    public bool PreventAutoCouple { get; set; }

    public TrainEndpointState Clone() => new()
    {
        Endpoint = Endpoint,
        Capabilities = Capabilities,
        MechanicalPartner = MechanicalPartner,
        HosePartner = HosePartner,
        MultipleUnitPartner = MultipleUnitPartner,
        ChainState = ChainState,
        CockOpen = CockOpen,
        PreventAutoCouple = PreventAutoCouple
    };
}

public sealed class TrainTopologyState
{
    private readonly Dictionary<TrainEndpointId, TrainEndpointState> endpoints;

    public TrainTopologyState(uint revision, IEnumerable<TrainEndpointState> endpointStates)
    {
        Revision = revision;
        endpoints = endpointStates?.ToDictionary(state => state.Endpoint, state => state.Clone())
            ?? new Dictionary<TrainEndpointId, TrainEndpointState>();
    }

    public uint Revision { get; }
    public IReadOnlyCollection<TrainEndpointState> Endpoints => endpoints.Values;

    public bool TryGet(TrainEndpointId endpoint, out TrainEndpointState state) =>
        endpoints.TryGetValue(endpoint, out state);

    public TrainTopologyState Clone(uint? revision = null) =>
        new(revision ?? Revision, endpoints.Values);

    internal Dictionary<TrainEndpointId, TrainEndpointState> CloneEndpointMap() =>
        endpoints.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
}

public sealed class TrainTopologyCommand
{
    public ulong OperationId { get; set; }
    public uint ExpectedRevision { get; set; }
    public TrainTopologyCommandKind Kind { get; set; }
    public TrainEndpointId Primary { get; set; }
    public TrainEndpointId? Secondary { get; set; }
    public bool RequestedValue { get; set; }
    public TrainChainState RequestedChainState { get; set; }
    public bool CascadeHose { get; set; }
    public bool CascadeMultipleUnit { get; set; }
}

public sealed class TrainTopologyResult
{
    public bool Accepted => Code == TrainTopologyResultCode.Accepted;
    public TrainTopologyResultCode Code { get; set; }
    public TrainTopologyState State { get; set; }
    public TrainEndpointId[] AffectedEndpoints { get; set; } = Array.Empty<TrainEndpointId>();
}

public static class TrainTopologyStateMachine
{
    public static TrainTopologyResult Apply(TrainTopologyState canonical, TrainTopologyCommand command)
    {
        if (canonical == null || command == null)
            return Reject(canonical, TrainTopologyResultCode.InvariantViolation);
        if (!Validate(canonical, out _))
            return Reject(canonical, TrainTopologyResultCode.InvariantViolation);
        if (command.ExpectedRevision != canonical.Revision)
            return Reject(canonical, TrainTopologyResultCode.StaleRevision,
                ExistingClosure(canonical, command));
        if (canonical.Revision == uint.MaxValue)
            return Reject(canonical, TrainTopologyResultCode.RevisionExhausted,
                ExistingClosure(canonical, command));

        Dictionary<TrainEndpointId, TrainEndpointState> map = canonical.CloneEndpointMap();
        if (!map.TryGetValue(command.Primary, out TrainEndpointState primary))
            return Reject(canonical, TrainTopologyResultCode.UnknownEndpoint);

        var affected = new HashSet<TrainEndpointId> { command.Primary };
        TrainEndpointState secondary = null;
        if (command.Secondary.HasValue)
        {
            if (!map.TryGetValue(command.Secondary.Value, out secondary))
                return Reject(canonical, TrainTopologyResultCode.UnknownEndpoint, affected);
            affected.Add(command.Secondary.Value);
        }

        TrainTopologyResultCode code = ApplyMutation(map, command, primary, secondary, affected);
        if (code != TrainTopologyResultCode.Accepted)
            return Reject(canonical, code, ExistingClosure(canonical, command, affected));

        var next = new TrainTopologyState(canonical.Revision + 1, map.Values);
        if (!Validate(next, out _))
            return Reject(canonical, TrainTopologyResultCode.InvariantViolation,
                ExistingClosure(canonical, command, affected));

        return new TrainTopologyResult
        {
            Code = TrainTopologyResultCode.Accepted,
            State = next,
            AffectedEndpoints = ExpandClosure(next, affected).OrderBy(id => id).ToArray()
        };
    }

    public static bool Validate(TrainTopologyState state, out string reason)
    {
        reason = string.Empty;
        if (state == null)
        {
            reason = "state-null";
            return false;
        }

        foreach (TrainEndpointState endpoint in state.Endpoints)
        {
            if (!endpoint.Endpoint.IsValid ||
                !Enum.IsDefined(typeof(TrainEnd), endpoint.Endpoint.End) ||
                !Enum.IsDefined(typeof(TrainChainState), endpoint.ChainState) ||
                (endpoint.Capabilities & ~(TrainEndpointCapabilities.Mechanical |
                    TrainEndpointCapabilities.Hose | TrainEndpointCapabilities.Cock |
                    TrainEndpointCapabilities.MultipleUnit)) != TrainEndpointCapabilities.None)
            {
                reason = "endpoint-invalid";
                return false;
            }
            if (!ValidatePartner(state, endpoint, endpoint.MechanicalPartner,
                    partner => partner.MechanicalPartner, TrainEndpointCapabilities.Mechanical,
                    "mechanical", out reason) ||
                !ValidatePartner(state, endpoint, endpoint.HosePartner,
                    partner => partner.HosePartner, TrainEndpointCapabilities.Hose,
                    "hose", out reason) ||
                !ValidatePartner(state, endpoint, endpoint.MultipleUnitPartner,
                    partner => partner.MultipleUnitPartner, TrainEndpointCapabilities.MultipleUnit,
                    "multiple-unit", out reason))
                return false;
            if (endpoint.CockOpen &&
                !endpoint.Capabilities.HasFlag(TrainEndpointCapabilities.Cock))
            {
                reason = $"cock-unsupported:{endpoint.Endpoint}";
                return false;
            }
        }

        foreach (IGrouping<ushort, TrainEndpointState> car in
                 state.Endpoints.GroupBy(endpoint => endpoint.Endpoint.CarNetId))
        {
            if (car.Count() != 2 ||
                !car.Any(endpoint => endpoint.Endpoint.End == TrainEnd.Rear) ||
                !car.Any(endpoint => endpoint.Endpoint.End == TrainEnd.Front))
            {
                reason = $"car-endpoint-set-invalid:{car.Key}";
                return false;
            }
        }

        return !ContainsMechanicalCycle(state, out reason);
    }

    private static TrainTopologyResultCode ApplyMutation(
        Dictionary<TrainEndpointId, TrainEndpointState> map,
        TrainTopologyCommand command,
        TrainEndpointState primary,
        TrainEndpointState secondary,
        HashSet<TrainEndpointId> affected)
    {
        switch (command.Kind)
        {
            case TrainTopologyCommandKind.Couple:
                if (secondary == null || primary.Endpoint == secondary.Endpoint)
                    return TrainTopologyResultCode.InvalidRelationship;
                if (!Supports(primary, TrainEndpointCapabilities.Mechanical) ||
                    !Supports(secondary, TrainEndpointCapabilities.Mechanical))
                    return TrainTopologyResultCode.UnsupportedEndpoint;
                if (primary.MechanicalPartner.HasValue || secondary.MechanicalPartner.HasValue)
                    return TrainTopologyResultCode.EndpointOccupied;
                if (CarsMechanicallyConnected(map, primary.Endpoint.CarNetId,
                        secondary.Endpoint.CarNetId))
                    return TrainTopologyResultCode.WouldCreateCycle;
                primary.MechanicalPartner = secondary.Endpoint;
                secondary.MechanicalPartner = primary.Endpoint;
                return TrainTopologyResultCode.Accepted;

            case TrainTopologyCommandKind.Uncouple:
            {
                TrainTopologyResultCode disconnectResult = DisconnectPair(map, primary,
                    secondary, affected,
                    state => state.MechanicalPartner,
                    (state, value) => state.MechanicalPartner = value,
                    command.CascadeHose, command.CascadeMultipleUnit);
                if (disconnectResult == TrainTopologyResultCode.Accepted)
                    primary.ChainState = command.RequestedChainState;
                return disconnectResult;
            }

            case TrainTopologyCommandKind.ConnectHose:
                return ConnectPair(primary, secondary, TrainEndpointCapabilities.Hose,
                    state => state.HosePartner,
                    (state, value) => state.HosePartner = value);

            case TrainTopologyCommandKind.DisconnectHose:
                return DisconnectPair(map, primary, secondary, affected,
                    state => state.HosePartner,
                    (state, value) => state.HosePartner = value, false, false);

            case TrainTopologyCommandKind.SetCock:
                if (!Supports(primary, TrainEndpointCapabilities.Cock))
                    return TrainTopologyResultCode.UnsupportedEndpoint;
                primary.CockOpen = command.RequestedValue;
                return TrainTopologyResultCode.Accepted;

            case TrainTopologyCommandKind.ConnectMultipleUnit:
                return ConnectPair(primary, secondary, TrainEndpointCapabilities.MultipleUnit,
                    state => state.MultipleUnitPartner,
                    (state, value) => state.MultipleUnitPartner = value);

            case TrainTopologyCommandKind.DisconnectMultipleUnit:
                return DisconnectPair(map, primary, secondary, affected,
                    state => state.MultipleUnitPartner,
                    (state, value) => state.MultipleUnitPartner = value, false, false);

            case TrainTopologyCommandKind.SetChainState:
                if (!Supports(primary, TrainEndpointCapabilities.Mechanical))
                    return TrainTopologyResultCode.UnsupportedEndpoint;
                primary.ChainState = command.RequestedChainState;
                return TrainTopologyResultCode.Accepted;

            default:
                return TrainTopologyResultCode.InvalidRelationship;
        }
    }

    private static TrainTopologyResultCode ConnectPair(
        TrainEndpointState primary,
        TrainEndpointState secondary,
        TrainEndpointCapabilities capability,
        Func<TrainEndpointState, TrainEndpointId?> getPartner,
        Action<TrainEndpointState, TrainEndpointId?> setPartner)
    {
        if (secondary == null || primary.Endpoint == secondary.Endpoint ||
            primary.Endpoint.CarNetId == secondary.Endpoint.CarNetId)
            return TrainTopologyResultCode.InvalidRelationship;
        if (!Supports(primary, capability) || !Supports(secondary, capability))
            return TrainTopologyResultCode.UnsupportedEndpoint;
        if (getPartner(primary).HasValue || getPartner(secondary).HasValue)
            return TrainTopologyResultCode.EndpointOccupied;
        setPartner(primary, secondary.Endpoint);
        setPartner(secondary, primary.Endpoint);
        return TrainTopologyResultCode.Accepted;
    }

    private static TrainTopologyResultCode DisconnectPair(
        Dictionary<TrainEndpointId, TrainEndpointState> map,
        TrainEndpointState primary,
        TrainEndpointState secondary,
        HashSet<TrainEndpointId> affected,
        Func<TrainEndpointState, TrainEndpointId?> getPartner,
        Action<TrainEndpointState, TrainEndpointId?> setPartner,
        bool cascadeHose,
        bool cascadeMultipleUnit)
    {
        TrainEndpointId? partnerId = getPartner(primary);
        if (!partnerId.HasValue || !map.TryGetValue(partnerId.Value, out TrainEndpointState partner))
            return TrainTopologyResultCode.NotConnected;
        if (secondary != null && secondary.Endpoint != partner.Endpoint)
            return TrainTopologyResultCode.InvalidRelationship;
        affected.Add(partner.Endpoint);
        setPartner(primary, null);
        setPartner(partner, null);
        if (cascadeHose)
            ClearPair(map, primary, state => state.HosePartner,
                (state, value) => state.HosePartner = value, affected);
        if (cascadeMultipleUnit)
            ClearPair(map, primary, state => state.MultipleUnitPartner,
                (state, value) => state.MultipleUnitPartner = value, affected);
        return TrainTopologyResultCode.Accepted;
    }

    private static void ClearPair(
        Dictionary<TrainEndpointId, TrainEndpointState> map,
        TrainEndpointState primary,
        Func<TrainEndpointState, TrainEndpointId?> getPartner,
        Action<TrainEndpointState, TrainEndpointId?> setPartner,
        HashSet<TrainEndpointId> affected)
    {
        TrainEndpointId? partnerId = getPartner(primary);
        if (!partnerId.HasValue)
            return;
        setPartner(primary, null);
        if (map.TryGetValue(partnerId.Value, out TrainEndpointState partner))
        {
            setPartner(partner, null);
            affected.Add(partner.Endpoint);
        }
    }

    private static bool Supports(TrainEndpointState endpoint,
        TrainEndpointCapabilities capability) => endpoint.Capabilities.HasFlag(capability);

    private static bool ValidatePartner(
        TrainTopologyState state,
        TrainEndpointState endpoint,
        TrainEndpointId? partnerId,
        Func<TrainEndpointState, TrainEndpointId?> reverse,
        TrainEndpointCapabilities capability,
        string domain,
        out string reason)
    {
        reason = string.Empty;
        if (!partnerId.HasValue)
            return true;
        if (partnerId.Value == endpoint.Endpoint)
        {
            reason = $"{domain}-self:{endpoint.Endpoint}";
            return false;
        }
        if (!endpoint.Capabilities.HasFlag(capability) ||
            !state.TryGet(partnerId.Value, out TrainEndpointState partner) ||
            !partner.Capabilities.HasFlag(capability) ||
            reverse(partner) != endpoint.Endpoint)
        {
            reason = $"{domain}-not-reciprocal:{endpoint.Endpoint}";
            return false;
        }
        return true;
    }

    private static bool CarsMechanicallyConnected(
        Dictionary<TrainEndpointId, TrainEndpointState> map, ushort first, ushort second)
    {
        if (first == second)
            return true;
        var visited = new HashSet<ushort> { first };
        var queue = new Queue<ushort>();
        queue.Enqueue(first);
        while (queue.Count > 0)
        {
            ushort car = queue.Dequeue();
            foreach (TrainEnd end in new[] { TrainEnd.Rear, TrainEnd.Front })
            {
                if (!map.TryGetValue(new TrainEndpointId(car, end), out TrainEndpointState endpoint) ||
                    !endpoint.MechanicalPartner.HasValue)
                    continue;
                ushort next = endpoint.MechanicalPartner.Value.CarNetId;
                if (next == second)
                    return true;
                if (visited.Add(next))
                    queue.Enqueue(next);
            }
        }
        return false;
    }

    private static bool ContainsMechanicalCycle(TrainTopologyState state, out string reason)
    {
        reason = string.Empty;
        var adjacency = new Dictionary<ushort, HashSet<ushort>>();
        var edges = new HashSet<(ushort First, ushort Second)>();
        foreach (TrainEndpointState endpoint in state.Endpoints)
        {
            if (!adjacency.ContainsKey(endpoint.Endpoint.CarNetId))
                adjacency[endpoint.Endpoint.CarNetId] = new HashSet<ushort>();
            if (!endpoint.MechanicalPartner.HasValue)
                continue;
            ushort other = endpoint.MechanicalPartner.Value.CarNetId;
            if (endpoint.Endpoint.CompareTo(endpoint.MechanicalPartner.Value) < 0)
            {
                var edge = endpoint.Endpoint.CarNetId < other
                    ? (endpoint.Endpoint.CarNetId, other)
                    : (other, endpoint.Endpoint.CarNetId);
                if (!edges.Add(edge))
                {
                    reason = $"mechanical-parallel-edge:{edge.Item1}:{edge.Item2}";
                    return true;
                }
            }
            adjacency[endpoint.Endpoint.CarNetId].Add(other);
        }

        var visited = new HashSet<ushort>();
        foreach (ushort car in adjacency.Keys)
        {
            if (visited.Contains(car))
                continue;
            if (HasCycle(car, 0, adjacency, visited))
            {
                reason = $"mechanical-cycle:{car}";
                return true;
            }
        }
        return false;
    }

    private static bool HasCycle(ushort car, ushort parent,
        Dictionary<ushort, HashSet<ushort>> adjacency, HashSet<ushort> visited)
    {
        visited.Add(car);
        foreach (ushort next in adjacency[car])
        {
            if (!visited.Contains(next))
            {
                if (HasCycle(next, car, adjacency, visited))
                    return true;
            }
            else if (next != parent)
                return true;
        }
        return false;
    }

    private static HashSet<TrainEndpointId> ExistingClosure(TrainTopologyState state,
        TrainTopologyCommand command, IEnumerable<TrainEndpointId> seed = null)
    {
        var endpoints = seed == null
            ? new HashSet<TrainEndpointId>()
            : new HashSet<TrainEndpointId>(seed);
        endpoints.Add(command.Primary);
        if (command.Secondary.HasValue)
            endpoints.Add(command.Secondary.Value);
        return ExpandClosure(state, endpoints);
    }

    private static HashSet<TrainEndpointId> ExpandClosure(TrainTopologyState state,
        IEnumerable<TrainEndpointId> seed)
    {
        var result = new HashSet<TrainEndpointId>(seed);
        var pending = new Queue<TrainEndpointId>(result);
        while (pending.Count > 0)
        {
            TrainEndpointId id = pending.Dequeue();
            if (state == null || !state.TryGet(id, out TrainEndpointState endpoint))
                continue;
            foreach (TrainEndpointId? partner in new[]
                     { endpoint.MechanicalPartner, endpoint.HosePartner, endpoint.MultipleUnitPartner })
            {
                if (partner.HasValue && result.Add(partner.Value))
                    pending.Enqueue(partner.Value);
            }
        }
        return result;
    }

    private static TrainTopologyResult Reject(TrainTopologyState state,
        TrainTopologyResultCode code, IEnumerable<TrainEndpointId> affected = null) => new()
    {
        Code = code,
        State = state?.Clone(),
        AffectedEndpoints = affected?.Distinct().OrderBy(id => id).ToArray()
            ?? Array.Empty<TrainEndpointId>()
    };
}
