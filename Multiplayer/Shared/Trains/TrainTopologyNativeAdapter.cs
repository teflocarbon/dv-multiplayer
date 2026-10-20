using DV.MultipleUnit;
using DV.Simulation.Brake;
using Multiplayer.Core.Trains;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Shared.Trains;

internal static class TrainTopologyNativeAdapter
{
    internal static TrainTopologyState Capture(uint revision)
    {
        var endpoints = new List<TrainEndpointState>();
        foreach (NetworkedTrainCar networkedCar in NetworkedTrainCar.GetAllRegistered()
                     .OrderBy(value => value.NetId))
        {
            endpoints.Add(CaptureEndpoint(networkedCar, networkedCar.TrainCar.rearCoupler));
            endpoints.Add(CaptureEndpoint(networkedCar, networkedCar.TrainCar.frontCoupler));
        }
        return new TrainTopologyState(revision, endpoints);
    }

    internal static BrakeLineStateData[] CaptureBrakeLines()
    {
        var seen = new HashSet<Brakeset>();
        var result = new List<BrakeLineStateData>();
        foreach (NetworkedTrainCar networkedCar in NetworkedTrainCar.GetAllRegistered())
        {
            Brakeset brakeset = networkedCar.TrainCar?.brakeSystem?.brakeset;
            if (brakeset == null || !seen.Add(brakeset))
                continue;
            ushort[] members = brakeset.cars
                .Select(system => system?.GetComponent<TrainCar>()?.GetNetId() ?? (ushort)0)
                .Where(netId => netId != 0)
                .Distinct()
                .OrderBy(netId => netId)
                .ToArray();
            if (members.Length == 0)
                continue;
            result.Add(new BrakeLineStateData
            {
                AnchorCarNetId = members[0],
                AnchorEnd = TrainEnd.Rear,
                MemberDigest = DigestMembers(members),
                PipePressure = brakeset.pipePressure
            });
        }
        return result.OrderBy(line => line.AnchorCarNetId).ToArray();
    }

    internal static bool TryCaptureBrakeLine(ushort carNetId, out BrakeLineStateData line)
    {
        line = null;
        if (!NetworkedTrainCar.TryGet(carNetId, out TrainCar car) ||
            car?.brakeSystem?.brakeset == null)
            return false;
        Brakeset brakeset = car.brakeSystem.brakeset;
        ushort[] members = brakeset.cars
            .Select(system => system?.GetComponent<TrainCar>()?.GetNetId() ?? (ushort)0)
            .Where(netId => netId != 0).Distinct().OrderBy(netId => netId).ToArray();
        if (members.Length == 0)
            return false;
        line = new BrakeLineStateData
        {
            AnchorCarNetId = members[0],
            AnchorEnd = TrainEnd.Rear,
            MemberDigest = DigestMembers(members),
            PipePressure = brakeset.pipePressure
        };
        return true;
    }

    internal static bool TryApplyBrakeLine(BrakeLineStateData line)
    {
        if (line == null || !NetworkedTrainCar.TryGet(line.AnchorCarNetId, out TrainCar anchor) ||
            anchor?.brakeSystem?.brakeset == null)
            return false;
        Brakeset brakeset = anchor.brakeSystem.brakeset;
        ushort[] members = brakeset.cars
            .Select(system => system?.GetComponent<TrainCar>()?.GetNetId() ?? (ushort)0)
            .Where(netId => netId != 0).Distinct().OrderBy(netId => netId).ToArray();
        if (DigestMembers(members) != line.MemberDigest)
            return false;
        brakeset.pipePressure = line.PipePressure;
        foreach (BrakeSystem member in brakeset.cars)
            member.brakePipePressure = line.PipePressure;
        return true;
    }

