using Multiplayer.Core.Trains;
using NUnit.Framework;
using System.Linq;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class TrainTopologyStateMachineTests
{
    [Test]
    public void Couple_CommitsReciprocalPairAndOneRevision()
    {
        TrainTopologyState original = State(Car(10), Car(20));
        TrainTopologyResult result = TrainTopologyStateMachine.Apply(original,
            Command(TrainTopologyCommandKind.Couple, E(10, TrainEnd.Front),
                E(20, TrainEnd.Rear)));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.Revision, Is.EqualTo(1));
        Assert.That(result.State.TryGet(E(10, TrainEnd.Front), out TrainEndpointState first), Is.True);
        Assert.That(result.State.TryGet(E(20, TrainEnd.Rear), out TrainEndpointState second), Is.True);
        Assert.That(first.MechanicalPartner, Is.EqualTo(second.Endpoint));
        Assert.That(second.MechanicalPartner, Is.EqualTo(first.Endpoint));
        Assert.That(original.Endpoints.All(endpoint => !endpoint.MechanicalPartner.HasValue), Is.True);
    }

    [Test]
    public void Couple_RejectsCycleWithoutMutation()
    {
        TrainTopologyState state = State(Car(1), Car(2), Car(3));
        state = Apply(state, TrainTopologyCommandKind.Couple, E(1, TrainEnd.Front), E(2, TrainEnd.Rear));
        state = Apply(state, TrainTopologyCommandKind.Couple, E(2, TrainEnd.Front), E(3, TrainEnd.Rear));

        TrainTopologyResult result = TrainTopologyStateMachine.Apply(state,
            Command(TrainTopologyCommandKind.Couple, E(3, TrainEnd.Front), E(1, TrainEnd.Rear),
                state.Revision));

        Assert.That(result.Code, Is.EqualTo(TrainTopologyResultCode.WouldCreateCycle));
        Assert.That(result.State.Revision, Is.EqualTo(state.Revision));
        Assert.That(result.State.TryGet(E(3, TrainEnd.Front), out TrainEndpointState endpoint), Is.True);
        Assert.That(endpoint.MechanicalPartner, Is.Null);
    }

    [Test]
    public void StaleRevision_ReturnsCanonicalClosure()
    {
        TrainTopologyState state = State(Car(10), Car(20));
        state = Apply(state, TrainTopologyCommandKind.ConnectHose,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear));

        TrainTopologyResult result = TrainTopologyStateMachine.Apply(state,
            Command(TrainTopologyCommandKind.DisconnectHose, E(10, TrainEnd.Front),
                expectedRevision: 0));

        Assert.That(result.Code, Is.EqualTo(TrainTopologyResultCode.StaleRevision));
        Assert.That(result.State.Revision, Is.EqualTo(1));
        Assert.That(result.AffectedEndpoints, Does.Contain(E(10, TrainEnd.Front)));
        Assert.That(result.AffectedEndpoints, Does.Contain(E(20, TrainEnd.Rear)));
    }

    [Test]
    public void ChainUncouple_CanPreserveIndependentHose()
    {
        TrainTopologyState state = State(Car(10), Car(20));
        state = Apply(state, TrainTopologyCommandKind.Couple,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear));
        state = Apply(state, TrainTopologyCommandKind.ConnectHose,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear));

        TrainTopologyCommand command = Command(TrainTopologyCommandKind.Uncouple,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear), state.Revision);
        command.CascadeHose = false;
        TrainTopologyResult result = TrainTopologyStateMachine.Apply(state, command);

        Assert.That(result.Accepted, Is.True);
        result.State.TryGet(E(10, TrainEnd.Front), out TrainEndpointState first);
        result.State.TryGet(E(20, TrainEnd.Rear), out TrainEndpointState second);
        Assert.That(first.MechanicalPartner, Is.Null);
        Assert.That(second.MechanicalPartner, Is.Null);
        Assert.That(first.HosePartner, Is.EqualTo(second.Endpoint));
        Assert.That(second.HosePartner, Is.EqualTo(first.Endpoint));
    }

    [Test]
    public void ChainUncouple_CommitsRequestedFinalPlacement()
    {
        TrainTopologyState state = State(Car(10), Car(20));
        state = Apply(state, TrainTopologyCommandKind.Couple,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear));
        TrainTopologyCommand command = Command(TrainTopologyCommandKind.Uncouple,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear), state.Revision);
        command.RequestedChainState = TrainChainState.Dangling;

        TrainTopologyResult result = TrainTopologyStateMachine.Apply(state, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.TryGet(E(10, TrainEnd.Front), out TrainEndpointState endpoint),
            Is.True);
        Assert.That(endpoint.MechanicalPartner, Is.Null);
        Assert.That(endpoint.ChainState, Is.EqualTo(TrainChainState.Dangling));
    }

    [Test]
    public void OrdinaryUncouple_CanCascadeHoseAndMu()
    {
        TrainTopologyState state = State(Car(10), Car(20));
        state = Apply(state, TrainTopologyCommandKind.Couple,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear));
        state = Apply(state, TrainTopologyCommandKind.ConnectHose,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear));
        state = Apply(state, TrainTopologyCommandKind.ConnectMultipleUnit,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear));

        TrainTopologyCommand command = Command(TrainTopologyCommandKind.Uncouple,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear), state.Revision);
        command.CascadeHose = true;
        command.CascadeMultipleUnit = true;
        TrainTopologyResult result = TrainTopologyStateMachine.Apply(state, command);

        Assert.That(result.Accepted, Is.True);
        result.State.TryGet(E(10, TrainEnd.Front), out TrainEndpointState first);
        result.State.TryGet(E(20, TrainEnd.Rear), out TrainEndpointState second);
        Assert.Multiple(() =>
        {
            Assert.That(first.MechanicalPartner, Is.Null);
            Assert.That(first.HosePartner, Is.Null);
            Assert.That(first.MultipleUnitPartner, Is.Null);
            Assert.That(second.MechanicalPartner, Is.Null);
            Assert.That(second.HosePartner, Is.Null);
            Assert.That(second.MultipleUnitPartner, Is.Null);
        });
    }

    [Test]
    public void InvalidOneSidedGraph_IsRejectedBeforeMutation()
    {
        TrainEndpointState[] cars = Car(10).Concat(Car(20)).ToArray();
        cars.Single(endpoint => endpoint.Endpoint == E(10, TrainEnd.Front)).HosePartner =
            E(20, TrainEnd.Rear);
        TrainTopologyState invalid = State(cars);

        Assert.That(TrainTopologyStateMachine.Validate(invalid, out string reason), Is.False);
        Assert.That(reason, Does.StartWith("hose-not-reciprocal"));
        TrainTopologyResult result = TrainTopologyStateMachine.Apply(invalid,
            Command(TrainTopologyCommandKind.SetCock, E(10, TrainEnd.Front)));
        Assert.That(result.Code, Is.EqualTo(TrainTopologyResultCode.InvariantViolation));
    }

    [Test]
    public void UnsupportedMuEndpoint_IsRejected()
    {
        TrainTopologyState state = State(Car(10, mu: true), Car(20, mu: false));
        TrainTopologyResult result = TrainTopologyStateMachine.Apply(state,
            Command(TrainTopologyCommandKind.ConnectMultipleUnit,
                E(10, TrainEnd.Front), E(20, TrainEnd.Rear)));

        Assert.That(result.Code, Is.EqualTo(TrainTopologyResultCode.UnsupportedEndpoint));
        Assert.That(result.State.Revision, Is.Zero);
    }

    [Test]
    public void MissingPhysicalEnd_IsRejected()
    {
        TrainTopologyState state = State(Endpoint(10, TrainEnd.Front,
            TrainEndpointCapabilities.Mechanical));

        Assert.That(TrainTopologyStateMachine.Validate(state, out string reason), Is.False);
        Assert.That(reason, Is.EqualTo("car-endpoint-set-invalid:10"));
    }

    [Test]
    public void SameCarHoseConnection_IsRejected()
    {
        TrainTopologyState state = State(Car(10));
        TrainTopologyResult result = TrainTopologyStateMachine.Apply(state,
            Command(TrainTopologyCommandKind.ConnectHose,
                E(10, TrainEnd.Front), E(10, TrainEnd.Rear)));

        Assert.That(result.Code, Is.EqualTo(TrainTopologyResultCode.InvalidRelationship));
    }

    [Test]
    public void ParallelMechanicalEdgesBetweenSameCars_AreRejected()
    {
        TrainEndpointState[] endpoints = Car(10).Concat(Car(20)).ToArray();
        TrainEndpointState tenRear = endpoints.Single(value => value.Endpoint == E(10, TrainEnd.Rear));
        TrainEndpointState tenFront = endpoints.Single(value => value.Endpoint == E(10, TrainEnd.Front));
        TrainEndpointState twentyRear = endpoints.Single(value => value.Endpoint == E(20, TrainEnd.Rear));
        TrainEndpointState twentyFront = endpoints.Single(value => value.Endpoint == E(20, TrainEnd.Front));
        tenRear.MechanicalPartner = twentyFront.Endpoint;
        twentyFront.MechanicalPartner = tenRear.Endpoint;
        tenFront.MechanicalPartner = twentyRear.Endpoint;
        twentyRear.MechanicalPartner = tenFront.Endpoint;

        Assert.That(TrainTopologyStateMachine.Validate(State(endpoints), out string reason), Is.False);
        Assert.That(reason, Does.StartWith("mechanical-parallel-edge"));
    }

    [Test]
    public void ReapplyingAcceptedCommandAsStale_IsIdempotent()
    {
        TrainTopologyState state = State(Car(10), Car(20));
        TrainTopologyCommand command = Command(TrainTopologyCommandKind.Couple,
            E(10, TrainEnd.Front), E(20, TrainEnd.Rear));
        TrainTopologyResult accepted = TrainTopologyStateMachine.Apply(state, command);
        TrainTopologyResult replay = TrainTopologyStateMachine.Apply(accepted.State, command);

        Assert.That(accepted.Accepted, Is.True);
        Assert.That(replay.Code, Is.EqualTo(TrainTopologyResultCode.StaleRevision));
        Assert.That(replay.State.Revision, Is.EqualTo(accepted.State.Revision));
        replay.State.TryGet(E(10, TrainEnd.Front), out TrainEndpointState first);
        Assert.That(first.MechanicalPartner, Is.EqualTo(E(20, TrainEnd.Rear)));
    }

    private static TrainTopologyState Apply(TrainTopologyState state,
        TrainTopologyCommandKind kind, TrainEndpointId first, TrainEndpointId second)
    {
        TrainTopologyResult result = TrainTopologyStateMachine.Apply(state,
            Command(kind, first, second, state.Revision));
        Assert.That(result.Accepted, Is.True, result.Code.ToString());
        return result.State;
    }

    private static TrainTopologyCommand Command(TrainTopologyCommandKind kind,
        TrainEndpointId primary, TrainEndpointId? secondary = null, uint expectedRevision = 0) =>
        new()
        {
            OperationId = 42,
            Kind = kind,
            Primary = primary,
            Secondary = secondary,
            ExpectedRevision = expectedRevision
        };

    private static TrainTopologyState State(params TrainEndpointState[][] cars) =>
        new(0, cars.SelectMany(car => car));

    private static TrainTopologyState State(params TrainEndpointState[] endpoints) => new(0, endpoints);

    private static TrainEndpointState[] Car(ushort netId, bool mu = true)
    {
        TrainEndpointCapabilities capabilities = TrainEndpointCapabilities.Mechanical |
            TrainEndpointCapabilities.Hose | TrainEndpointCapabilities.Cock;
        if (mu)
            capabilities |= TrainEndpointCapabilities.MultipleUnit;
        return new[]
        {
            Endpoint(netId, TrainEnd.Rear, capabilities),
            Endpoint(netId, TrainEnd.Front, capabilities)
        };
    }

    private static TrainEndpointState Endpoint(ushort netId, TrainEnd end,
        TrainEndpointCapabilities capabilities) => new()
    {
        Endpoint = E(netId, end),
        Capabilities = capabilities,
        ChainState = TrainChainState.Parked
    };

    private static TrainEndpointId E(ushort car, TrainEnd end) => new(car, end);
}
