using DV;
using DV.CabControls;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Physics;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.Physics;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Multiplayer.Shared.Items.Physics.NetworkedItemSpatialManager;

namespace Multiplayer.Server.Items.Physics;

/// <summary>
/// The host's side of the spatial stream (<see cref="NetworkedItemSpatialManager"/>): granting a
/// lease when a player lets go of an item, accepting and relaying its simulator's samples,
/// transferring it when the simulator goes quiet or leaves, and committing the settled pose to
/// the authority record. It works on the Shared manager's lease table.
/// </summary>
internal sealed class ServerItemSpatial
{
    private readonly NetworkedItemSpatialManager spatial;
    private readonly ServerItemManager manager;
    private readonly Dictionary<ushort, (NetworkedItem Item, uint Revision, byte Simulator,
        ItemSpatialPhase Phase)> pendingHostBegins = new();

    internal ServerItemSpatial(NetworkedItemSpatialManager spatial, ServerItemManager manager)
    {
        this.spatial = spatial;
        this.manager = manager;
    }

    private Dictionary<ushort, Lease> leases => spatial.Leases;
    private Dictionary<ushort, ItemSpatialStateData> committedStates => spatial.CommittedStates;
    private Dictionary<ushort, uint> epochs => spatial.Epochs;

    internal void Clear() => pendingHostBegins.Clear();

    /// <summary>
    /// The items <paramref name="playerId"/> simulates under a lease now: theirs to keep in interest
    /// until it ends. Retired mid-flight, its samples stopped and it settled where it last was.
    /// </summary>
    internal IEnumerable<NetworkedItem> SimulatedBy(byte playerId)
    {
        foreach (Lease lease in leases.Values)
            if (lease.SimulatorPlayerId == playerId && NetworkedItem.TryGet(lease.ItemNetId, out NetworkedItem item) &&
                item != null)
                yield return item;
    }

    internal void Update()
    {
        if (NetworkLifecycle.Instance == null || !NetworkLifecycle.Instance.IsHost())
            return;
        ProcessPendingHostBegins();
        UpdateHostLeases();
    }

    // The host simulates this lease itself: it takes its own samples as a client's would be.
    private void SampleHostLease(NetworkedItem item, Lease lease)
    {
        if (!spatial.TrySampleLocalLease(item, lease, host: true, out ItemSpatialStateData state, out bool settled))
            return;
        // Null on a host with no player of its own: the server simulates as itself (HostItemActor).
        ServerPlayer actor = HostItemActor.Player;
        bool hostActs = actor != null || HostItemActor.IsServer;
        string rejection = "unknown-host-simulator";
        if (settled)
        {
            if (hostActs && TryAcceptHostSample(state, actor, settlement: true, out _, out _, out rejection,
                    serverSample: actor == null))
            {
                ApplyAcceptedHostSample(item, lease, state);
                CommitHostSettlement(item, lease, state, "host-settlement");
            }
            else if (hostActs)
            {
                // Its last sample, refused: it settles where it last was good, or the lease ends
                // (OI-PHY-04). Left open, it sampled nothing more and never ended.
                Reject(state, actor, rejection ?? "host-settlement-rejected");
                CommitHostSettlement(item, lease, lease.LastAccepted, $"host-settlement-rejected:{rejection}");
            }
            else
                Reject(state, actor, rejection ?? "host-settlement-rejected");
            return;
        }

        if (hostActs && TryAcceptHostSample(state, actor, settlement: false, out _, out _, out rejection,
                serverSample: actor == null))
        {
            ApplyAcceptedHostSample(item, lease, state);
            NetworkServer.Instance.SendItemSpatialSample(item,
                new ClientboundItemSpatialSamplePacket { State = state.Clone() }, lease.SimulatorPlayerId);
            Publish("item.spatial-sample-relayed", item.NetId, SpatialDebug(state), highFrequency: true);
            CommitParentChangeIfDue(item, lease, state);
        }
        else
            Reject(state, actor, rejection ?? "host-sample-rejected");
    }