    internal static bool TryApplyCommand(TrainTopologyCommand command, bool playAudio,
        out string failure)
    {
        failure = string.Empty;
        if (!TryResolve(command.Primary, out Coupler primary))
        {
            failure = "primary-endpoint-unavailable";
            return false;
        }
        Coupler secondary = null;
        if (command.Secondary.HasValue && !TryResolve(command.Secondary.Value, out secondary))
        {
            failure = "secondary-endpoint-unavailable";
            return false;
        }
        var chainsBefore = CaptureChains(new[] { primary, secondary, primary.coupledTo, secondary?.coupledTo });

        try
        {
            using (NetworkedTrainCar.BeginAuthoritativeTopologyApplication())
            {
                switch (command.Kind)
                {
                    case TrainTopologyCommandKind.Couple:
                        primary.CoupleTo(secondary, playAudio,
                            viaChainInteraction: !command.CascadeHose);
                        break;
                    case TrainTopologyCommandKind.Uncouple:
                        if (command.CascadeMultipleUnit)
                            DisconnectMu(primary, false);
                        CouplerLogic.Uncouple(primary, false,
                            viaChainInteraction: !command.CascadeHose, playAudio: playAudio);
                        ApplyChainState(primary, command.RequestedChainState);
                        break;
                    case TrainTopologyCommandKind.ConnectHose:
                        primary.ConnectAirHose(secondary, playAudio);
                        break;
                    case TrainTopologyCommandKind.DisconnectHose:
                        primary.DisconnectAirHose(playAudio);
                        break;
                    case TrainTopologyCommandKind.SetCock:
                        primary.IsCockOpen = command.RequestedValue;
                        break;
                    case TrainTopologyCommandKind.ConnectMultipleUnit:
                        if (!TryGetMuCable(primary, out MultipleUnitCable primaryCable) ||
                            !TryGetMuCable(secondary, out MultipleUnitCable secondaryCable))
                        {
                            failure = "mu-cable-unavailable";
                            return false;
                        }
                        primaryCable.Connect(secondaryCable, playAudio);
                        break;
                    case TrainTopologyCommandKind.DisconnectMultipleUnit:
                        DisconnectMu(primary, playAudio);
                        break;
                    case TrainTopologyCommandKind.SetChainState:
                        ApplyChainState(primary, command.RequestedChainState);
                        break;
                    default:
                        failure = "unsupported-command";
                        return false;
                }
            }
        }
        catch (Exception exception)
        {
            failure = $"native-{exception.GetType().Name}:{exception.Message}";
            return false;
        }

        if (!VerifyCommandPostcondition(command))
        {
            failure = "native-postcondition-failed";
            return false;
        }
        RefreshChangedChains(chainsBefore);
        return true;
    }

    /// <param name="deferDistantCouplings">
    /// Leave couplings between couplers more than <see cref="MaxCoupleDistance"/> apart (and their
    /// hoses and MU cables) for a retry, failing with couple-deferred-too-far once the rest is
    /// applied. For a client applying the host's topology after joining; never for the host.
    /// </param>
    internal static bool TryReconcile(TrainTopologyState desired,
        BrakeLineStateData[] brakeLines, out string failure, bool deferDistantCouplings = false)
    {
        failure = string.Empty;
        if (!TrainTopologyStateMachine.Validate(desired, out failure))
            return false;
        // Applied (via the retry) once the local player lets go of the chain.
        if (NetworkedTrainCar.IsLocalChainDragInProgress)
        {
            failure = "local-chain-drag-in-progress";
            return false;
        }
        foreach (TrainEndpointState endpoint in desired.Endpoints)
        {
            if (!TryResolve(endpoint.Endpoint, out _))
            {
                failure = "endpoint-not-ready:" + endpoint.Endpoint;
                return false;
            }
        }
        var chainsBefore = CaptureChains(desired.Endpoints.Select(endpoint =>
            TryResolve(endpoint.Endpoint, out Coupler coupler) ? coupler : null));
        var deferred = new Dictionary<TrainEndpointId, float>();

        try
        {
            using (NetworkedTrainCar.BeginAuthoritativeTopologyApplication())
            {
                DisconnectIncorrectMu(desired);
                DisconnectIncorrectMechanical(desired);
                DisconnectIncorrectHoses(desired);
                ConnectMechanical(desired, deferDistantCouplings ? deferred : null);
                ConnectHoses(desired, deferred);
                ConnectMu(desired, deferred);
                ApplyEndpointValues(desired, deferred);
                // A deferred coupling leaves its brake line split here, so no line would match;
                // the brake state heartbeat keeps the pressures right until the retry couples.
                if (deferred.Count == 0)
                    ApplyBrakeLines(brakeLines);
            }
        }
        catch (Exception exception)
        {
            failure = $"reconcile-{exception.GetType().Name}:{exception.Message}";
            return false;
        }

        if (deferred.Count > 0)
        {
            RefreshChangedChains(chainsBefore);
            failure = CoupleDeferredFailure +
                string.Join(",", deferred.Select(pair => $"{pair.Key} {pair.Value:F1} m"));
            return false;
        }

        if (!VerifyTopology(desired, out failure))
            return false;
        RefreshChangedChains(chainsBefore);
        foreach (BrakeLineStateData line in brakeLines ?? Array.Empty<BrakeLineStateData>())
        {
            if (!TryCaptureBrakeLine(line.AnchorCarNetId, out BrakeLineStateData actual) ||
                actual.MemberDigest != line.MemberDigest ||
                Math.Abs(actual.PipePressure - line.PipePressure) > 0.001f)
            {
                failure = "brake-line-postcondition-mismatch:" + line.AnchorCarNetId;
                return false;
            }
        }
        return true;
    }

