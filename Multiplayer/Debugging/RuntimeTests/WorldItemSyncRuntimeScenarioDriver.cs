#if DEBUG
using DV;
using DV.Interaction;
using DV.InventorySystem;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Components.Networking.World.WorldItems;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Integrations.Inventory;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests;

/// <summary>
/// End-to-end runtime coverage for the host-authoritative world-item stream.
/// Each mutation starts with a dashboard-owned fixture, invokes the real local DV
/// inventory/grabber path, and observes the canonical projection returned by the host.
/// The dashboard destroys the fixture after this driver has restored the player position.
/// </summary>
internal sealed class WorldItemSyncRuntimeScenarioDriver
{
    private const float RemoteCellOffset = WorldItemCellCoord.Size * 6f;

    private sealed class State
    {
        public RuntimeTestScenarioContext Context;
        public RuntimeTestRunDto Run;
        public ushort NetId;
        public NetworkedItem Item;
        public GameObject OriginalObject;
        public int OriginalSlot;
        public byte OwnerPlayerId;
        public Vector3 OriginalAbsolute;
        public Quaternion OriginalRotation;
        public Vector3 RemoteAbsolute;
        public Vector3 SettledAbsolute;
        public Quaternion SettledRotation;
        public uint SettledRevision;
    }

    public IEnumerator AuthoritativeDropRoundTrip(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => RunFixture(command, run, AuthoritativeDropRoundTripBody);

    public IEnumerator InventorySurvivesInterestTeleport(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => RunFixture(command, run, InventorySurvivesInterestTeleportBody);

    public IEnumerator ForeignHolderSurvivesInterestTeleport(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => RunFixture(command, run, ForeignHolderSurvivesInterestTeleportBody);

    public IEnumerator WorldProjectionRetiresAndReprojects(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => RunFixture(command, run, WorldProjectionRetiresAndReprojectsBody);

    public IEnumerator EssentialOwnerClaimRemainsRelevant(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => RunFixture(command, run, EssentialOwnerClaimRemainsRelevantBody);

    public IEnumerator CrossCellHeldDropReindexes(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => RunFixture(command, run, CrossCellHeldDropReindexesBody);

    public IEnumerator SpatialThrowSettlesCanonicalPose(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => RunFixture(command, run, SpatialThrowSettlesCanonicalPoseBody);

    public IEnumerator SpatialSettlementReprojectsExactPose(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => RunFixture(command, run, SpatialSettlementReprojectsExactPoseBody);

    public IEnumerator RepeatedSpatialThrowsRetireEveryLease(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => RunFixture(command, run, RepeatedSpatialThrowsRetireEveryLeaseBody);

    public IEnumerator AuthoredProjectionIntegrity(RuntimeTestCommandDto command,
        RuntimeTestRunDto run)
    {
        RuntimeTestScenarioContext context = new(command, run);
        return RuntimeTestScenarioRunner.Run(context, AuthoredProjectionIntegrityBody(context, run));
    }

    private static IEnumerator RunFixture(RuntimeTestCommandDto command, RuntimeTestRunDto run,
        Func<State, IEnumerator> body)
    {
        RuntimeTestScenarioContext context = new(command, run);
        State state = new() { Context = context, Run = run };
        return RuntimeTestScenarioRunner.Run(context, ExecuteFixture(command, state, body));
    }

    private static IEnumerator ExecuteFixture(RuntimeTestCommandDto command, State state,
        Func<State, IEnumerator> body)
    {
        state.Context.EnterPhase("prepare", "resolve-world-item-fixture");
        state.NetId = RequiredNetId(command);
        if (!NetworkedItem.TryGet(state.NetId, out state.Item) || state.Item == null)
            throw new InvalidOperationException("fixture-item-not-projected:" + state.NetId);
        if (command.Parameters.TryGetValue("itemFixtureToken", out string fixtureToken))
            InventoryRuntimeFixtureDriver.TrackLocalFixture(state.Item, fixtureToken);

        Inventory inventory = Inventory.Instance ??
            throw new RuntimeTestUnsupportedException("inventory-unavailable");
        state.OriginalObject = state.Item.gameObject;
        state.OriginalSlot = inventory.IndexOf(state.OriginalObject);
        state.OwnerPlayerId = state.Item.PersistentOwnerPlayerId;
        state.OriginalAbsolute = PlayerManager.PlayerTransform.GetWorldAbsolutePosition();
        state.OriginalRotation = PlayerManager.PlayerTransform.rotation;
        state.RemoteAbsolute = state.OriginalAbsolute + new Vector3(RemoteCellOffset, 20f,
            RemoteCellOffset);
        state.Context.RegisterResource("player-position", "local-player",
            "restored-to-pre-scenario-position", () => RestorePlayer(state));
        lock (state.Run)
        {
            state.Run.Result["itemNetId"] = state.NetId;
            state.Run.Result["originalInventorySlot"] = state.OriginalSlot;
            state.Run.Result["persistentOwnerPlayerId"] = state.OwnerPlayerId;
            state.Run.Result["originalAbsolute"] = DebugValueSnapshotter.Snapshot(
                state.OriginalAbsolute);
            state.Run.Result["remoteAbsolute"] = DebugValueSnapshotter.Snapshot(
                state.RemoteAbsolute);
        }
        IEnumerator operation = body(state);
        while (operation.MoveNext()) yield return operation.Current;
    }

    private static IEnumerator AuthoritativeDropRoundTripBody(State state)
    {
        AssertNormalOwnedInventoryFixture(state);
        uint beforeRevision = state.Item.AuthorityRevision;
        Vector3 target = NearbyDropTarget(state);
        state.Context.EnterPhase("act", "drop-through-native-inventory-path");
        yield return PlaceWorldThroughNativePath(state, target);
        yield return state.Context.Eventually("host-canonical-world-transition-returned",
            () => Resolve(state) is { } current && current.DebugCurrentState == ItemState.Dropped &&
                  current.AuthorityRevision > beforeRevision &&
                  current.PersistentOwnerPlayerId == state.OwnerPlayerId,
            8f, () => ProjectionSummary(state));
        NetworkedItem item = Resolve(state);
        state.Context.EnterPhase("assert", "authoritative-world-drop-invariants");
        state.Context.Assert("world-drop-removed-active-inventory-membership",
            Inventory.Instance.Contains(item.gameObject, false) == false,
            Inventory.Instance.IndexOf(item.gameObject));
        state.Context.Assert("world-drop-has-single-canonical-client-projection",
            CountNetworkBoundRepresentations(state.NetId) == 1,
            CountNetworkBoundRepresentations(state.NetId));
        lock (state.Run)
        {
            state.Run.Result["authorityRevisionBefore"] = beforeRevision;
            state.Run.Result["authorityRevisionAfter"] = item.AuthorityRevision;
            state.Run.Result["worldPositionAbsolute"] = DebugValueSnapshotter.Snapshot(
                item.transform.GetWorldAbsolutePosition());
        }
    }

    private static IEnumerator InventorySurvivesInterestTeleportBody(State state)
    {
        AssertNormalOwnedInventoryFixture(state);
        state.Context.EnterPhase("act", "teleport-owner-across-interest-cells");
        Teleport(state, state.RemoteAbsolute);
        yield return WaitSeconds(2f);
        yield return state.Context.Eventually("owned-inventory-item-remains-nonspatial",
            () => Resolve(state) is { } current && current.NetId == state.NetId &&
                  Inventory.Instance.IndexOf(current.gameObject) == state.OriginalSlot &&
                  Inventory.Instance.Contains(current.gameObject, false),
            8f, () => ProjectionSummary(state));
        state.Context.EnterPhase("assert", "owned-inventory-interest-invariants");
        state.Context.Assert("owned-inventory-netid-remains-stable", Resolve(state).NetId == state.NetId,
            Resolve(state).NetId);
        state.Context.Assert("owned-inventory-is-not-accidentally-world-projected",
            Resolve(state).DebugCurrentState == ItemState.InInventory,
            Resolve(state).DebugCurrentState.ToString());
    }

    private static IEnumerator ForeignHolderSurvivesInterestTeleportBody(State state)
    {
        state.Context.EnterPhase("prepare", "validate-foreign-owned-holder-fixture");
        state.Context.Assert("fixture-owner-is-not-local-holder",
            state.OwnerPlayerId != InventoryIntegration.LocalPlayerId && state.OwnerPlayerId != 0,
            state.OwnerPlayerId);
        state.Context.Assert("foreign-fixture-starts-in-local-inventory",
            state.OriginalSlot >= 0 && Inventory.Instance.Contains(state.OriginalObject, false),
            state.OriginalSlot);
        state.Context.EnterPhase("act", "teleport-foreign-item-holder-across-interest-cells");
        Teleport(state, state.RemoteAbsolute);
        yield return WaitSeconds(2f);
        yield return state.Context.Eventually("foreign-held-inventory-remains-relevant-to-holder",
            () => Resolve(state) is { } current && current.NetId == state.NetId &&
                  current.PersistentOwnerPlayerId == state.OwnerPlayerId &&
                  Inventory.Instance.IndexOf(current.gameObject) == state.OriginalSlot &&
                  Inventory.Instance.Contains(current.gameObject, false),
            8f, () => ProjectionSummary(state));
        state.Context.EnterPhase("assert", "foreign-holder-interest-invariants");
        state.Context.Assert("foreign-holder-keeps-single-canonical-projection",
            CountNetworkBoundRepresentations(state.NetId) == 1,
            CountNetworkBoundRepresentations(state.NetId));
    }

    private static IEnumerator WorldProjectionRetiresAndReprojectsBody(State state)
    {
        AssertNormalOwnedInventoryFixture(state);
        state.Context.Assert("fixture-is-not-essential-owner-claim",
            state.Item.Item?.InventorySpecs?.IsEssential != true,
            state.Item.Item?.InventorySpecs?.IsEssential);
        Vector3 target = NearbyDropTarget(state);
        state.Context.EnterPhase("act", "create-nearby-spatial-world-item");
        yield return PlaceWorldThroughNativePath(state, target);
        yield return state.Context.Eventually("nearby-world-item-projected",
            () => IsActiveWorldProjection(state), 8f, () => ProjectionSummary(state));

        state.Context.EnterPhase("act", "leave-world-item-interest-neighbourhood");
        Teleport(state, state.RemoteAbsolute);
        yield return state.Context.Eventually("world-projection-retired-after-interest-leave",
            () => !NetworkedItem.TryGet(state.NetId, out NetworkedItem current) || current == null ||
                  !current.gameObject.activeSelf || current.NetId != state.NetId,
            8f, () => ProjectionSummary(state));
        state.Context.Assert("retirement-cleared-local-active-inventory-membership",
            Inventory.Instance.Contains(state.OriginalObject, false) == false,
            Inventory.Instance.IndexOf(state.OriginalObject));

        state.Context.EnterPhase("act", "re-enter-world-item-interest-neighbourhood");
        Teleport(state, state.OriginalAbsolute);
        yield return state.Context.Eventually("world-projection-recreated-on-interest-reentry",
            () => IsActiveWorldProjection(state), 10f, () => ProjectionSummary(state));
        NetworkedItem reprojected = Resolve(state);
        state.Context.EnterPhase("assert", "world-projection-lifecycle-invariants");
        state.Context.Assert("reentry-preserved-world-item-owner",
            reprojected.PersistentOwnerPlayerId == state.OwnerPlayerId,
            reprojected.PersistentOwnerPlayerId);
        state.Context.Assert("reentry-did-not-create-duplicate-netid-projection",
            CountNetworkBoundRepresentations(state.NetId) == 1,
            CountNetworkBoundRepresentations(state.NetId));
        state.Context.Assert("reentry-kept-item-out-of-active-inventory",
            Inventory.Instance.Contains(reprojected.gameObject, false) == false,
            Inventory.Instance.IndexOf(reprojected.gameObject));
    }

    private static IEnumerator EssentialOwnerClaimRemainsRelevantBody(State state)
    {
        state.Context.EnterPhase("prepare", "validate-essential-owner-claim-fixture");
        state.Context.Assert("fixture-is-owned-by-local-player",
            state.OwnerPlayerId == InventoryIntegration.LocalPlayerId, state.OwnerPlayerId);
        state.Context.Assert("fixture-is-essential", state.Item.Item?.InventorySpecs?.IsEssential == true,
            state.Item.Item?.InventorySpecs?.IsEssential);
        state.Context.Assert("essential-fixture-has-inventory-slot", state.OriginalSlot >= 0,
            state.OriginalSlot);
        Vector3 target = NearbyDropTarget(state);
        state.Context.EnterPhase("act", "drop-essential-item-then-leave-spatial-interest");
        yield return PlaceWorldThroughNativePath(state, target);
        Teleport(state, state.RemoteAbsolute);
        yield return WaitSeconds(2f);
        yield return state.Context.Eventually("owner-essential-silhouette-remains-projected",
            () => Resolve(state) is { } current && current.NetId == state.NetId &&
                  Inventory.Instance.IndexOf(current.gameObject) == state.OriginalSlot &&
                  Inventory.Instance.GetSlotDroppedState(state.OriginalSlot) &&
                  Inventory.Instance.GetSlotReservedState(state.OriginalSlot),
            8f, () => ProjectionSummary(state));
        state.Context.EnterPhase("assert", "essential-owner-interest-invariants");
        state.Context.Assert("essential-owner-netid-remains-bound-for-return-action",
            Resolve(state).NetId == state.NetId, Resolve(state).NetId);
        state.Context.Assert("essential-owner-claim-keeps-single-projection",
            CountNetworkBoundRepresentations(state.NetId) == 1,
            CountNetworkBoundRepresentations(state.NetId));
    }

    private static IEnumerator CrossCellHeldDropReindexesBody(State state)
    {
        AssertNormalOwnedInventoryFixture(state);
        state.Context.EnterPhase("act", "hold-fixture-through-native-grabber-path");
        yield return ForceHoldThroughNativeGrabber(state);
        yield return state.Context.Eventually("held-item-transition-returned-by-host",
            () => Resolve(state) is { } current && current.DebugCurrentState == ItemState.InHand,
            8f, () => ProjectionSummary(state));

        state.Context.EnterPhase("act", "move-held-item-to-different-world-cell");
        Teleport(state, state.RemoteAbsolute);
        yield return WaitSeconds(1f);
        yield return DropThroughNativeGrabber(state);
        yield return state.Context.Eventually("cross-cell-world-drop-projected",
            () => IsActiveWorldProjection(state) &&
                  WorldItemCellCoord.FromAbsolute(Resolve(state).transform.GetWorldAbsolutePosition()).Equals(
                      WorldItemCellCoord.FromAbsolute(state.RemoteAbsolute)),
            8f, () => ProjectionSummary(state));

        state.Context.EnterPhase("act", "verify-old-cell-interest-no-longer-retains-item");
        Teleport(state, state.OriginalAbsolute);
        yield return state.Context.Eventually("cross-cell-drop-retired-from-old-cell",
            () => !NetworkedItem.TryGet(state.NetId, out NetworkedItem current) || current == null ||
                  !current.gameObject.activeSelf || current.NetId != state.NetId,
            8f, () => ProjectionSummary(state));

        state.Context.EnterPhase("act", "verify-new-cell-interest-reprojects-item");
        Teleport(state, state.RemoteAbsolute);
        yield return state.Context.Eventually("cross-cell-drop-reprojected-from-new-cell-index",
            () => IsActiveWorldProjection(state) &&
                  WorldItemCellCoord.FromAbsolute(Resolve(state).transform.GetWorldAbsolutePosition()).Equals(
                      WorldItemCellCoord.FromAbsolute(state.RemoteAbsolute)),
            10f, () => ProjectionSummary(state));
        state.Context.EnterPhase("assert", "cross-cell-index-invariants");
        state.Context.Assert("cross-cell-drop-kept-single-canonical-projection",
            CountNetworkBoundRepresentations(state.NetId) == 1,
            CountNetworkBoundRepresentations(state.NetId));
    }

    private static IEnumerator SpatialThrowSettlesCanonicalPoseBody(State state)
    {
        AssertNormalOwnedInventoryFixture(state);
        state.Context.Assert("fixture-has-rigidbody-for-spatial-simulation",
            state.Item.Item?.ItemRigidbody != null, state.Item.Item?.ItemRigidbody);
        uint initialRevision = state.Item.AuthorityRevision;

        yield return ThrowAndAwaitSpatialSettlement(state, 0);

        state.Context.EnterPhase("assert", "spatial-settlement-authority-invariants");
        AssertSettledCanonicalPose(state);
        state.Context.Assert("throw-advanced-logical-and-spatial-authority-revisions",
            state.SettledRevision >= initialRevision + 2,
            $"before={initialRevision},after={state.SettledRevision}");
        lock (state.Run)
        {
            state.Run.Result["authorityRevisionBefore"] = initialRevision;
            state.Run.Result["settledAuthorityRevision"] = state.SettledRevision;
            state.Run.Result["settledAbsolute"] = DebugValueSnapshotter.Snapshot(state.SettledAbsolute);
            state.Run.Result["settledRotation"] = DebugValueSnapshotter.Snapshot(state.SettledRotation);
        }
    }

    private static IEnumerator SpatialSettlementReprojectsExactPoseBody(State state)
    {
        AssertNormalOwnedInventoryFixture(state);
        state.Context.Assert("fixture-is-not-essential-owner-claim",
            state.Item.Item?.InventorySpecs?.IsEssential != true,
            state.Item.Item?.InventorySpecs?.IsEssential);
        yield return ThrowAndAwaitSpatialSettlement(state, 0);
        Vector3 committedPosition = state.SettledAbsolute;
        Quaternion committedRotation = state.SettledRotation;
        uint committedRevision = state.SettledRevision;

        state.Context.EnterPhase("act", "leave-settled-item-interest-neighbourhood");
        Teleport(state, state.RemoteAbsolute);
        yield return state.Context.Eventually("settled-world-projection-retires-outside-interest",
            () => !NetworkedItem.TryGet(state.NetId, out NetworkedItem current) || current == null ||
                  !current.gameObject.activeSelf || current.NetId != state.NetId,
            10f, () => ProjectionSummary(state));

        state.Context.EnterPhase("act", "reenter-settled-item-interest-neighbourhood");
        Teleport(state, state.OriginalAbsolute);
        yield return state.Context.Eventually("settled-world-projection-recreated",
            () => IsActiveWorldProjection(state), 12f, () => ProjectionSummary(state));

        NetworkedItem reprojected = Resolve(state);
        Vector3 reprojectedAbsolute = reprojected.transform.GetWorldAbsolutePosition();
        state.Context.EnterPhase("assert", "settled-reprojection-pose-invariants");
        state.Context.Assert("reprojection-uses-committed-spatial-position",
            Vector3.Distance(reprojectedAbsolute, committedPosition) <= 0.2f,
            $"expected={committedPosition},actual={reprojectedAbsolute}");
        state.Context.Assert("reprojection-uses-committed-spatial-rotation",
            Quaternion.Angle(reprojected.transform.rotation, committedRotation) <= 2f,
            Quaternion.Angle(reprojected.transform.rotation, committedRotation));
        state.Context.Assert("interest-reprojection-does-not-change-authority-revision",
            reprojected.AuthorityRevision == committedRevision,
            $"expected={committedRevision},actual={reprojected.AuthorityRevision}");
        state.Context.Assert("reprojection-does-not-reopen-spatial-lease",
            !NetworkedItemManager.Instance.HasActiveSpatialState(state.NetId),
            ProjectionSummary(state));
    }

    private static IEnumerator RepeatedSpatialThrowsRetireEveryLeaseBody(State state)
    {
        AssertNormalOwnedInventoryFixture(state);
        uint previousRevision = state.Item.AuthorityRevision;
        List<object> cycles = new();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            yield return ThrowAndAwaitSpatialSettlement(state, cycle);
            AssertSettledCanonicalPose(state);
            state.Context.Assert($"cycle-{cycle + 1}-revision-is-monotonic",
                state.SettledRevision > previousRevision,
                $"before={previousRevision},after={state.SettledRevision}");
            previousRevision = state.SettledRevision;
            cycles.Add(new Dictionary<string, object>
            {
                ["cycle"] = cycle + 1,
                ["revision"] = state.SettledRevision,
                ["positionAbsolute"] = DebugValueSnapshotter.Snapshot(state.SettledAbsolute)
            });
        }
        state.Context.EnterPhase("assert", "repeated-spatial-lease-invariants");
        state.Context.Assert("all-repeated-throws-end-without-active-lease",
            !NetworkedItemManager.Instance.HasActiveSpatialState(state.NetId),
            ProjectionSummary(state));
        state.Context.Assert("repeated-throws-keep-single-canonical-projection",
            CountNetworkBoundRepresentations(state.NetId) == 1,
            CountNetworkBoundRepresentations(state.NetId));
        lock (state.Run) state.Run.Result["cycles"] = cycles;
    }

    private static IEnumerator ThrowAndAwaitSpatialSettlement(State state, int cycle)
    {
        state.Context.EnterPhase("act", $"cycle-{cycle + 1}-hold-through-native-grabber");
        yield return ForceHoldThroughNativeGrabber(state);
        yield return state.Context.Eventually($"cycle-{cycle + 1}-held-transition-returned",
            () => Resolve(state) is { } current && current.DebugCurrentState == ItemState.InHand,
            8f, () => ProjectionSummary(state));
        uint releaseRevision = Resolve(state).AuthorityRevision;
        Vector3 releaseAbsolute = Resolve(state).transform.GetWorldAbsolutePosition();

        state.Context.EnterPhase("act", $"cycle-{cycle + 1}-throw-through-native-grabber");
        Vector3 direction = PlayerManager.PlayerTransform.forward +
                            Vector3.up * (0.08f + cycle * 0.04f);
        yield return ThrowThroughNativeGrabber(state, direction.normalized);
        yield return state.Context.Eventually($"cycle-{cycle + 1}-spatial-lease-observed",
            () => NetworkedItemManager.Instance.HasActiveSpatialState(state.NetId),
            3f, () => ProjectionSummary(state));
        yield return state.Context.Eventually($"cycle-{cycle + 1}-spatial-settlement-committed",
            () => TryGetSettledRecord(state, releaseRevision),
            15f, () => ProjectionSummary(state));

        NetworkedItem settled = Resolve(state);
        state.SettledAbsolute = settled.transform.GetWorldAbsolutePosition();
        state.SettledRotation = settled.transform.rotation;
        state.SettledRevision = settled.AuthorityRevision;
        state.Context.Assert($"cycle-{cycle + 1}-throw-moved-item",
            Vector3.Distance(releaseAbsolute, state.SettledAbsolute) >= 0.15f,
            $"release={releaseAbsolute},settled={state.SettledAbsolute}");
    }

    private static bool TryGetSettledRecord(State state, uint releaseRevision)
    {
        NetworkedItem current = Resolve(state);
        return current != null && current.DebugCurrentState == ItemState.Dropped &&
               current.AuthorityRevision > releaseRevision &&
               !NetworkedItemManager.Instance.HasActiveSpatialState(state.NetId) &&
               NetworkedItemManager.Instance.TryGetCommittedSpatialState(state.NetId,
                   out ItemSpatialStateData committed) &&
               committed.AuthorityRevision == current.AuthorityRevision &&
               committed.Phase == ItemSpatialPhase.Settled;
    }

    private static void AssertSettledCanonicalPose(State state)
    {
        NetworkedItem item = Resolve(state);
        state.Context.Assert("settlement-retired-transient-spatial-lease",
            !NetworkedItemManager.Instance.HasActiveSpatialState(state.NetId),
            ProjectionSummary(state));
        state.Context.Assert("settlement-kept-one-canonical-projection",
            CountNetworkBoundRepresentations(state.NetId) == 1,
            CountNetworkBoundRepresentations(state.NetId));
        state.Context.Assert("settlement-kept-item-in-world-placement",
            item != null && item.DebugCurrentState == ItemState.Dropped,
            item?.DebugCurrentState.ToString() ?? "missing");
        state.Context.Assert("settlement-registry-and-projection-positions-match",
            NetworkedItemManager.Instance.TryGetCommittedSpatialState(state.NetId,
                out ItemSpatialStateData committed) &&
            Vector3.Distance(committed.AbsolutePosition, item.transform.GetWorldAbsolutePosition()) <= 0.05f,
            item?.transform.GetWorldAbsolutePosition());
    }

    private static IEnumerator AuthoredProjectionIntegrityBody(RuntimeTestScenarioContext context,
        RuntimeTestRunDto run)
    {
        context.EnterPhase("prepare", "select-client-authored-scene-candidate");
        if (NetworkLifecycle.Instance?.IsHost() == true)
            throw new RuntimeTestUnsupportedException("authored-projection-integrity-requires-client-runtime");
        if (PlayerManager.PlayerTransform == null)
            throw new RuntimeTestUnsupportedException("local-player-transform-unavailable");

        Vector3 originalAbsolute = PlayerManager.PlayerTransform.GetWorldAbsolutePosition();
        Quaternion originalRotation = PlayerManager.PlayerTransform.rotation;
        context.RegisterResource("player-position", "authored-projection-client",
            "restored-to-pre-scenario-position", () => RestorePlayer(originalAbsolute, originalRotation));

        NetworkedItem item = NetworkedItem.GetAll().Where(candidate => candidate != null &&
                candidate.IsSceneAuthored && candidate.Item != null &&
                candidate.gameObject.scene.IsValid() && candidate.gameObject.scene.isLoaded &&
                (candidate.transform.GetWorldAbsolutePosition() - originalAbsolute).sqrMagnitude >=
                    RemoteCellOffset * RemoteCellOffset)
            .OrderBy(candidate => candidate.gameObject.activeInHierarchy)
            .ThenByDescending(candidate =>
                (candidate.transform.GetWorldAbsolutePosition() - originalAbsolute).sqrMagnitude)
            .FirstOrDefault();
        if (item == null)
            throw new RuntimeTestUnsupportedException(
                "no-distant-client-authored-scene-item-catalogue-entry");

        ushort beforeNetId = item.NetId;
        bool beforeActive = item.gameObject.activeInHierarchy;

        Vector3 targetAbsolute = item.transform.GetWorldAbsolutePosition() +
            new Vector3(1.25f, 1.5f, 1.25f);
        context.EnterPhase("act", "enter-authored-item-world-region-through-native-teleport");
        PlayerManager.TeleportPlayer(targetAbsolute + WorldMover.currentMove, originalRotation,
            null, true, false);
        yield return WaitSeconds(2f);
        yield return context.Eventually("authored-item-binds-when-native-world-region-is-active",
            () => item != null && item.NetId != 0 && item.gameObject.activeInHierarchy,
            12f, () => AuthoredProjectionSummary(item));

        context.Assert("authored-item-has-stable-key",
            WorldItemStableIdentity.IsValid(item.AuthoredItemKey), item.AuthoredItemKey);
        context.Assert("authored-item-netid-lookup-binds-same-unity-object",
            NetworkedItem.TryGet(item.NetId, out NetworkedItem canonical) && canonical == item,
            item.NetId);
        context.Assert("authored-item-has-single-active-netid-projection",
            CountNetworkBoundRepresentations(item.NetId) == 1,
            item.NetId);
        context.Assert("authored-item-is-not-gated-as-unbound-scene-object",
            item.UnboundState == ClientItemUnboundState.None, item.UnboundState.ToString());
        lock (run)
        {
            run.Result["itemNetId"] = item.NetId;
            run.Result["itemNetIdBeforeInterest"] = beforeNetId;
            run.Result["itemActiveBeforeInterest"] = beforeActive;
            run.Result["authoredItemKey"] = item.AuthoredItemKey;
            run.Result["prefabName"] = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name;
            run.Result["positionAbsolute"] = DebugValueSnapshotter.Snapshot(
                item.transform.GetWorldAbsolutePosition());
            run.Result["scenarioTeleportAbsolute"] = DebugValueSnapshotter.Snapshot(targetAbsolute);
        }
        yield return null;
    }

    private static void AssertNormalOwnedInventoryFixture(State state)
    {
        state.Context.EnterPhase("prepare", "validate-owned-inventory-fixture");
        state.Context.Assert("fixture-is-owned-by-local-player",
            state.OwnerPlayerId == InventoryIntegration.LocalPlayerId, state.OwnerPlayerId);
        state.Context.Assert("fixture-starts-in-active-inventory",
            state.OriginalSlot >= 0 && Inventory.Instance.Contains(state.OriginalObject, false),
            state.OriginalSlot);
    }

    private static IEnumerator PlaceWorldThroughNativePath(State state, Vector3 absolute)
    {
        NetworkedItem item = Resolve(state) ??
            throw new InvalidOperationException("fixture-canonical-projection-missing:" + state.NetId);
        item.Item?.ForceEndInteraction();
        Inventory.Instance.DropItemFromHandsOrInventory(item.gameObject);
        item.transform.position = absolute + WorldMover.currentMove;
        yield return null;
        yield return new WaitForEndOfFrame();
        item.ProcessLocalStateObservation("runtime-world-item-sync-place-world");
        yield return null;
    }

    private static IEnumerator ForceHoldThroughNativeGrabber(State state)
    {
        NetworkedItem item = Resolve(state) ??
            throw new InvalidOperationException("fixture-canonical-projection-missing:" + state.NetId);
        Grabber grabber = UnityEngine.Object.FindObjectsOfType<Grabber>()
            .FirstOrDefault(candidate => candidate != null &&
                candidate.GetComponent<GrabberInteractionHandlerDV>() != null);
        GrabberInteractionHandlerDV interaction = grabber?.GetComponent<GrabberInteractionHandlerDV>();
        GrabHandlerItem handler = item.GetComponent<GrabHandlerItem>();
        if (grabber == null || interaction == null || handler == null)
            throw new RuntimeTestUnsupportedException("local-grabber-or-item-handler-unavailable");
        if (grabber.CurrentItemHeld != null && grabber.CurrentItemHeld != handler)
            throw new InvalidOperationException("grabber-already-holding-item");
        if (grabber.CurrentItemHeld != handler)
        {
            Inventory.Instance.DropItemFromHandsOrInventory(item.gameObject);
            interaction.RequestForceHold(handler);
            yield return null;
        }
        if (grabber.CurrentItemHeld != handler || !handler.IsGrabbed())
            throw new InvalidOperationException("force-hold-did-not-enter-holding-state");
        item.ProcessLocalStateObservation("runtime-world-item-sync-force-hold");
        yield return null;
    }

    private static IEnumerator DropThroughNativeGrabber(State state)
    {
        NetworkedItem item = Resolve(state) ??
            throw new InvalidOperationException("held-fixture-canonical-projection-missing:" + state.NetId);
        Grabber grabber = UnityEngine.Object.FindObjectsOfType<Grabber>()
            .FirstOrDefault(candidate => candidate != null &&
                candidate.GetComponent<GrabberInteractionHandlerDV>() != null);
        GrabberInteractionHandlerDV interaction = grabber?.GetComponent<GrabberInteractionHandlerDV>();
        GrabHandlerItem handler = item.GetComponent<GrabHandlerItem>();
        if (grabber?.CurrentItemHeld != handler || interaction == null || handler == null)
            throw new InvalidOperationException("fixture-not-held-before-native-drop");
        interaction.RequestDrop();
        yield return null;
        yield return new WaitForEndOfFrame();
        if (grabber.CurrentItemHeld != null || handler.IsGrabbed())
            throw new InvalidOperationException("native-drop-did-not-release-held-item");
        item.ProcessLocalStateObservation("runtime-world-item-sync-cross-cell-drop");
        yield return null;
    }

    private static IEnumerator ThrowThroughNativeGrabber(State state, Vector3 direction)
    {
        NetworkedItem item = Resolve(state) ??
            throw new InvalidOperationException("held-fixture-canonical-projection-missing:" + state.NetId);
        Grabber grabber = UnityEngine.Object.FindObjectsOfType<Grabber>()
            .FirstOrDefault(candidate => candidate != null &&
                candidate.GetComponent<GrabberInteractionHandlerDV>() != null);
        GrabberInteractionHandlerDV interaction = grabber?.GetComponent<GrabberInteractionHandlerDV>();
        GrabHandlerItem handler = item.GetComponent<GrabHandlerItem>();
        if (grabber?.CurrentItemHeld != handler || interaction == null || handler == null)
            throw new InvalidOperationException("fixture-not-held-before-native-throw");
        interaction.RequestDrop();
        handler.Throw(direction);
        yield return null;
        yield return new WaitForEndOfFrame();
        if (grabber.CurrentItemHeld != null || handler.IsGrabbed())
            throw new InvalidOperationException("native-throw-did-not-release-held-item");
        item.ProcessLocalStateObservation("runtime-world-item-spatial-throw");
        yield return null;
    }

    private static bool IsActiveWorldProjection(State state)
    {
        NetworkedItem item = Resolve(state);
        return item != null && item.NetId == state.NetId && item.gameObject.activeSelf &&
               item.DebugCurrentState == ItemState.Dropped &&
               Inventory.Instance.Contains(item.gameObject, false) == false;
    }

    private static NetworkedItem Resolve(State state)
    {
        if (NetworkedItem.TryGet(state.NetId, out NetworkedItem current) && current != null)
            state.Item = current;
        return state.Item != null && state.Item.NetId == state.NetId ? state.Item : null;
    }

    private static int CountNetworkBoundRepresentations(ushort netId) => NetworkedItem.GetAll()
        .Count(item => item != null && item.NetId == netId);

    private static Vector3 NearbyDropTarget(State state)
    {
        Vector3 forward = PlayerManager.PlayerTransform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        return state.OriginalAbsolute + forward.normalized * 1.5f;
    }

    private static void Teleport(State state, Vector3 absolute) =>
        PlayerManager.TeleportPlayer(absolute + WorldMover.currentMove, state.OriginalRotation,
            null, true, false);

    private static IEnumerator RestorePlayer(State state)
    {
        if (PlayerManager.PlayerTransform != null)
            Teleport(state, state.OriginalAbsolute);
        yield return null;
    }

    private static IEnumerator RestorePlayer(Vector3 absolute, Quaternion rotation)
    {
        if (PlayerManager.PlayerTransform != null)
            PlayerManager.TeleportPlayer(absolute + WorldMover.currentMove, rotation, null, true, false);
        yield return null;
    }

    private static string AuthoredProjectionSummary(NetworkedItem item)
    {
        if (item == null) return "missing";
        return "netId=" + item.NetId + ",active=" + item.gameObject.activeSelf +
            ",activeInHierarchy=" + item.gameObject.activeInHierarchy +
            ",key=" + item.AuthoredItemKey;
    }

    private static string ProjectionSummary(State state)
    {
        NetworkedItem item = Resolve(state);
        if (item == null) return "missing";
        Inventory inventory = Inventory.Instance;
        int slot = inventory?.IndexOf(item.gameObject) ?? -1;
        return "netId=" + item.NetId + ",active=" + item.gameObject.activeSelf +
            ",state=" + item.DebugCurrentState + ",slot=" + slot +
            ",revision=" + item.AuthorityRevision + ",owner=" + item.PersistentOwnerPlayerId +
            ",cell=" + WorldItemCellCoord.FromAbsolute(item.transform.GetWorldAbsolutePosition());
    }

    private static ushort RequiredNetId(RuntimeTestCommandDto command) =>
        command.Parameters.TryGetValue("itemNetId", out string value) &&
        ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort netId) &&
        netId != 0 ? netId : throw new ArgumentException("missing-or-invalid-itemNetId");

    private static IEnumerator WaitSeconds(float seconds)
    {
        float started = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - started < seconds) yield return null;
    }
}
#endif