    internal void OnAuthoritativeTransitionCommitted(NetworkedItem item, ItemUpdate snapshot,
        LiveItems.LiveItem record, ServerPlayer actor, ItemPlacementKind previousPlacement,
        bool appliesPlacement)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null || record == null)
            return;

        bool releasedByPlayer = snapshot.ItemState is ItemState.Dropped or ItemState.Thrown &&
            (previousPlacement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory ||
             snapshot.ItemState == ItemState.Thrown);
        // No actor: the server's own release, on a host with no player (HostItemActor.IsServer).
        bool released = releasedByPlayer && (actor != null || HostItemActor.IsServer);
        switch (ItemLeaseRules.AfterChange(appliesPlacement, (LivePlacement)(byte)record.Placement, released))
        {
            case LeaseAfterChange.Begins:
                pendingHostBegins[item.NetId] = (item, record.Revision,
                    actor?.PlayerId ?? HostItemActor.ServerSimulatorId,
                    snapshot.ItemState == ItemState.Thrown ? ItemSpatialPhase.InFlight : ItemSpatialPhase.Sliding);
                return;
            case LeaseAfterChange.Ends:
                // Anywhere else, and the server moving it about the world itself: the old lease
                // simulates a place the item has left, so it ends here, not at its next sample.
                EndLease(item.NetId, "placement:" + record.Placement);
                return;
        }
    }

    /// <summary>
    /// A committed change moved the item out of its lease's hands (a recall, a Lost and Found
    /// collection, its record gone): the lease ends now and its simulator is told (OI-PHY-02).
    /// </summary>
    internal void EndLease(ushort itemNetId, string reason)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        pendingHostBegins.Remove(itemNetId);
        RevokeHostLease(itemNetId, reason);
        committedStates.Remove(itemNetId);
    }

    internal bool ResetAuthoredItemToBaseline(NetworkedItem item,
        LiveItems.LiveItem record)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null || record == null ||
            item.NetId == 0 || record.NetId != item.NetId ||
            !WorldItemStableIdentity.IsValid(record.AuthoredItemKey))
            return false;

        RevokeHostLease(item.NetId, "authored-slot-reset", notify: true);
        pendingHostBegins.Remove(item.NetId);
        uint epoch = epochs.TryGetValue(item.NetId, out uint previous) ? previous + 1 : 1;
        epochs[item.NetId] = epoch;
        ItemSpatialStateData state = new()
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            SimulationEpoch = epoch,
            SampleSequence = 0,
            SourceTick = NetworkLifecycle.Instance.Tick,
            SimulatorPlayerId = 0,
            Phase = ItemSpatialPhase.Settled,
            AbsolutePosition = record.BaselinePosition,
            Rotation = record.BaselineRotation,
            LinearVelocity = Vector3.zero,
            AngularVelocity = Vector3.zero,
            WorldParentKind = ItemWorldParentKind.World,
            ParentLocalRotation = Quaternion.identity,
            Sleeping = true
        };
        if (!LiveItems.TryCommitSpatialPose(item, record.Revision, state,
                out LiveItems.LiveItem committed, out string rejection))
        {
            Publish("item.authored-slot-reset-rejected", item.NetId, new()
            {
                ["reason"] = rejection ?? string.Empty,
                ["authoredItemKey"] = record.AuthoredItemKey
            });
            return false;
        }

        state.AuthorityRevision = committed.Revision;
        committed.HasPersistentOverride = false;
        committedStates[item.NetId] = state.Clone();
        ApplyPose(item, state, immediate: true);
        ApplyCommitMetadata(item, state);
        manager.RefreshHostSpatialPosition(item, state.AbsolutePosition, state.WorldParentKind);
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.Sleep();
        }
        NetworkServer.Instance.SendItemSpatialCommit(item,
            new ClientboundItemSpatialCommitPacket { State = state });
        return true;
    }

    internal void OnPlayerDisconnected(byte playerId)
    {
        if (!NetworkLifecycle.Instance.IsHost() || playerId == 0)
            return;
        foreach (Lease lease in leases.Values.Where(value => value.SimulatorPlayerId == playerId).ToArray())
        {
            if (!NetworkedItem.TryGet(lease.ItemNetId, out NetworkedItem item) || item == null)
            {
                RevokeHostLease(lease.ItemNetId, "simulator-disconnected");
                continue;
            }
            if (!TryTransferLease(item, lease, "simulator-disconnected"))
                CommitHostSettlement(item, lease, lease.LastAccepted, "simulator-disconnected");
        }
    }

    private void ProcessPendingHostBegins()
    {
        foreach (var pair in pendingHostBegins.ToArray())
        {
            pendingHostBegins.Remove(pair.Key);
            var pending = pair.Value;
            if (pending.Item == null || !LiveItems.TryGet(pair.Key, out var record) ||
                !LiveItems.PlacementUnchangedSince(record, pending.Revision) ||
                record.Placement is not (ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
                    ItemPlacementKind.StaticParent))
                continue;
            BeginHostLease(pending.Item, record, pending.Simulator, pending.Phase);
        }
    }

    private void BeginHostLease(NetworkedItem item, LiveItems.LiveItem record,
        byte simulatorPlayerId, ItemSpatialPhase phase)
    {
        // Simulator 0 is the server itself, only on a host with no player of its own.
        if (item == null || record == null ||
            simulatorPlayerId == HostItemActor.ServerSimulatorId && !HostItemActor.IsServer)
            return;
        // The new lease's presentation sets the body up (a proxy is stopped, a local simulator keeps
        // its motion), as a client's game and a lease transfer do. Stopping it here lost the force
        // of a throw the host made of an item still under its last lease (one just dropped).
        RevokeHostLease(item.NetId, "superseded", notify: false, restoreBody: false);
        uint epoch = epochs.TryGetValue(item.NetId, out uint previous) ? previous + 1 : 1;
        epochs[item.NetId] = epoch;
        ItemSpatialStateData baseline = Capture(item, record.Revision, epoch, 0, simulatorPlayerId, phase);
        float now = Time.realtimeSinceStartup;
        Lease lease = new()
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            Epoch = epoch,
            SimulatorPlayerId = simulatorPlayerId,
            StartedAt = now,
            LastAcceptedAt = now,
            LastAccepted = baseline.Clone(),
            Target = baseline.Clone()
        };
        leases[item.NetId] = lease;

        bool localSimulator = simulatorPlayerId == HostItemActor.SimulatorId;
        ConfigureLeasePresentation(item, localSimulator, baseline);
        NetworkServer.Instance.SendItemSpatialLease(item, new ClientboundItemSpatialLeasePacket
        {
            Active = true,
            Reason = "world-release",
            State = baseline
        });
        Publish("item.spatial-lease-granted", item.NetId, new()
        {
            ["simulationEpoch"] = epoch,
            ["simulatorPlayerId"] = simulatorPlayerId,
            ["authorityRevision"] = record.Revision,
            ["phase"] = phase.ToString(),
            ["worldParentKind"] = baseline.WorldParentKind.ToString()
        });
    }

    // Ends a physics lease from outside (e.g. the item fell through the map), telling its simulator.
    internal void RevokeLease(ushort itemNetId, string reason) => RevokeHostLease(itemNetId, reason);

    private void RevokeHostLease(ushort itemNetId, string reason, bool notify = true, bool restoreBody = true)
    {
        if (!leases.TryGetValue(itemNetId, out Lease lease))
            return;
        leases.Remove(itemNetId);
        spatial.LeaseEnds[itemNetId] = "revoked:" + (reason ?? string.Empty);
        if (restoreBody)
            RestoreLocalBodyAfterLease(lease);
        if (notify && NetworkedItem.TryGet(itemNetId, out NetworkedItem item) && item != null)
            NetworkServer.Instance.SendItemSpatialLease(item, new ClientboundItemSpatialLeasePacket
            {
                Active = false,
                Reason = reason ?? string.Empty,
                State = lease.LastAccepted?.Clone()
            });
        Publish("item.spatial-lease-revoked", itemNetId, new()
        {
            ["simulationEpoch"] = lease.Epoch,
            ["simulatorPlayerId"] = lease.SimulatorPlayerId,
            ["reason"] = reason ?? string.Empty
        });
    }

    internal void ReceiveClientSample(ItemSpatialStateData state, ServerPlayer sender)
    {
        if (!TryAcceptHostSample(state, sender, settlement: false, out Lease lease, out NetworkedItem item,
                out string rejection))
        {
            Reject(state, sender, rejection);
            TerminateInvalidCurrentLease(state, sender, rejection);
            return;
        }
        ApplyAcceptedHostSample(item, lease, state);
        NetworkServer.Instance.SendItemSpatialSample(item,
            new ClientboundItemSpatialSamplePacket { State = state.Clone() }, sender.PlayerId);
        Publish("item.spatial-sample-relayed", item.NetId, SpatialDebug(state), highFrequency: true);
        CommitParentChangeIfDue(item, lease, state);
    }

    internal void ReceiveClientSettlement(ItemSpatialStateData state, ServerPlayer sender)
    {
        if (!TryAcceptHostSample(state, sender, settlement: true, out Lease lease, out NetworkedItem item,
                out string rejection))
        {
            Reject(state, sender, rejection);
            TerminateInvalidCurrentLease(state, sender, rejection);
            return;
        }
        ApplyAcceptedHostSample(item, lease, state);
        CommitHostSettlement(item, lease, state, "client-settlement");
    }

    internal bool TryBeginTrainWake(ushort itemNetId, byte preferredSimulatorPlayerId,
        Vector3 initialLocalVelocity, string reason, out byte simulatorPlayerId,
        out string rejection)
    {
        simulatorPlayerId = 0;
        rejection = string.Empty;
        if (!NetworkLifecycle.Instance.IsHost())
        {
            rejection = "host-required";
            return false;
        }
        if (leases.ContainsKey(itemNetId))
        {
            rejection = "item-already-active";
            return false;
        }
        if (!NetworkedItem.TryGet(itemNetId, out NetworkedItem item) || item == null ||
            !LiveItems.TryGet(itemNetId, out LiveItems.LiveItem record))
        {
            rejection = "item-representation-unavailable";
            return false;
        }
        if (record.Placement != ItemPlacementKind.TrainInterior ||
            record.WorldParentKind != ItemWorldParentKind.TrainInterior || record.WorldParentNetId == 0)
        {
            rejection = "item-not-settled-in-train";
            return false;
        }
        if (!TrySelectTrainWakeSimulator(item, record, preferredSimulatorPlayerId,
                out byte simulator))
        {
            rejection = "no-eligible-train-item-simulator";
            return false;
        }

        ItemSpatialStateData baseline;
        if (!committedStates.TryGetValue(itemNetId, out ItemSpatialStateData committed) ||
            committed == null ||
            !LiveItems.PlacementUnchangedSince(record, committed.AuthorityRevision) ||
            committed.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            committed.WorldParentNetId != record.WorldParentNetId)
        {
            baseline = new ItemSpatialStateData
            {
                ItemNetId = itemNetId,
                AuthorityRevision = record.Revision,
                AbsolutePosition = record.Position,
                Rotation = record.Rotation,
                WorldParentKind = ItemWorldParentKind.TrainInterior,
                WorldParentNetId = record.WorldParentNetId,
                ParentLocalPosition = record.ParentLocalPosition,
                ParentLocalRotation = record.ParentLocalRotation,
                Phase = ItemSpatialPhase.Settled,
                Sleeping = true
            };
        }
        else
            baseline = committed.Clone();

        uint epoch = epochs.TryGetValue(itemNetId, out uint previous) ? previous + 1 :
            Math.Max(1u, baseline.SimulationEpoch + 1);
        epochs[itemNetId] = epoch;
        baseline.AuthorityRevision = record.Revision;
        baseline.SimulationEpoch = epoch;
        baseline.SampleSequence = 0;
        baseline.SourceTick = NetworkLifecycle.Instance.Tick;
        baseline.SimulatorPlayerId = simulator;
        baseline.Phase = ItemSpatialPhase.Sliding;
        baseline.LinearVelocity = Vector3.ClampMagnitude(initialLocalVelocity, 6f);
        baseline.AngularVelocity = Vector3.zero;
        baseline.Sleeping = false;
        float now = Time.realtimeSinceStartup;
        Lease lease = new()
        {
            ItemNetId = itemNetId,
            AuthorityRevision = record.Revision,
            Epoch = epoch,
            SimulatorPlayerId = simulator,
            StartedAt = now,
            LastAcceptedAt = now,
            LastAccepted = baseline.Clone(),
            Target = baseline.Clone()
        };
        leases[itemNetId] = lease;
        simulatorPlayerId = simulator;
        bool localHost = simulator == HostItemActor.SimulatorId;
        ConfigureLeasePresentation(item, localHost, baseline);
        if (localHost)
            ApplySimulationVelocity(item, baseline);
        NetworkServer.Instance.SendItemSpatialLease(item,
            new ClientboundItemSpatialLeasePacket
            {
                Active = true,
                Reason = "train-wake:" + (reason ?? "unknown"),
                State = baseline.Clone()
            });
        Publish("item.spatial-train-wake-granted", itemNetId, new(SpatialDebug(baseline))
        {
            ["reason"] = reason ?? string.Empty
        });
        return true;
    }

    private bool TrySelectTrainWakeSimulator(NetworkedItem item,
        LiveItems.LiveItem record, byte preferredPlayerId, out byte selected)
    {
        selected = 0;
        if (NetworkServer.Instance == null || item == null || record == null)
            return false;
        Vector3 absolute = record.Position;
        if (NetworkedTrainCar.TryGet(record.WorldParentNetId, out TrainCar parentCar) &&
            parentCar != null)
            absolute = (parentCar.interior ?? parentCar.transform)
                .TransformPoint(record.ParentLocalPosition) - WorldMover.currentMove;
        SimulatorCandidate[] candidates = Candidates(item, absolute, record.WorldParentNetId).ToArray();
        if (ItemSimulatorChoice.ForWake(candidates, preferredPlayerId, MaximumSimulatorDistance, out selected))
            return true;
        // Nobody has it yet: the players in range are sent it, and the wake, pending, finds one of
        // them once their game has it (OI-PHY-12; before, a client had to know the item already).
        foreach (byte lacking in ItemSimulatorChoice.Lacking(candidates, MaximumSimulatorDistance))
            if (NetworkServer.Instance.ServerPlayers.FirstOrDefault(p => p != null && p.PlayerId == lacking) is { } player)
                manager.PinInterest(player, item, WakeInterestSeconds);
        return false;
    }

    // How long a pending wake keeps its item in a would-be simulator's interest: as long as the wake
    // waits (TrainItemWakeServer.PendingWakeSeconds).
    private const float WakeInterestSeconds = 10f;

    /// <summary>
    /// Who could simulate the item at <paramref name="absolute"/> (ItemSimulatorChoice): every
    /// player with a world, and on a dedicated host the server itself where its world is solid
    /// (OI-PHY-07). A player has it once projected and acknowledged; the host's own world, while
    /// its object is active.
    /// </summary>
    private static IEnumerable<SimulatorCandidate> Candidates(NetworkedItem item, Vector3 absolute,
        ushort parentCarNetId)
    {
        ServerPlayer hostPlayer = HostItemActor.Player;
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
        {
            if (player == null || player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;
            bool host = player == hostPlayer;
            bool hasItem = host
                ? item.gameObject.activeInHierarchy
                : player.KnownItems.ContainsKey(item) && player.AcknowledgedWorldItems.Contains(item.NetId);
            yield return new SimulatorCandidate(player.PlayerId, hasItem,
                (player.AbsoluteWorldPosition - absolute).sqrMagnitude,
                parentCarNetId != 0 && player.CarId == parentCarNetId, hostWorld: host);
        }
        if (HostItemActor.IsServer)
            yield return new SimulatorCandidate(HostItemActor.ServerSimulatorId,
                item.gameObject.activeInHierarchy && Coverage.ServerWorldCoverage.IsSolidAt(item.transform.position),
                0f, hostWorld: true, serverItself: true);
    }

    internal void SendActiveLeasesTo(ServerPlayer player, IEnumerable<ItemUpdate> snapshots)
    {
        if (player?.Peer == null || snapshots == null)
            return;
        foreach (ushort itemNetId in snapshots.Where(snapshot => snapshot != null &&
                     snapshot.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create))
                 .Select(snapshot => snapshot.ItemNetId).Distinct())
        {
            if (!leases.TryGetValue(itemNetId, out Lease lease) || lease.LastAccepted == null ||
                !NetworkedItem.TryGet(itemNetId, out NetworkedItem item) || item == null)
                continue;
            NetworkServer.Instance.SendItemSpatialLease(player,
                new ClientboundItemSpatialLeasePacket
                {
                    Active = true,
                    Reason = "interest-entry",
                    State = lease.LastAccepted.Clone()
                });
        }
    }

    private void UpdateHostLeases()
    {
        float now = Time.realtimeSinceStartup;
        byte hostId = HostItemActor.SimulatorId;
        foreach (Lease lease in leases.Values.ToArray())
        {
            if (!NetworkedItem.TryGet(lease.ItemNetId, out NetworkedItem item) || item?.Item == null)
            {
                RevokeHostLease(lease.ItemNetId, "representation-missing");
                continue;
            }
            // A change that skipped the explicit end still ends it, at once, not at every sample's
            // refusal (OI-PHY-03: a lease whose placement changed without a transition).
            if (!LiveItems.TryGet(lease.ItemNetId, out LiveItems.LiveItem record) ||
                !ItemLeaseRules.Holds((LivePlacement)(byte)record.Placement, lease.AuthorityRevision,
                    record.Revision, record.ObjectStateRunBase, record.ObjectStateRunTop))
            {
                EndLease(lease.ItemNetId, "placement-changed");
                continue;
            }
            if (lease.SimulatorPlayerId == hostId)
                SampleHostLease(item, lease);
            else
            {
                if (lease.Target != null)
                    ApplyObservedPose(item, lease, now);
                if (now - lease.LastAcceptedAt < SampleTimeoutSeconds)
                    continue;
                Publish("item.spatial-timeout", item.NetId, new()
                {
                    ["simulationEpoch"] = lease.Epoch,
                    ["simulatorPlayerId"] = lease.SimulatorPlayerId,
                    ["lastSampleAgeSeconds"] = now - lease.LastAcceptedAt
                });
                if (!TryTransferLease(item, lease, "sample-timeout"))
                    CommitHostSettlement(item, lease, lease.LastAccepted, "sample-timeout");
            }
        }
    }

    private bool TryTransferLease(NetworkedItem item, Lease previous, string reason)
    {
        if (item == null || previous?.LastAccepted == null ||
            !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) ||
            !LiveItems.PlacementUnchangedSince(record, previous.AuthorityRevision))
            return false;

        // A dedicated host takes it too, where its world is solid (OI-PHY-07): before, a thrown
        // item whose thrower left with nobody else near froze where its last sample had it.
        if (!ItemSimulatorChoice.ForTransfer(Candidates(item, previous.LastAccepted.AbsolutePosition,
                    previous.LastAccepted.WorldParentKind == ItemWorldParentKind.TrainInterior
                        ? previous.LastAccepted.WorldParentNetId : (ushort)0),
                previous.SimulatorPlayerId, MaximumSimulatorDistance, out byte candidateId))
            return false;

        leases.Remove(item.NetId);
        uint epoch = epochs.TryGetValue(item.NetId, out uint lastEpoch)
            ? lastEpoch + 1
            : previous.Epoch + 1;
        epochs[item.NetId] = epoch;
        ItemSpatialStateData baseline = previous.LastAccepted.Clone();
        baseline.AuthorityRevision = record.Revision;
        baseline.SimulationEpoch = epoch;
        baseline.SampleSequence = 0;
        baseline.SourceTick = NetworkLifecycle.Instance.Tick;
        baseline.SimulatorPlayerId = candidateId;
        float now = Time.realtimeSinceStartup;
        Lease transferred = new()
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            Epoch = epoch,
            SimulatorPlayerId = candidateId,
            StartedAt = now,
            LastAcceptedAt = now,
            LastAccepted = baseline.Clone(),
            Target = baseline.Clone()
        };
        leases[item.NetId] = transferred;
        bool localHost = candidateId == HostItemActor.SimulatorId;
        ConfigureLeasePresentation(item, localHost, baseline);
        if (localHost)
            ApplySimulationVelocity(item, baseline);
        NetworkServer.Instance.SendItemSpatialLease(item,
            new ClientboundItemSpatialLeasePacket
            {
                Active = true,
                Reason = $"lease-transfer:{reason}",
                State = baseline
            });
        Publish("item.spatial-lease-transferred", item.NetId, new()
        {
            ["previousSimulationEpoch"] = previous.Epoch,
            ["simulationEpoch"] = epoch,
            ["previousSimulatorPlayerId"] = previous.SimulatorPlayerId,
            ["simulatorPlayerId"] = candidateId,
            ["reason"] = reason ?? string.Empty
        });
        return true;
    }

    /// <param name="serverSample">
    /// The server's own sample with no sender: on a host with no player of its own, which simulates
    /// as itself (<see cref="HostItemActor"/>). Its own world needs no range check.
    /// </param>
    private bool TryAcceptHostSample(ItemSpatialStateData state, ServerPlayer sender, bool settlement,
        out Lease lease, out NetworkedItem item, out string rejection, bool serverSample = false)
    {
        lease = null;
        item = null;
        rejection = string.Empty;
        if (state == null || sender == null && !serverSample || !leases.TryGetValue(state?.ItemNetId ?? 0, out lease) ||
            !NetworkedItem.TryGet(state?.ItemNetId ?? 0, out item) || item == null)
        {
            rejection = "unknown-item-or-lease";
            return false;
        }
        // The lease stays good while only object state has changed since it was granted (the
        // server's copy of a thrown lantern flickering): placement is what it simulates.
        bool registryCurrent = LiveItems.TryGet(state.ItemNetId,
            out LiveItems.LiveItem record) &&
            LiveItems.PlacementUnchangedSince(record, lease.AuthorityRevision);
        bool simulatablePlacement = registryCurrent && record.Placement is ItemPlacementKind.World or
            ItemPlacementKind.TrainInterior or ItemPlacementKind.StaticParent;
        rejection = ItemSpatialStreamValidator.Validate(
            new ItemSpatialLeaseToken(lease.SimulatorPlayerId, lease.AuthorityRevision, lease.Epoch,
                lease.LastSequence, simulatablePlacement),
            new ItemSpatialSampleToken(sender?.PlayerId ?? HostItemActor.ServerSimulatorId,
                state.SimulatorPlayerId, state.AuthorityRevision,
                state.SimulationEpoch, state.SampleSequence));
        if (!string.IsNullOrEmpty(rejection))
        {
            return false;
        }
        if (!registryCurrent)
        {
            rejection = "authority-registry-mismatch";
            return false;
        }
        float rotationMagnitude = Mathf.Sqrt(state.Rotation.x * state.Rotation.x +
                                             state.Rotation.y * state.Rotation.y +
                                             state.Rotation.z * state.Rotation.z +
                                             state.Rotation.w * state.Rotation.w);
        if (!Finite(state) || rotationMagnitude < 0.5f || rotationMagnitude > 1.5f ||
            state.LinearVelocity.magnitude > MaximumLinearSpeed ||
            state.AngularVelocity.magnitude > MaximumAngularSpeed)
        {
            rejection = "invalid-or-excessive-motion";
            return false;
        }
        if (sender != null && (state.AbsolutePosition - sender.AbsoluteWorldPosition).sqrMagnitude >
            MaximumSimulatorDistance * MaximumSimulatorDistance)
        {
            rejection = "item-outside-simulator-range";
            return false;
        }
        float elapsed = Mathf.Max(0.016f, Time.realtimeSinceStartup - lease.LastAcceptedAt);
        float allowed = MaximumSampleDisplacementPerSecond * elapsed + 3f;
        bool sameParent = state.WorldParentKind == lease.LastAccepted.WorldParentKind &&
                          state.WorldParentNetId == lease.LastAccepted.WorldParentNetId &&
                          string.Equals(state.WorldParentKey ?? string.Empty,
                              lease.LastAccepted.WorldParentKey ?? string.Empty, StringComparison.Ordinal);
        Vector3 displacement = sameParent && state.WorldParentKind != ItemWorldParentKind.World
            ? state.ParentLocalPosition - lease.LastAccepted.ParentLocalPosition
            : state.AbsolutePosition - lease.LastAccepted.AbsolutePosition;
        if (displacement.sqrMagnitude > allowed * allowed)
        {
            rejection = "sample-displacement-envelope-exceeded";
            return false;
        }
        if (!ValidateParent(state, sender))
        {
            rejection = "invalid-spatial-parent";
            return false;
        }
        if (settlement && state.Phase != ItemSpatialPhase.Settled)
        {
            rejection = "settlement-phase-required";
            return false;
        }
        return true;
    }

    private void TerminateInvalidCurrentLease(ItemSpatialStateData state, ServerPlayer sender,
        string rejection)
    {
        if (state == null || sender == null ||
            rejection is "stale-sample-sequence" or "stale-simulation-epoch" or
                "stale-spatial-authority-revision" or "sender-not-simulator" ||
            !leases.TryGetValue(state.ItemNetId, out Lease lease) ||
            lease.SimulatorPlayerId != sender.PlayerId || lease.Epoch != state.SimulationEpoch ||
            lease.LastAccepted == null ||
            !NetworkedItem.TryGet(state.ItemNetId, out NetworkedItem item) || item == null)
            return;
        CommitHostSettlement(item, lease, lease.LastAccepted, $"sample-rejected:{rejection}");
        Publish("item.spatial-reconciled", item.NetId, new()
        {
            ["simulationEpoch"] = lease.Epoch,
            ["simulatorPlayerId"] = lease.SimulatorPlayerId,
            ["reason"] = rejection ?? string.Empty,
            ["reconciledTo"] = DebugValueSnapshotter.Snapshot(lease.LastAccepted.AbsolutePosition)
        });
    }

    // sender null: the server's own sample, whose parent only has to exist.
    private static bool ValidateParent(ItemSpatialStateData state, ServerPlayer sender)
    {
        if (state.WorldParentKind == ItemWorldParentKind.World)
            return true;
        if (state.WorldParentKind == ItemWorldParentKind.TrainInterior)
            return NetworkedTrainCar.TryGet(state.WorldParentNetId, out TrainCar car) && car != null &&
                   (sender == null ||
                    (car.transform.position - WorldMover.currentMove - sender.AbsoluteWorldPosition).sqrMagnitude <=
                    MaximumSimulatorDistance * MaximumSimulatorDistance);
        return state.WorldParentKind == ItemWorldParentKind.StaticParent &&
               WorldItemStaticParentRegistry.TryResolve(state.WorldParentKey, out Transform parent) && parent != null &&
               (sender == null ||
                (parent.position - WorldMover.currentMove - sender.AbsoluteWorldPosition).sqrMagnitude <=
                MaximumSimulatorDistance * MaximumSimulatorDistance);
    }

    private void ApplyAcceptedHostSample(NetworkedItem item, Lease lease, ItemSpatialStateData state)
    {
        lease.LastSequence = state.SampleSequence;
        lease.LastAcceptedAt = Time.realtimeSinceStartup;
        lease.LastAccepted = state.Clone();
        lease.Target = state.Clone();
        if (lease.SimulatorPlayerId == HostItemActor.SimulatorId)
            ApplyPose(item, state, immediate: true);
        else
            SetProxyKinematic(item, true);
        manager.RefreshHostSpatialPosition(item, state.AbsolutePosition, state.WorldParentKind);
        Publish("item.spatial-sample-accepted", item.NetId, SpatialDebug(state), highFrequency: true);
    }

    // Its parent, as one string: the world, a car by net ID, or a shelf by key.
    private static string ParentOf(ItemWorldParentKind kind, ushort netId, string key) => kind switch
    {
        ItemWorldParentKind.TrainInterior => "car:" + netId,
        ItemWorldParentKind.StaticParent => "shelf:" + key,
        _ => "world"
    };

    /// <summary>
    /// A flight whose samples show a new parent (it landed in a car, left one, came onto a shelf)
    /// for <see cref="ItemLeaseRules.ParentChangeDwell"/>: the change is committed as one, and the
    /// flight goes on under a new epoch at the new revision, with the same simulator
    /// (OI-PHY-05, F33). Before, only the settlement fixed the parent.
    /// </summary>
    private void CommitParentChangeIfDue(NetworkedItem item, Lease lease, ItemSpatialStateData state)
    {
        if (!LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
            return;
        float now = Time.realtimeSinceStartup;
        string sampled = ParentOf(state.WorldParentKind, state.WorldParentNetId, state.WorldParentKey);
        if (!string.Equals(sampled, lease.SampledParent, StringComparison.Ordinal))
        {
            lease.SampledParent = sampled;
            lease.SampledParentSince = now;
        }
        string committed = ParentOf(record.WorldParentKind, record.WorldParentNetId, record.WorldParentKey);
        if (!ItemLeaseRules.CommitsParentChange(committed, sampled, now - lease.SampledParentSince))
            return;
        if (!LiveItems.TryCommitSpatialPose(item, lease.AuthorityRevision, state, out record, out string rejection,
                PlayerNumbers.Identity(lease.SimulatorPlayerId)))
        {
            Reject(state, null, rejection);
            EndLease(item.NetId, $"parent-change-refused:{rejection}");
            return;
        }
        uint epoch = epochs.TryGetValue(item.NetId, out uint last) ? last + 1 : lease.Epoch + 1;
        epochs[item.NetId] = epoch;
        ItemSpatialStateData baseline = state.Clone();
        baseline.AuthorityRevision = record.Revision;
        baseline.SimulationEpoch = epoch;
        baseline.SampleSequence = 0;
        baseline.SourceTick = NetworkLifecycle.Instance.Tick;
        leases[item.NetId] = new Lease
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            Epoch = epoch,
            SimulatorPlayerId = lease.SimulatorPlayerId,
            StartedAt = lease.StartedAt,
            LastAcceptedAt = now,
            LastAccepted = baseline.Clone(),
            Target = baseline.Clone(),
            SampledParent = sampled,
            SampledParentSince = lease.SampledParentSince
        };
        ApplyCommitMetadata(item, baseline);
        manager.RefreshHostSpatialPosition(item, baseline.AbsolutePosition, baseline.WorldParentKind);
        NetworkServer.Instance.SendItemSpatialLease(item, new ClientboundItemSpatialLeasePacket
        {
            Active = true,
            Reason = ParentChangeLeaseReason,
            State = baseline.Clone()
        });
        Publish("item.spatial-parent-committed", item.NetId, new(SpatialDebug(baseline))
        {
            ["from"] = committed,
            ["to"] = sampled,
            ["previousSimulationEpoch"] = lease.Epoch
        });
    }

    private void CommitHostSettlement(NetworkedItem item, Lease lease, ItemSpatialStateData state, string reason)
    {
        if (item == null || state == null || !leases.TryGetValue(item.NetId, out Lease current) ||
            current.Epoch != lease.Epoch)
            return;
        if (!LiveItems.TryCommitSpatialPose(item, lease.AuthorityRevision, state,
                out LiveItems.LiveItem record, out string rejection, PlayerNumbers.Identity(lease.SimulatorPlayerId)))
        {
            // A refused settlement ends its lease (OI-PHY-03, -04): the change that made it stale
            // has its own update, and an open lease failed again every frame, never ending.
            Reject(state, null, rejection);
            EndLease(item.NetId, $"settlement-refused:{rejection}");
            return;
        }
        state = state.Clone();
        state.AuthorityRevision = record.Revision;
        state.Phase = ItemSpatialPhase.Settled;
        state.LinearVelocity = Vector3.zero;
        state.AngularVelocity = Vector3.zero;
        state.Sleeping = true;
        committedStates[item.NetId] = state.Clone();
        ApplyPose(item, state, immediate: true);
        ApplyCommitMetadata(item, state);
        manager.RefreshHostSpatialPosition(item, state.AbsolutePosition, state.WorldParentKind);
        leases.Remove(item.NetId);
        spatial.LeaseEnds[item.NetId] = "settled:" + (reason ?? string.Empty);
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.Sleep();
            if (state.WorldParentKind == ItemWorldParentKind.TrainInterior ||
                lease.SimulatorPlayerId != HostItemActor.SimulatorId)
                body.isKinematic = true;
        }
        NetworkServer.Instance.SendItemSpatialCommit(item,
            new ClientboundItemSpatialCommitPacket { State = state });
        manager.TrainItemWake.OnSpatialSettled(item, state, record);
        Publish("item.spatial-commit-accepted", item.NetId, new(SpatialDebug(state))
        {
            ["reason"] = reason ?? string.Empty
        });
    }

    private static void Reject(ItemSpatialStateData state, ServerPlayer sender, string reason)
    {
        DebugRuntime.Publish("item-spatial", "item.spatial-sample-rejected", DebugRuntimeSide.Server,
            DebugSeverity.Warning, "Item", (state?.ItemNetId ?? 0).ToString(), new()
            {
                ["reason"] = reason ?? string.Empty,
                ["senderPlayerId"] = sender?.PlayerId ?? 0,
                ["simulationEpoch"] = state?.SimulationEpoch ?? 0,
                ["sampleSequence"] = state?.SampleSequence ?? 0
            });
    }
}