    internal static string Fingerprint(TrainTopologyState state)
    {
        if (state == null)
            return string.Empty;
        return string.Join("|", state.Endpoints.OrderBy(value => value.Endpoint).Select(value =>
            $"{value.Endpoint};{Partner(value.MechanicalPartner)};{Partner(value.HosePartner)};" +
            $"{Partner(value.MultipleUnitPartner)};{(byte)value.ChainState};" +
            $"{value.CockOpen};{value.PreventAutoCouple};{(byte)value.Capabilities}"));
    }

    private static TrainEndpointState CaptureEndpoint(NetworkedTrainCar networkedCar, Coupler coupler)
    {
        TrainEndpointCapabilities capabilities = TrainEndpointCapabilities.None;
        if (coupler != null)
            capabilities |= TrainEndpointCapabilities.Mechanical;
        if (coupler?.hoseAndCock != null)
            capabilities |= TrainEndpointCapabilities.Hose | TrainEndpointCapabilities.Cock;
        if (TryGetMuCable(coupler, out _))
            capabilities |= TrainEndpointCapabilities.MultipleUnit;

        return new TrainEndpointState
        {
            Endpoint = Id(networkedCar.NetId, coupler?.isFrontCoupler ?? false),
            Capabilities = capabilities,
            MechanicalPartner = EndpointFor(coupler?.coupledTo),
            HosePartner = HosePartnerFor(coupler),
            MultipleUnitPartner = MuPartnerFor(coupler),
            ChainState = ToCore(coupler?.state ?? ChainCouplerInteraction.State.Parked),
            CockOpen = coupler?.hoseAndCock != null && coupler.IsCockOpen,
            PreventAutoCouple = coupler?.preventAutoCouple ?? false
        };
    }

    private static TrainEndpointId Id(ushort carNetId, bool front) =>
        new(carNetId, front ? TrainEnd.Front : TrainEnd.Rear);

    private static TrainEndpointId? EndpointFor(Coupler coupler)
    {
        ushort netId = coupler?.train?.GetNetId() ?? 0;
        return netId == 0 ? (TrainEndpointId?)null : Id(netId, coupler.isFrontCoupler);
    }

    private static TrainEndpointId? HosePartnerFor(Coupler coupler)
    {
        if (coupler?.hoseAndCock?.connectedTo == null ||
            !NetworkedTrainCar.TryGetCoupler(coupler.hoseAndCock.connectedTo, out Coupler partner))
            return null;
        return EndpointFor(partner);
    }

    private static TrainEndpointId? MuPartnerFor(Coupler coupler)
    {
        if (!TryGetMuCable(coupler, out MultipleUnitCable cable) || cable.connectedTo == null)
            return null;
        TrainCar train = cable.connectedTo.muModule?.train;
        ushort netId = train?.GetNetId() ?? 0;
        return netId == 0 ? (TrainEndpointId?)null : Id(netId, cable.connectedTo.isFront);
    }

    // Applied coupling changes go through the chain-interaction path, which the chain adapter
    // deliberately ignores, so a chain that is active near a player kept showing its old state
    // (parked while coupled, hooked while uncoupled). Re-enabling the chain makes it work out its
    // state from the coupler again - the same refresh the menu patch already does. Inactive chains
    // do this themselves when they activate.
    private static Dictionary<Coupler, (Coupler CoupledTo, ChainCouplerInteraction.State State)> CaptureChains(
        IEnumerable<Coupler> couplers)
    {
        var before = new Dictionary<Coupler, (Coupler, ChainCouplerInteraction.State)>();
        foreach (Coupler coupler in couplers)
            if (coupler != null)
                before[coupler] = (coupler.coupledTo, coupler.state);
        return before;
    }

    private static void RefreshChangedChains(Dictionary<Coupler, (Coupler CoupledTo, ChainCouplerInteraction.State State)> before)
    {
        using (NetworkedTrainCar.BeginAuthoritativeTopologyApplication())
        {
            foreach (var entry in before)
            {
                Coupler coupler = entry.Key;
                if (coupler == null || (coupler.coupledTo == entry.Value.CoupledTo && coupler.state == entry.Value.State))
                    continue;
                RefreshChainVisual(coupler);
                RefreshChainVisual(entry.Value.CoupledTo);
                RefreshChainVisual(coupler.coupledTo);
            }
        }
    }

    internal static void RefreshChainVisual(Coupler coupler)
    {
        ChainCouplerInteraction chain = coupler != null ? coupler.ChainScript : null;
        if (chain == null || !chain.isActiveAndEnabled)
            return;
        chain.enabled = false;
        chain.enabled = true;
    }

    private static bool TryResolve(TrainEndpointId id, out Coupler coupler)
    {
        coupler = null;
        if (!id.IsValid || !NetworkedTrainCar.TryGet(id.CarNetId, out TrainCar car) || car == null)
            return false;
        coupler = id.End == TrainEnd.Front ? car.frontCoupler : car.rearCoupler;
        return coupler != null;
    }

    internal static bool TryGetMuCable(Coupler coupler, out MultipleUnitCable cable)
    {
        cable = null;
        if (coupler?.train == null || !coupler.train.IsMultipleUnit || coupler.train.muModule == null)
            return false;
        cable = coupler.isFrontCoupler
            ? coupler.train.muModule.frontCable
            : coupler.train.muModule.rearCable;
        return cable != null;
    }

    private static void DisconnectMu(Coupler coupler, bool playAudio)
    {
        if (TryGetMuCable(coupler, out MultipleUnitCable cable) && cable.IsConnected)
            cable.Disconnect(playAudio);
    }

    private static bool VerifyCommandPostcondition(TrainTopologyCommand command)
    {
        if (!TryResolve(command.Primary, out Coupler primary))
            return false;
        Coupler secondary = null;
        if (command.Secondary.HasValue && !TryResolve(command.Secondary.Value, out secondary))
            return false;
        switch (command.Kind)
        {
            case TrainTopologyCommandKind.Couple:
                return secondary != null && primary.coupledTo == secondary &&
                       secondary.coupledTo == primary &&
                       (primary.rigidCJ != null || secondary.rigidCJ != null) &&
                       primary.train.trainset == secondary.train.trainset &&
                       Math.Abs(primary.train.indexInTrainset - secondary.train.indexInTrainset) == 1;
            case TrainTopologyCommandKind.Uncouple:
                return primary.coupledTo == null &&
                       (secondary == null || secondary.coupledTo == null);
            case TrainTopologyCommandKind.ConnectHose:
                return secondary != null && primary.hoseAndCock.connectedTo == secondary.hoseAndCock &&
                       secondary.hoseAndCock.connectedTo == primary.hoseAndCock;
            case TrainTopologyCommandKind.DisconnectHose:
                return !primary.hoseAndCock.IsHoseConnected;
            case TrainTopologyCommandKind.SetCock:
                return primary.IsCockOpen == command.RequestedValue;
            case TrainTopologyCommandKind.ConnectMultipleUnit:
                return TryGetMuCable(primary, out MultipleUnitCable first) &&
                       TryGetMuCable(secondary, out MultipleUnitCable second) &&
                       first.connectedTo == second && second.connectedTo == first;
            case TrainTopologyCommandKind.DisconnectMultipleUnit:
                return !TryGetMuCable(primary, out MultipleUnitCable cable) || !cable.IsConnected;
            case TrainTopologyCommandKind.SetChainState:
                return ToCore(primary.state) == command.RequestedChainState;
            default:
                return false;
        }
    }

    private static void DisconnectIncorrectMu(TrainTopologyState desired)
    {
        foreach (TrainEndpointState state in desired.Endpoints)
        {
            TryResolve(state.Endpoint, out Coupler coupler);
            if (!TryGetMuCable(coupler, out MultipleUnitCable cable) || cable.connectedTo == null)
                continue;
            TrainEndpointId? actual = MuPartnerFor(coupler);
            if (actual != state.MultipleUnitPartner && state.Endpoint.CompareTo(actual ?? default) < 0)
                cable.Disconnect(false);
        }
    }

    private static void DisconnectIncorrectMechanical(TrainTopologyState desired)
    {
        foreach (TrainEndpointState state in desired.Endpoints)
        {
            TryResolve(state.Endpoint, out Coupler coupler);
            TrainEndpointId? actual = EndpointFor(coupler.coupledTo);
            if (actual != state.MechanicalPartner && actual.HasValue &&
                state.Endpoint.CompareTo(actual.Value) < 0)
                coupler.Uncouple(false, false, false, true);
        }
    }

    private static void DisconnectIncorrectHoses(TrainTopologyState desired)
    {
        foreach (TrainEndpointState state in desired.Endpoints)
        {
            TryResolve(state.Endpoint, out Coupler coupler);
            TrainEndpointId? actual = HosePartnerFor(coupler);
            if (actual != state.HosePartner && actual.HasValue &&
                state.Endpoint.CompareTo(actual.Value) < 0)
                coupler.DisconnectAirHose(false);
        }
    }

    /// <summary>
    /// Couplers further apart than this aren't coupled yet. Vanilla makes the joint as long as the
    /// gap and then reels the cars in, which flings them; the physics corrections bring the cars
    /// together first (#64).
    /// </summary>
    internal const float MaxCoupleDistance = 2f;
    internal const string CoupleDeferredFailure = "couple-deferred-too-far:";

    /// <param name="deferred">Given: couplings whose couplers are too far apart are left out and
    /// added here (both endpoints, with the gap).</param>
    private static void ConnectMechanical(TrainTopologyState desired, Dictionary<TrainEndpointId, float> deferred)
    {
        var byEndpoint = desired.Endpoints.ToDictionary(value => value.Endpoint);
        foreach (TrainEndpointState state in desired.Endpoints.OrderBy(value => value.Endpoint))
        {
            if (!state.MechanicalPartner.HasValue ||
                state.Endpoint.CompareTo(state.MechanicalPartner.Value) >= 0)
                continue;
            TryResolve(state.Endpoint, out Coupler first);
            TryResolve(state.MechanicalPartner.Value, out Coupler second);
            byEndpoint.TryGetValue(state.MechanicalPartner.Value,
                out TrainEndpointState partnerState);

            // Coupler.CoupleTo creates the ConfigurableJoint on the receiver. Only that
            // endpoint's SetChainTight call can then change the native chain FSM. Preserve
            // the host's joint-owning endpoint instead of choosing by NetId ordering.
            bool secondOwnsChain = !IsAttached(state.ChainState) &&
                IsAttached(partnerState?.ChainState ?? TrainChainState.Parked);
            Coupler owner = secondOwnsChain ? second : first;
            Coupler other = secondOwnsChain ? first : second;

            bool correctPair = first.coupledTo == second && second.coupledTo == first;
            float gap = UnityEngine.Vector3.Distance(first.transform.position, second.transform.position);
            if (!correctPair && deferred != null && gap > MaxCoupleDistance)
            {
                deferred[state.Endpoint] = gap;
                deferred[state.MechanicalPartner.Value] = gap;
                continue;
            }
            // With both chains attached (#285) the snapshot doesn't say which end has the joint,
            // so either will do: recoupling would only jolt the cars.
            bool bothAttached = IsAttached(state.ChainState) &&
                IsAttached(partnerState?.ChainState ?? TrainChainState.Parked);
            bool wrongJointOwner = correctPair && !bothAttached && IsAttached(
                secondOwnsChain ? partnerState.ChainState : state.ChainState) &&
                owner.rigidCJ == null && other.rigidCJ != null;
            if (wrongJointOwner)
            {
                first.Uncouple(false, false, false, true);
                correctPair = false;
            }
            if (!correctPair)
                owner.CoupleTo(other, false, true);
        }
    }

    private static bool IsAttached(TrainChainState state) =>
        state == TrainChainState.AttachedLoose || state == TrainChainState.AttachedTight;

    private static void ConnectHoses(TrainTopologyState desired, Dictionary<TrainEndpointId, float> deferred)
    {
        foreach (TrainEndpointState state in desired.Endpoints.OrderBy(value => value.Endpoint))
        {
            if (!state.HosePartner.HasValue || state.Endpoint.CompareTo(state.HosePartner.Value) >= 0 ||
                deferred.ContainsKey(state.Endpoint) || deferred.ContainsKey(state.HosePartner.Value))
                continue;
            TryResolve(state.Endpoint, out Coupler first);
            TryResolve(state.HosePartner.Value, out Coupler second);
            if (first.hoseAndCock.connectedTo != second.hoseAndCock)
                first.ConnectAirHose(second, false);
        }
    }

    private static void ConnectMu(TrainTopologyState desired, Dictionary<TrainEndpointId, float> deferred)
    {
        foreach (TrainEndpointState state in desired.Endpoints.OrderBy(value => value.Endpoint))
        {
            if (!state.MultipleUnitPartner.HasValue ||
                state.Endpoint.CompareTo(state.MultipleUnitPartner.Value) >= 0 ||
                deferred.ContainsKey(state.Endpoint) || deferred.ContainsKey(state.MultipleUnitPartner.Value))
                continue;
            TryResolve(state.Endpoint, out Coupler first);
            TryResolve(state.MultipleUnitPartner.Value, out Coupler second);
            if (TryGetMuCable(first, out MultipleUnitCable firstCable) &&
                TryGetMuCable(second, out MultipleUnitCable secondCable) &&
                firstCable.connectedTo != secondCable)
                firstCable.Connect(secondCable, false);
        }
    }

    private static void ApplyEndpointValues(TrainTopologyState desired, Dictionary<TrainEndpointId, float> deferred)
    {
        foreach (TrainEndpointState state in desired.Endpoints)
        {
            TryResolve(state.Endpoint, out Coupler coupler);
            coupler.preventAutoCouple = state.PreventAutoCouple;
            if (coupler.hoseAndCock != null && coupler.IsCockOpen != state.CockOpen)
                coupler.IsCockOpen = state.CockOpen;
            if (!deferred.ContainsKey(state.Endpoint)) // its chain goes on with the coupling
                ApplyChainState(coupler, state.ChainState);
        }
    }

    // SetChainTight changes only the joint's coupler: on the other end of a coupling it does
    // nothing. Vanilla can still leave that end's chain attached too: a spawned car auto-couples
    // (its own coupler takes the joint), and the parked car's chain, switched on because a player
    // is near, hooks itself on when told of the coupling (#285). Its state is then set as is.
    private static void ApplyChainState(Coupler coupler, TrainChainState state)
    {
        if (coupler == null || ToCore(coupler.state) == state)
            return;
        if (coupler.IsCoupled() && coupler.rigidCJ != null && IsAttached(state))
            coupler.SetChainTight(state == TrainChainState.AttachedTight);
        else
            coupler.state = ToNative(state);
    }

    private static void ApplyBrakeLines(IEnumerable<BrakeLineStateData> lines)
    {
        foreach (BrakeLineStateData line in lines ?? Array.Empty<BrakeLineStateData>())
            if (!TryApplyBrakeLine(line))
                throw new InvalidOperationException(
                    "brake-line-membership-mismatch:" + line.AnchorCarNetId);
    }

    private static bool VerifyTopology(TrainTopologyState desired, out string failure)
    {
        TrainTopologyState actual = Capture(desired.Revision);
        var actualByEndpoint = actual.Endpoints.ToDictionary(value => value.Endpoint);
        foreach (TrainEndpointState expected in desired.Endpoints.OrderBy(value => value.Endpoint))
        {
            if (!actualByEndpoint.TryGetValue(expected.Endpoint, out TrainEndpointState observed))
            {
                failure = "topology-postcondition-missing:" + expected.Endpoint;
                return false;
            }

            string expectedFingerprint = Fingerprint(new TrainTopologyState(desired.Revision,
                new[] { expected }));
            string observedFingerprint = Fingerprint(new TrainTopologyState(desired.Revision,
                new[] { observed }));
            if (!string.Equals(expectedFingerprint, observedFingerprint, StringComparison.Ordinal))
            {
                failure = $"topology-postcondition-mismatch:{expected.Endpoint}:" +
                          $"expected={expectedFingerprint}:actual={observedFingerprint}";
                return false;
            }
        }

        // Ignore additional locally registered standalone cars here. Live train creation
        // can initialize the native object one frame before its authoritative topology
        // snapshot arrives; those cars must not prevent the preceding revision from
        // committing. Any connection from a desired endpoint to an extra car is still
        // caught above because that endpoint's partner fingerprint will differ.
        foreach (TrainEndpointState state in desired.Endpoints)
        {
            if (!state.MechanicalPartner.HasValue ||
                state.Endpoint.CompareTo(state.MechanicalPartner.Value) >= 0)
                continue;
            TryResolve(state.Endpoint, out Coupler first);
            TryResolve(state.MechanicalPartner.Value, out Coupler second);
            if ((first.rigidCJ == null && second.rigidCJ == null) ||
                first.train.trainset != second.train.trainset ||
                Math.Abs(first.train.indexInTrainset - second.train.indexInTrainset) != 1)
            {
                failure = "mechanical-derived-state-mismatch:" + state.Endpoint;
                return false;
            }
        }
        failure = string.Empty;
        return true;
    }

    private static uint DigestMembers(IEnumerable<ushort> members)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        foreach (ushort member in members)
        {
            hash = (hash ^ (byte)member) * prime;
            hash = (hash ^ (byte)(member >> 8)) * prime;
        }
        return hash;
    }

    private static string Partner(TrainEndpointId? value) =>
        value.HasValue ? value.Value.ToString() : "-";

    private static TrainChainState ToCore(ChainCouplerInteraction.State state)
    {
        switch (state)
        {
            case ChainCouplerInteraction.State.Dangling: return TrainChainState.Dangling;
            case ChainCouplerInteraction.State.Being_Dragged: return TrainChainState.BeingDragged;
            case ChainCouplerInteraction.State.Attached_Loose: return TrainChainState.AttachedLoose;
            case ChainCouplerInteraction.State.Attached_Tight: return TrainChainState.AttachedTight;
            case ChainCouplerInteraction.State.Disabled: return TrainChainState.Disabled;
            default: return TrainChainState.Parked;
        }
    }

    private static ChainCouplerInteraction.State ToNative(TrainChainState state)
    {
        switch (state)
        {
            case TrainChainState.Dangling: return ChainCouplerInteraction.State.Dangling;
            case TrainChainState.BeingDragged: return ChainCouplerInteraction.State.Being_Dragged;
            case TrainChainState.AttachedLoose: return ChainCouplerInteraction.State.Attached_Loose;
            case TrainChainState.AttachedTight: return ChainCouplerInteraction.State.Attached_Tight;
            case TrainChainState.Disabled: return ChainCouplerInteraction.State.Disabled;
            default: return ChainCouplerInteraction.State.Parked;
        }
    }
}
