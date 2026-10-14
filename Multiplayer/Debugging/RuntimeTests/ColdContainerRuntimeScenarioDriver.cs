#if DEBUG
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Components.Networking.World.Containers;
using Multiplayer.Core.Containers;
using Multiplayer.Integrations.Inventory;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Serverbound;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests;

internal sealed class ColdContainerRuntimeScenarioDriver
{
    private sealed class ScenarioState
    {
        public RuntimeTestScenarioContext Context;
        public RuntimeTestRunDto Run;
        public ItemContainer Container;
        public NetworkedItem Shell;
        public NetworkedItem OriginalItem;
        public GameObject OriginalObject;
        public ushort OriginalNetId;
        public ushort MaterializedNetId;
        public int OriginalInventorySlot;
        public int ContainerSlot;
        public int InitialRows;
        public uint InitialRevision;
        public uint RootContainerHandle;
        public string PrefabName;
        public byte OwnerPlayerId;
        public JObject DetachedState;
        public string ItemFixtureToken;
        public string InventoryResourceId;
        public Guid? ChildContainerId;
        public bool Deposited;
        public bool DepositRequested;
        public bool WithdrawalAccepted;
        public readonly List<string> OperationIds = new();
    }

    private static readonly MethodInfo quickMoveAction = typeof(InventoryViewNonVR)
        .GetMethod("QuickMoveAction", BindingFlags.Instance | BindingFlags.NonPublic);

    public IEnumerator RoundTrip(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RuntimeTestScenarioContext context = new(command, run);
        ScenarioState state = new() { Context = context, Run = run };
        return RuntimeTestScenarioRunner.Run(context, RoundTripBody(command, state));
    }

    public IEnumerator RejectForeignOwner(RuntimeTestCommandDto command,
        RuntimeTestRunDto run)
    {
        RuntimeTestScenarioContext context = new(command, run);
        ScenarioState state = new() { Context = context, Run = run };
        return RuntimeTestScenarioRunner.Run(context, RejectForeignOwnerBody(command, state));
    }

    public IEnumerator ReopenRoundTrip(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        Run(command, run, ReopenRoundTripBody);

    public IEnumerator MoveWithinRoundTrip(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, MoveWithinRoundTripBody);

    public IEnumerator RejectStaleRevision(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, RejectStaleRevisionBody);

    public IEnumerator RejectInvalidWithdrawalSlot(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, RejectInvalidWithdrawalSlotBody);

    public IEnumerator RejectIncompatibleItem(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, RejectIncompatibleItemBody);

    public IEnumerator RepeatedRoundTrip(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, RepeatedRoundTripBody);

    public IEnumerator RejectPrivateBrowse(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, RejectPrivateBrowseBody);

    public IEnumerator NestedContainerCycleGuard(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, NestedContainerCycleGuardBody);

    private static IEnumerator Run(RuntimeTestCommandDto command, RuntimeTestRunDto run,
        Func<RuntimeTestCommandDto, ScenarioState, IEnumerator> body)
    {
        RuntimeTestScenarioContext context = new(command, run);
        ScenarioState state = new() { Context = context, Run = run };
        return RuntimeTestScenarioRunner.Run(context, body(command, state));
    }

    private static IEnumerator RoundTripBody(RuntimeTestCommandDto command,
        ScenarioState state)
    {
        state.Context.EnterPhase("prepare", "resolve-owned-fixtures");
        ResolveFixtures(command, state, foreignOwner: false);
        RegisterCleanup(state);
        SnapshotFixtureResult(state);

        IEnumerator open = OpenContainer(state);
        while (open.MoveNext()) yield return open.Current;

        state.Context.EnterPhase("act", "quick-move-deposit");
        uint depositSequence = ColdContainerUiIntegration.MutationSequence;
        state.DepositRequested = true;
        InvokeQuickMove(state.OriginalInventorySlot, isContainerSlot: false);
        IEnumerator depositResult = state.Context.Eventually("deposit-result-received",
            () => ColdContainerUiIntegration.MutationSequence > depositSequence, 8f,
            () => ColdContainerUiIntegration.MutationSequence);
        while (depositResult.MoveNext()) yield return depositResult.Current;
        ClientboundContainerMutationResultPacket deposit =
            ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, deposit);
        state.Context.Assert("deposit-accepted", deposit?.Accepted == true,
            deposit?.RejectionReason ?? "missing-result");
        state.Deposited = true;

        IEnumerator depositView = state.Context.Eventually("deposit-view-refreshed",
            () => ColdContainerUiIntegration.ActiveRevision > state.InitialRevision &&
                ColdContainerUiIntegration.ActiveRowCount == state.InitialRows + 1 &&
                ColdContainerUiIntegration.ActiveSlots.Contains(state.ContainerSlot), 8f,
            ActiveViewSummary);
        while (depositView.MoveNext()) yield return depositView.Current;

        IEnumerator retired = state.Context.Eventually("physical-item-retired",
            () => !InventoryIntegration.ContainsActive(state.OriginalObject) &&
                !NetworkedItem.TryGet(state.OriginalNetId, out _), 8f,
            () => $"inventory={InventoryIntegration.ContainsActive(state.OriginalObject)},registry={NetworkedItem.TryGet(state.OriginalNetId, out _)}");
        while (retired.MoveNext()) yield return retired.Current;

        state.Context.EnterPhase("act", "quick-move-withdraw");
        uint depositedRevision = ColdContainerUiIntegration.ActiveRevision;
        uint withdrawalSequence = ColdContainerUiIntegration.MutationSequence;
        InvokeQuickMove(state.ContainerSlot, isContainerSlot: true);
        IEnumerator withdrawalResult = state.Context.Eventually("withdrawal-result-received",
            () => ColdContainerUiIntegration.MutationSequence > withdrawalSequence, 10f,
            () => ColdContainerUiIntegration.MutationSequence);
        while (withdrawalResult.MoveNext()) yield return withdrawalResult.Current;
        ClientboundContainerMutationResultPacket withdrawal =
            ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, withdrawal);
        state.Context.Assert("withdrawal-accepted", withdrawal?.Accepted == true,
            withdrawal?.RejectionReason ?? "missing-result");
        state.WithdrawalAccepted = true;
        state.Deposited = false;
        state.MaterializedNetId = withdrawal.MaterializedItemNetId;
        state.Context.Assert("materialized-netid-allocated",
            state.MaterializedNetId != 0 && state.MaterializedNetId != state.OriginalNetId,
            state.MaterializedNetId);

        IEnumerator withdrawalView = state.Context.Eventually("withdrawal-view-refreshed",
            () => ColdContainerUiIntegration.ActiveRevision > depositedRevision &&
                ColdContainerUiIntegration.ActiveRowCount == state.InitialRows, 10f,
            ActiveViewSummary);
        while (withdrawalView.MoveNext()) yield return withdrawalView.Current;

        NetworkedItem materialized = null;
        IEnumerator inventoryRestored = state.Context.Eventually("inventory-projection-restored",
            () => NetworkedItem.TryGet(state.MaterializedNetId, out materialized) &&
                materialized != null && InventoryIntegration.ContainsActive(materialized.gameObject) &&
                Inventory.Instance.IndexOf(materialized.gameObject) == state.OriginalInventorySlot,
            10f, () => materialized == null ? "missing" :
                $"slot={Inventory.Instance.IndexOf(materialized.gameObject)},owner={materialized.PersistentOwnerPlayerId}");
        while (inventoryRestored.MoveNext()) yield return inventoryRestored.Current;
        InventoryRuntimeFixtureDriver.TrackLocalFixture(materialized,
            OptionalString(command, "itemFixtureToken", string.Empty));

        state.Context.EnterPhase("assert", "round-trip-invariants");
        state.Context.Assert("persistent-owner-preserved",
            materialized.PersistentOwnerPlayerId == state.OwnerPlayerId,
            materialized.PersistentOwnerPlayerId);
        state.Context.Assert("prefab-preserved", string.Equals(
            materialized.Item?.InventorySpecs?.ItemPrefabName, state.PrefabName,
            StringComparison.Ordinal), materialized.Item?.InventorySpecs?.ItemPrefabName);
        JObject restoredState = materialized.GetComponent<ItemSaveData>()?.SaveItemData();
        bool detachedStateEqual = JToken.DeepEquals(state.DetachedState, restoredState);
        lock (state.Run)
        {
            state.Run.Result["detachedStateCompared"] =
                state.DetachedState != null || restoredState != null;
            state.Run.Result["detachedStateEqual"] = detachedStateEqual;
            state.Run.Result["materializedNetId"] = state.MaterializedNetId;
            state.Run.Result["operationIds"] = state.OperationIds.ToArray();
        }
        if (OptionalBool(command, "assertDetachedState", true))
            state.Context.Assert("detached-state-preserved", detachedStateEqual);

        state.Context.MarkRestored("inventory-item", state.OriginalNetId.ToString());
    }

    private static IEnumerator RejectForeignOwnerBody(RuntimeTestCommandDto command,
        ScenarioState state)
    {
        state.Context.EnterPhase("prepare", "resolve-foreign-owned-fixture");
        ResolveFixtures(command, state, foreignOwner: true);
        RegisterCleanup(state);
        SnapshotFixtureResult(state);

        IEnumerator open = OpenContainer(state);
        while (open.MoveNext()) yield return open.Current;

        state.Context.EnterPhase("act", "quick-move-foreign-deposit");
        uint sequence = ColdContainerUiIntegration.MutationSequence;
        state.DepositRequested = true;
        InvokeQuickMove(state.OriginalInventorySlot, isContainerSlot: false);
        IEnumerator resultWait = state.Context.Eventually("rejection-result-received",
            () => ColdContainerUiIntegration.MutationSequence > sequence, 8f,
            () => ColdContainerUiIntegration.MutationSequence);
        while (resultWait.MoveNext()) yield return resultWait.Current;
        ClientboundContainerMutationResultPacket result = ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, result);

        state.Context.EnterPhase("assert", "foreign-deposit-unchanged");
        state.Context.Assert("foreign-deposit-rejected", result?.Accepted == false,
            result?.RejectionReason ?? "missing-result");
        state.Context.Assert("foreign-deposit-reason",
            string.Equals(result.RejectionReason, "deposit-item-not-owned",
                StringComparison.Ordinal), result.RejectionReason);
        yield return null;
        yield return new WaitForEndOfFrame();
        state.Context.Assert("container-revision-unchanged",
            ColdContainerUiIntegration.ActiveRevision == state.InitialRevision,
            ActiveViewSummary());
        state.Context.Assert("container-row-count-unchanged",
            ColdContainerUiIntegration.ActiveRowCount == state.InitialRows,
            ActiveViewSummary());
        state.Context.Assert("foreign-item-slot-unchanged",
            Inventory.Instance.IndexOf(state.OriginalObject) ==
                state.OriginalInventorySlot,
            Inventory.Instance.IndexOf(state.OriginalObject));
        state.Context.Assert("foreign-item-representation-preserved",
            NetworkedItem.TryGet(state.OriginalNetId, out NetworkedItem current) &&
                current == state.OriginalItem &&
                InventoryIntegration.ContainsActive(state.OriginalObject));
        lock (state.Run) state.Run.Result["operationIds"] = state.OperationIds.ToArray();
        MarkInventoryRestored(state);
    }

    private static IEnumerator ReopenRoundTripBody(RuntimeTestCommandDto command,
        ScenarioState state)
    {
        IEnumerator prepare = PrepareOwnedAsync(command, state);
        while (prepare.MoveNext()) yield return prepare.Current;
        IEnumerator deposit = Deposit(state, "reopen");
        while (deposit.MoveNext()) yield return deposit.Current;
        int coldSlot = state.ContainerSlot;
        int expectedRows = ColdContainerUiIntegration.ActiveRowCount;

        state.Context.EnterPhase("act", "close-container-ui");
        if (Inventory.Instance.ItemContainerRegistry.ActiveContainer == state.Container)
            state.Container.ToggleContainerAccess();
        ColdContainerUiIntegration.Reset();
        yield return null;
        state.Context.Assert("container-ui-closed",
            Inventory.Instance.ItemContainerRegistry.ActiveContainer != state.Container &&
            !ColdContainerUiIntegration.Active, ActiveViewSummary());

        IEnumerator reopen = OpenContainer(state);
        while (reopen.MoveNext()) yield return reopen.Current;
        state.Context.Assert("cold-row-survived-reopen",
            ColdContainerUiIntegration.ActiveRowCount == expectedRows &&
            ColdContainerUiIntegration.ActiveSlots.Contains(coldSlot),
            ActiveViewSummary());
        state.ContainerSlot = coldSlot;
        IEnumerator withdraw = Withdraw(state, "reopen");
        while (withdraw.MoveNext()) yield return withdraw.Current;
        MarkInventoryRestored(state);
    }

    private static IEnumerator MoveWithinRoundTripBody(RuntimeTestCommandDto command,
        ScenarioState state)
    {
        IEnumerator prepare = PrepareOwnedAsync(command, state);
        while (prepare.MoveNext()) yield return prepare.Current;
        IEnumerator deposit = Deposit(state, "move-within");
        while (deposit.MoveNext()) yield return deposit.Current;
        int targetSlot = FirstFreeSlot(ColdContainerUiIntegration.ActiveCapacity,
            ColdContainerUiIntegration.ActiveSlots);
        state.Context.Assert("move-target-slot-available", targetSlot >= 0 &&
            targetSlot != state.ContainerSlot, targetSlot);

        state.Context.EnterPhase("act", "move-cold-row-within-container");
        uint beforeRevision = ColdContainerUiIntegration.ActiveRevision;
        uint sequence = ColdContainerUiIntegration.MutationSequence;
        state.Context.Assert("move-request-intercepted",
            ColdContainerUiIntegration.TryInterceptMove(state.Container,
                state.ContainerSlot, targetSlot));
        IEnumerator result = WaitForMutation(state, "move-result-received", sequence, 8f);
        while (result.MoveNext()) yield return result.Current;
        ClientboundContainerMutationResultPacket mutation =
            ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, mutation);
        state.Context.Assert("move-accepted", mutation?.Accepted == true,
            mutation?.RejectionReason ?? "missing-result");
        IEnumerator refreshed = state.Context.Eventually("move-view-refreshed",
            () => ColdContainerUiIntegration.ActiveRevision == beforeRevision + 1 &&
                ColdContainerUiIntegration.ActiveSlots.Contains(targetSlot) &&
                !ColdContainerUiIntegration.ActiveSlots.Contains(state.ContainerSlot), 8f,
            ActiveViewSummary);
        while (refreshed.MoveNext()) yield return refreshed.Current;
        state.ContainerSlot = targetSlot;
        IEnumerator withdraw = Withdraw(state, "move-within");
        while (withdraw.MoveNext()) yield return withdraw.Current;
        MarkInventoryRestored(state);
    }

    private static IEnumerator RejectStaleRevisionBody(RuntimeTestCommandDto command,
        ScenarioState state)
    {
        IEnumerator prepare = PrepareOwnedAsync(command, state);
        while (prepare.MoveNext()) yield return prepare.Current;
        IEnumerator deposit = Deposit(state, "stale");
        while (deposit.MoveNext()) yield return deposit.Current;
        uint currentRevision = ColdContainerUiIntegration.ActiveRevision;
        state.Context.Assert("revision-can-be-made-stale", currentRevision > 0,
            currentRevision);

        state.Context.EnterPhase("act", "withdraw-with-stale-revision");
        uint sequence = ColdContainerUiIntegration.MutationSequence;
        SendWithdrawal(state, currentRevision - 1, state.OriginalInventorySlot);
        IEnumerator result = WaitForMutation(state, "stale-result-received", sequence, 8f);
        while (result.MoveNext()) yield return result.Current;
        ClientboundContainerMutationResultPacket mutation =
            ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, mutation);
        state.Context.Assert("stale-withdrawal-rejected", mutation?.Accepted == false,
            mutation?.RejectionReason ?? "missing-result");
        state.Context.Assert("stale-withdrawal-reason",
            string.Equals(mutation?.RejectionReason, "source-revision-stale",
                StringComparison.Ordinal), mutation?.RejectionReason);
        yield return null;
        state.Context.Assert("stale-rejection-left-row-unchanged",
            ColdContainerUiIntegration.ActiveRevision == currentRevision &&
            ColdContainerUiIntegration.ActiveSlots.Contains(state.ContainerSlot),
            ActiveViewSummary());
        IEnumerator withdraw = Withdraw(state, "stale-recovery");
        while (withdraw.MoveNext()) yield return withdraw.Current;
        MarkInventoryRestored(state);
    }

    private static IEnumerator RejectInvalidWithdrawalSlotBody(
        RuntimeTestCommandDto command, ScenarioState state)
    {
        IEnumerator prepare = PrepareOwnedAsync(command, state);
        while (prepare.MoveNext()) yield return prepare.Current;
        IEnumerator deposit = Deposit(state, "invalid-slot");
        while (deposit.MoveNext()) yield return deposit.Current;
        uint currentRevision = ColdContainerUiIntegration.ActiveRevision;

        state.Context.EnterPhase("act", "withdraw-with-missing-destination-slot");
        uint sequence = ColdContainerUiIntegration.MutationSequence;
        SendWithdrawal(state, currentRevision, -1);
        IEnumerator result = WaitForMutation(state, "invalid-slot-result-received",
            sequence, 8f);
        while (result.MoveNext()) yield return result.Current;
        ClientboundContainerMutationResultPacket mutation =
            ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, mutation);
        state.Context.Assert("invalid-slot-withdrawal-rejected",
            mutation?.Accepted == false, mutation?.RejectionReason ?? "missing-result");
        state.Context.Assert("invalid-slot-withdrawal-reason",
            string.Equals(mutation?.RejectionReason,
                "withdraw-destination-slot-missing", StringComparison.Ordinal),
            mutation?.RejectionReason);
        yield return null;
        state.Context.Assert("invalid-slot-rejection-left-row-unchanged",
            ColdContainerUiIntegration.ActiveRevision == currentRevision &&
            ColdContainerUiIntegration.ActiveSlots.Contains(state.ContainerSlot),
            ActiveViewSummary());
        IEnumerator withdraw = Withdraw(state, "invalid-slot-recovery");
        while (withdraw.MoveNext()) yield return withdraw.Current;
        MarkInventoryRestored(state);
    }

    private static IEnumerator RejectIncompatibleItemBody(RuntimeTestCommandDto command,
        ScenarioState state)
    {
        state.Context.EnterPhase("prepare", "resolve-incompatible-fixtures");
        ResolveFixtures(command, state, foreignOwner: false, requireCompatible: false);
        RegisterCleanup(state);
        SnapshotFixtureResult(state);
        state.Context.Assert("runtime-container-rejects-fixture",
            !IsCompatible(state.Container, state.OriginalObject),
            $"container={state.Shell.name},item={state.OriginalItem.name}");
        IEnumerator open = OpenContainer(state);
        while (open.MoveNext()) yield return open.Current;

        state.Context.EnterPhase("act", "quick-move-incompatible-deposit");
        uint sequence = ColdContainerUiIntegration.MutationSequence;
        state.DepositRequested = true;
        InvokeQuickMove(state.OriginalInventorySlot, isContainerSlot: false);
        IEnumerator result = WaitForMutation(state, "incompatible-result-received",
            sequence, 8f);
        while (result.MoveNext()) yield return result.Current;
        ClientboundContainerMutationResultPacket mutation =
            ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, mutation);
        state.Context.Assert("incompatible-deposit-rejected",
            mutation?.Accepted == false, mutation?.RejectionReason ?? "missing-result");
        state.Context.Assert("incompatible-deposit-reason",
            string.Equals(mutation?.RejectionReason, "game-container-rejected-item",
                StringComparison.Ordinal), mutation?.RejectionReason);
        yield return null;
        state.Context.Assert("incompatible-item-remained-in-inventory",
            Inventory.Instance.IndexOf(state.OriginalObject) == state.OriginalInventorySlot &&
            NetworkedItem.TryGet(state.OriginalNetId, out NetworkedItem current) &&
            current == state.OriginalItem, Inventory.Instance.IndexOf(state.OriginalObject));
        state.Context.Assert("incompatible-container-unchanged",
            ColdContainerUiIntegration.ActiveRevision == state.InitialRevision &&
            ColdContainerUiIntegration.ActiveRowCount == state.InitialRows,
            ActiveViewSummary());
        MarkInventoryRestored(state);
    }

    private static IEnumerator RepeatedRoundTripBody(RuntimeTestCommandDto command,
        ScenarioState state)
    {
        IEnumerator prepare = PrepareOwnedAsync(command, state);
        while (prepare.MoveNext()) yield return prepare.Current;
        int cycles = Math.Max(2, Math.Min(5, OptionalInt(command, "cycles", 3)));
        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            IEnumerator deposit = Deposit(state, $"cycle-{cycle}");
            while (deposit.MoveNext()) yield return deposit.Current;
            IEnumerator withdraw = Withdraw(state, $"cycle-{cycle}");
            while (withdraw.MoveNext()) yield return withdraw.Current;
        }
        lock (state.Run) state.Run.Result["completedCycles"] = cycles;
        MarkInventoryRestored(state);
    }

    private static IEnumerator RejectPrivateBrowseBody(RuntimeTestCommandDto command,
        ScenarioState state)
    {
        state.Context.EnterPhase("prepare", "resolve-foreign-container-fixture");
        ResolveFixtures(command, state, foreignOwner: false, shellOwnedByLocal: false,
            requireCompatible: false);
        RegisterCleanup(state);
        SnapshotFixtureResult(state);
        state.Context.Assert("container-is-foreign-owned",
            state.Shell.PersistentOwnerPlayerId != InventoryIntegration.LocalPlayerId,
            state.Shell.PersistentOwnerPlayerId);

        state.Context.EnterPhase("act", "browse-foreign-container");
        uint viewSequence = ColdContainerUiIntegration.ViewSequence;
        if (Inventory.Instance.ItemContainerRegistry.ActiveContainer != state.Container)
            state.Container.ToggleContainerAccess();
        ColdContainerUiIntegration.Open(state.Container);
        IEnumerator response = state.Context.Eventually("private-browse-result-received",
            () => ColdContainerUiIntegration.ViewSequence > viewSequence, 8f,
            () => ColdContainerUiIntegration.ViewSequence);
        while (response.MoveNext()) yield return response.Current;
        ClientboundContainerViewPacket view = ColdContainerUiIntegration.LastView;
        state.Context.EnterPhase("assert", "private-container-undisclosed");
        state.Context.Assert("private-browse-rejected", view?.Accepted == false,
            view?.RejectionReason ?? "missing-result");
        state.Context.Assert("private-browse-denied-before-disclosure",
            string.Equals(view?.RejectionReason, "container-private",
                StringComparison.Ordinal) ||
            string.Equals(view?.RejectionReason, "container-shell-not-bound",
                StringComparison.Ordinal), view?.RejectionReason);
        state.Context.Assert("private-browse-disclosed-no-rows",
            (view?.Slots?.Length ?? 0) == 0 &&
            (view?.PrefabNames?.Length ?? 0) == 0, view?.Slots?.Length ?? -1);
        state.Context.Assert("private-container-never-became-active",
            !ColdContainerUiIntegration.Active, ActiveViewSummary());
        MarkInventoryRestored(state);
    }

    private static IEnumerator NestedContainerCycleGuardBody(RuntimeTestCommandDto command,
        ScenarioState state)
    {
        state.Context.EnterPhase("prepare", "resolve-nested-container-fixtures");
        ResolveFixtures(command, state, foreignOwner: false, allowItemContainers: true);
        RegisterCleanup(state);
        SnapshotFixtureResult(state);
        state.ChildContainerId = state.OriginalItem
            .GetComponent<ColdContainerIdentity>()?.PersistentId;
        state.Context.Assert("child-container-has-persistent-id",
            state.ChildContainerId.HasValue && state.ChildContainerId != Guid.Empty,
            state.ChildContainerId?.ToString("D") ?? "missing");
        IEnumerator open = OpenContainer(state);
        while (open.MoveNext()) yield return open.Current;
        IEnumerator deposit = Deposit(state, "nested");
        while (deposit.MoveNext()) yield return deposit.Current;
        int nestedSlot = state.ContainerSlot;

        int row = Array.IndexOf(ColdContainerUiIntegration.ActiveSlots,
            state.ContainerSlot);
        uint childHandle = row >= 0 &&
            row < ColdContainerUiIntegration.ActiveChildContainerHandles.Length
                ? ColdContainerUiIntegration.ActiveChildContainerHandles[row] : 0;
        state.Context.Assert("nested-row-has-child-handle", childHandle != 0,
            childHandle);

        state.Context.EnterPhase("act", "open-cold-child-container");
        uint viewSequence = ColdContainerUiIntegration.ViewSequence;
        ColdContainerUiIntegration.OpenChild(childHandle);
        IEnumerator childView = state.Context.Eventually("child-container-view-active",
            () => ColdContainerUiIntegration.ViewSequence > viewSequence &&
                ColdContainerUiIntegration.ActiveHandle == childHandle, 8f,
            ActiveViewSummary);
        while (childView.MoveNext()) yield return childView.Current;
        state.Context.Assert("child-container-starts-empty",
            ColdContainerUiIntegration.ActiveRowCount == 0, ActiveViewSummary());

        state.Context.EnterPhase("act", "reject-parent-into-descendant");
        uint sequence = ColdContainerUiIntegration.MutationSequence;
        state.Context.Assert("cycle-deposit-request-intercepted",
            ColdContainerUiIntegration.TryInterceptAdd(state.Container,
                state.Shell.gameObject, 0));
        IEnumerator cycle = WaitForMutation(state, "cycle-result-received", sequence, 8f);
        while (cycle.MoveNext()) yield return cycle.Current;
        ClientboundContainerMutationResultPacket mutation =
            ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, mutation);
        state.Context.Assert("recursive-nesting-rejected", mutation?.Accepted == false,
            mutation?.RejectionReason ?? "missing-result");
        state.Context.Assert("recursive-nesting-guard-status",
            mutation != null && ((ContainerOperationStatus)mutation.Status ==
                ContainerOperationStatus.CycleDetected ||
                (ContainerOperationStatus)mutation.Status ==
                ContainerOperationStatus.Incompatible),
            mutation == null ? "missing-result" :
                ((ContainerOperationStatus)mutation.Status).ToString());

        IEnumerator reopenRoot = OpenContainer(state);
        while (reopenRoot.MoveNext()) yield return reopenRoot.Current;
        state.Context.Assert("nested-row-survived-cycle-rejection",
            ColdContainerUiIntegration.ActiveSlots.Contains(nestedSlot),
            ActiveViewSummary());
        state.ContainerSlot = nestedSlot;
        IEnumerator withdraw = Withdraw(state, "nested");
        while (withdraw.MoveNext()) yield return withdraw.Current;
        Guid? restoredId = state.OriginalItem
            .GetComponent<ColdContainerIdentity>()?.PersistentId;
        state.Context.Assert("child-container-id-preserved",
            restoredId == state.ChildContainerId,
            restoredId?.ToString("D") ?? "missing");
        MarkInventoryRestored(state);
    }

    private static IEnumerator PrepareOwnedAsync(RuntimeTestCommandDto command,
        ScenarioState state)
    {
        state.Context.EnterPhase("prepare", "resolve-owned-fixtures");
        ResolveFixtures(command, state, foreignOwner: false);
        RegisterCleanup(state);
        SnapshotFixtureResult(state);
        IEnumerator open = OpenContainer(state);
        while (open.MoveNext()) yield return open.Current;
    }

    private static IEnumerator Deposit(ScenarioState state, string prefix)
    {
        uint beforeRevision = ColdContainerUiIntegration.ActiveRevision;
        int beforeRows = ColdContainerUiIntegration.ActiveRowCount;
        state.ContainerSlot = FirstFreeSlot(ColdContainerUiIntegration.ActiveCapacity,
            ColdContainerUiIntegration.ActiveSlots);
        state.Context.Assert(prefix + "-deposit-slot-available",
            state.ContainerSlot >= 0, ActiveViewSummary());
        state.Context.EnterPhase("act", prefix + "-quick-move-deposit");
        uint sequence = ColdContainerUiIntegration.MutationSequence;
        state.DepositRequested = true;
        state.WithdrawalAccepted = false;
        InvokeQuickMove(state.OriginalInventorySlot, isContainerSlot: false);
        IEnumerator result = WaitForMutation(state, prefix + "-deposit-result-received",
            sequence, 8f);
        while (result.MoveNext()) yield return result.Current;
        ClientboundContainerMutationResultPacket mutation =
            ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, mutation);
        state.Context.Assert(prefix + "-deposit-accepted", mutation?.Accepted == true,
            mutation?.RejectionReason ?? "missing-result");
        state.Deposited = true;

        IEnumerator refreshed = state.Context.Eventually(prefix + "-deposit-view-refreshed",
            () => ColdContainerUiIntegration.ActiveRevision > beforeRevision &&
                ColdContainerUiIntegration.ActiveRowCount == beforeRows + 1 &&
                ColdContainerUiIntegration.ActiveSlots.Contains(state.ContainerSlot), 8f,
            ActiveViewSummary);
        while (refreshed.MoveNext()) yield return refreshed.Current;
        IEnumerator retired = state.Context.Eventually(prefix + "-physical-item-retired",
            () => !InventoryIntegration.ContainsActive(state.OriginalObject) &&
                !NetworkedItem.TryGet(state.OriginalNetId, out _), 8f,
            () => $"inventory={InventoryIntegration.ContainsActive(state.OriginalObject)}," +
                $"registry={NetworkedItem.TryGet(state.OriginalNetId, out _)}");
        while (retired.MoveNext()) yield return retired.Current;
    }

    private static IEnumerator Withdraw(ScenarioState state, string prefix)
    {
        uint beforeRevision = ColdContainerUiIntegration.ActiveRevision;
        int beforeRows = ColdContainerUiIntegration.ActiveRowCount;
        ushort retiredNetId = state.OriginalNetId;
        state.Context.EnterPhase("act", prefix + "-quick-move-withdraw");
        uint sequence = ColdContainerUiIntegration.MutationSequence;
        InvokeQuickMove(state.ContainerSlot, isContainerSlot: true);
        IEnumerator result = WaitForMutation(state,
            prefix + "-withdrawal-result-received", sequence, 10f);
        while (result.MoveNext()) yield return result.Current;
        ClientboundContainerMutationResultPacket mutation =
            ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, mutation);
        state.Context.Assert(prefix + "-withdrawal-accepted", mutation?.Accepted == true,
            mutation?.RejectionReason ?? "missing-result");
        state.WithdrawalAccepted = true;
        state.Deposited = false;
        state.MaterializedNetId = mutation.MaterializedItemNetId;
        state.Context.Assert(prefix + "-materialized-netid-allocated",
            state.MaterializedNetId != 0 && state.MaterializedNetId != retiredNetId,
            state.MaterializedNetId);

        IEnumerator refreshed = state.Context.Eventually(
            prefix + "-withdrawal-view-refreshed",
            () => ColdContainerUiIntegration.ActiveRevision > beforeRevision &&
                ColdContainerUiIntegration.ActiveRowCount == beforeRows - 1, 10f,
            ActiveViewSummary);
        while (refreshed.MoveNext()) yield return refreshed.Current;
        NetworkedItem materialized = null;
        IEnumerator restored = state.Context.Eventually(
            prefix + "-inventory-projection-restored",
            () => NetworkedItem.TryGet(state.MaterializedNetId, out materialized) &&
                materialized != null &&
                InventoryIntegration.ContainsActive(materialized.gameObject) &&
                Inventory.Instance.IndexOf(materialized.gameObject) ==
                    state.OriginalInventorySlot,
            10f, () => materialized == null ? "missing" :
                $"slot={Inventory.Instance.IndexOf(materialized.gameObject)}," +
                $"owner={materialized.PersistentOwnerPlayerId}");
        while (restored.MoveNext()) yield return restored.Current;
        InventoryRuntimeFixtureDriver.TrackLocalFixture(materialized,
            state.ItemFixtureToken);
        state.Context.Assert(prefix + "-persistent-owner-preserved",
            materialized.PersistentOwnerPlayerId == state.OwnerPlayerId,
            materialized.PersistentOwnerPlayerId);
        state.Context.Assert(prefix + "-prefab-preserved", string.Equals(
            materialized.Item?.InventorySpecs?.ItemPrefabName, state.PrefabName,
            StringComparison.Ordinal), materialized.Item?.InventorySpecs?.ItemPrefabName);
        JObject restoredState = materialized.GetComponent<ItemSaveData>()?.SaveItemData();
        state.Context.Assert(prefix + "-detached-state-preserved",
            JToken.DeepEquals(state.DetachedState, restoredState));
        state.OriginalItem = materialized;
        state.OriginalObject = materialized.gameObject;
        state.OriginalNetId = materialized.NetId;
        state.DetachedState = restoredState;
        state.InitialRevision = ColdContainerUiIntegration.ActiveRevision;
        state.InitialRows = ColdContainerUiIntegration.ActiveRowCount;
        lock (state.Run)
        {
            state.Run.Result["materializedNetId"] = state.MaterializedNetId;
            state.Run.Result["operationIds"] = state.OperationIds.ToArray();
        }
    }

    private static IEnumerator WaitForMutation(ScenarioState state, string assertionId,
        uint previousSequence, float timeout)
    {
        IEnumerator wait = state.Context.Eventually(assertionId,
            () => ColdContainerUiIntegration.MutationSequence > previousSequence,
            timeout, () => ColdContainerUiIntegration.MutationSequence);
        while (wait.MoveNext()) yield return wait.Current;
    }

    private static void SendWithdrawal(ScenarioState state, uint expectedRevision,
        int destinationInventorySlot)
    {
        ServerboundContainerMutationPacket packet = new()
        {
            RequestId = (uint)Environment.TickCount,
            OperationId = Guid.NewGuid().ToByteArray(),
            Kind = (byte)ContainerOperationKind.Withdraw,
            SourceContainerHandle = ColdContainerUiIntegration.ActiveHandle,
            ExpectedSourceRevision = expectedRevision,
            SourceSlot = state.ContainerSlot,
            DestinationSlot = destinationInventorySlot
        };
        NetworkLifecycle.Instance.Client.RequestContainerMutation(packet);
    }

    private static IEnumerator OpenContainer(ScenarioState state)
    {
        state.Context.EnterPhase("prepare", "open-container");
        if (Inventory.Instance.ItemContainerRegistry.ActiveContainer != state.Container)
            state.Container.ToggleContainerAccess();
        yield return null;
        float nextBrowseRetry = 0f;
        IEnumerator active = state.Context.Eventually("cold-container-view-active",
            () =>
            {
                bool ready = ColdContainerUiIntegration.Active &&
                    ColdContainerUiIntegration.ActiveHandle != 0 &&
                    (state.RootContainerHandle == 0 ||
                     ColdContainerUiIntegration.ActiveHandle == state.RootContainerHandle) &&
                    Inventory.Instance.ItemContainerRegistry.ActiveContainer == state.Container;
                if (!ready && Time.realtimeSinceStartup >= nextBrowseRetry)
                {
                    ColdContainerUiIntegration.Open(state.Container);
                    nextBrowseRetry = Time.realtimeSinceStartup + 0.25f;
                }
                return ready;
            },
            8f, ActiveViewSummary);
        while (active.MoveNext()) yield return active.Current;
        state.RootContainerHandle = ColdContainerUiIntegration.ActiveHandle;
        state.InitialRevision = ColdContainerUiIntegration.ActiveRevision;
        state.InitialRows = ColdContainerUiIntegration.ActiveRowCount;
        state.ContainerSlot = FirstFreeSlot(ColdContainerUiIntegration.ActiveCapacity,
            ColdContainerUiIntegration.ActiveSlots);
        state.Context.Assert("container-has-free-slot", state.ContainerSlot >= 0,
            ActiveViewSummary());
    }

    private static void ResolveFixtures(RuntimeTestCommandDto command, ScenarioState state,
        bool foreignOwner, bool shellOwnedByLocal = true,
        bool requireCompatible = true, bool allowItemContainers = false)
    {
        Inventory inventory = Inventory.Instance ??
            throw new RuntimeTestUnsupportedException("inventory-unavailable");
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        if (localPlayerId == 0)
            throw new RuntimeTestUnsupportedException("local-player-id-unavailable");
        if (VRManager.IsVREnabled())
            throw new RuntimeTestUnsupportedException("cold-container-quick-move-non-vr-only");
        if (quickMoveAction == null || InventoryViewBase.Instance is not InventoryViewNonVR)
            throw new RuntimeTestUnsupportedException("inventory-quick-move-entry-point-unavailable");

        GameObject[] items = (inventory.GetItemsArray(false) ?? Array.Empty<GameObject>())
            .Where(item => item != null).Distinct().ToArray();
        ushort requestedShell = OptionalUShort(command, "shellNetId", 0);
        IEnumerable<NetworkedItem> shells = items.Select(item => item.GetComponent<NetworkedItem>())
            .Where(item => item != null && item.NetId != 0 &&
                item.GetComponent<ItemContainer>() != null &&
                (shellOwnedByLocal
                    ? item.PersistentOwnerPlayerId == localPlayerId
                    : item.PersistentOwnerPlayerId != 0 &&
                      item.PersistentOwnerPlayerId != localPlayerId) &&
                inventory.IndexOf(item.gameObject) >= 0 &&
                !inventory.GetSlotDroppedState(inventory.IndexOf(item.gameObject)));
        if (requestedShell != 0) shells = shells.Where(item => item.NetId == requestedShell);
        NetworkedItem[] availableShells = shells
            .OrderBy(item => inventory.IndexOf(item.gameObject)).ToArray();
        if (availableShells.Length == 0)
            throw new RuntimeTestUnsupportedException(requestedShell == 0 ?
                "no-owned-container-in-inventory" : "requested-container-unavailable");

        ushort requestedItem = OptionalUShort(command, "itemNetId", 0);
        IEnumerable<NetworkedItem> candidates = items
            .Select(item => item.GetComponent<NetworkedItem>())
            .Where(item => item != null && item.NetId != 0 &&
                (allowItemContainers || item.GetComponent<ItemContainer>() == null) &&
                inventory.IndexOf(item.gameObject) >= 0 &&
                !inventory.GetSlotDroppedState(inventory.IndexOf(item.gameObject)) &&
                (foreignOwner
                    ? item.PersistentOwnerPlayerId != 0 &&
                      item.PersistentOwnerPlayerId != localPlayerId
                    : item.PersistentOwnerPlayerId == localPlayerId));
        if (requestedItem != 0) candidates = candidates.Where(item => item.NetId == requestedItem);
        NetworkedItem[] availableItems = candidates
            .OrderBy(item => inventory.IndexOf(item.gameObject)).ToArray();
        foreach (NetworkedItem shell in availableShells)
        {
            ItemContainer container = shell.GetComponent<ItemContainer>();
            NetworkedItem compatible = availableItems.FirstOrDefault(item => item != shell &&
                (!requireCompatible || IsCompatible(container, item.gameObject)));
            if (compatible == null) continue;
            state.Shell = shell;
            state.Container = container;
            state.OriginalItem = compatible;
            break;
        }
        if (state.OriginalItem == null)
        {
            lock (state.Run)
                state.Run.Result["candidateContainers"] = availableShells.Select(shell =>
                    new Dictionary<string, object>
                    {
                        ["netId"] = shell.NetId,
                        ["prefabName"] = shell.Item?.InventorySpecs?.ItemPrefabName ?? shell.name,
                        ["slot"] = inventory.IndexOf(shell.gameObject)
                    }).ToArray();
            throw new RuntimeTestUnsupportedException(foreignOwner ?
                "no-compatible-foreign-owned-item-in-inventory" :
                "no-compatible-owned-item-in-inventory");
        }

        state.OriginalNetId = state.OriginalItem.NetId;
        state.InventoryResourceId = state.OriginalNetId.ToString();
        state.OriginalObject = state.OriginalItem.gameObject;
        state.ItemFixtureToken = OptionalString(command, "itemFixtureToken", string.Empty);
        InventoryRuntimeFixtureDriver.TrackLocalFixture(state.Shell,
            OptionalString(command, "shellFixtureToken", string.Empty));
        InventoryRuntimeFixtureDriver.TrackLocalFixture(state.OriginalItem,
            state.ItemFixtureToken);
        state.OriginalInventorySlot = inventory.IndexOf(state.OriginalObject);
        state.PrefabName = state.OriginalItem.Item?.InventorySpecs?.ItemPrefabName ??
            state.OriginalItem.name;
        state.OwnerPlayerId = state.OriginalItem.PersistentOwnerPlayerId;
        state.DetachedState = state.OriginalItem.GetComponent<ItemSaveData>()?.SaveItemData();
    }

    private static void RegisterCleanup(ScenarioState state)
    {
        state.Context.RegisterResource("container-ui", state.Shell.NetId.ToString(),
            "closed", () =>
            {
                if (Inventory.Instance?.ItemContainerRegistry?.ActiveContainer == state.Container)
                    state.Container.ToggleContainerAccess();
                ColdContainerUiIntegration.Reset();
            });
        state.Context.RegisterResource("inventory-item", state.InventoryResourceId,
            "restored-to-slot-" + state.OriginalInventorySlot,
            () => CleanupInventoryItem(state));
    }

    private static void MarkInventoryRestored(ScenarioState state) =>
        state.Context.MarkRestored("inventory-item", state.InventoryResourceId);

    private static IEnumerator CleanupInventoryItem(ScenarioState state)
    {
        if (state.WithdrawalAccepted)
        {
            if (state.MaterializedNetId != 0 &&
                NetworkedItem.TryGet(state.MaterializedNetId, out NetworkedItem restored) &&
                restored != null && InventoryIntegration.ContainsActive(restored.gameObject))
                yield break;
            throw new InvalidOperationException(
                "withdrawal-accepted-but-restoration-unverified");
        }

        bool depositMayHaveCommitted = state.Deposited || state.DepositRequested &&
            (!InventoryIntegration.ContainsActive(state.OriginalObject) ||
             ColdContainerUiIntegration.ActiveSlots.Contains(state.ContainerSlot));
        if (!depositMayHaveCommitted) yield break;

        if (state.RootContainerHandle != 0 &&
            ColdContainerUiIntegration.ActiveHandle != state.RootContainerHandle)
        {
            uint viewSequence = ColdContainerUiIntegration.ViewSequence;
            ColdContainerUiIntegration.Open(state.Container);
            float browseDeadline = Time.realtimeSinceStartup + 8f;
            while ((ColdContainerUiIntegration.ViewSequence <= viewSequence ||
                    ColdContainerUiIntegration.ActiveHandle != state.RootContainerHandle) &&
                   Time.realtimeSinceStartup < browseDeadline)
                yield return null;
            if (ColdContainerUiIntegration.ActiveHandle != state.RootContainerHandle)
                throw new InvalidOperationException(
                    "emergency-withdrawal-root-view-timeout");
        }

        uint sequence = ColdContainerUiIntegration.MutationSequence;
        if (!ColdContainerUiIntegration.TryInterceptRemove(state.Container,
                state.ContainerSlot, state.OriginalInventorySlot))
            throw new InvalidOperationException(
                "emergency-withdrawal-could-not-be-requested");

        float resultDeadline = Time.realtimeSinceStartup + 10f;
        while (ColdContainerUiIntegration.MutationSequence <= sequence &&
               Time.realtimeSinceStartup < resultDeadline)
            yield return null;
        if (ColdContainerUiIntegration.MutationSequence <= sequence)
            throw new InvalidOperationException("emergency-withdrawal-result-timeout");

        ClientboundContainerMutationResultPacket result =
            ColdContainerUiIntegration.LastMutation;
        RecordOperation(state, result);
        if (result?.Accepted != true || result.MaterializedItemNetId == 0)
            throw new InvalidOperationException("emergency-withdrawal-rejected:" +
                (result?.RejectionReason ?? "missing-result"));

        state.WithdrawalAccepted = true;
        state.Deposited = false;
        state.MaterializedNetId = result.MaterializedItemNetId;
        lock (state.Run)
        {
            state.Run.Result["materializedNetId"] = state.MaterializedNetId;
            state.Run.Result["operationIds"] = state.OperationIds.ToArray();
            state.Run.Result["emergencyWithdrawal"] = true;
        }

        NetworkedItem materialized = null;
        float projectionDeadline = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < projectionDeadline)
        {
            if (NetworkedItem.TryGet(state.MaterializedNetId, out materialized) &&
                materialized != null &&
                InventoryIntegration.ContainsActive(materialized.gameObject) &&
                Inventory.Instance.IndexOf(materialized.gameObject) ==
                    state.OriginalInventorySlot)
            {
                InventoryRuntimeFixtureDriver.TrackLocalFixture(materialized,
                    state.ItemFixtureToken);
                yield break;
            }
            yield return null;
        }
        throw new InvalidOperationException("emergency-withdrawal-projection-timeout");
    }

    private static void SnapshotFixtureResult(ScenarioState state)
    {
        lock (state.Run)
        {
            state.Run.Result["shellNetId"] = state.Shell.NetId;
            state.Run.Result["itemNetId"] = state.OriginalNetId;
            state.Run.Result["itemPrefabName"] = state.PrefabName;
            state.Run.Result["originalInventorySlot"] = state.OriginalInventorySlot;
            state.Run.Result["persistentOwnerPlayerId"] = state.OwnerPlayerId;
        }
    }

    private static void InvokeQuickMove(int slot, bool isContainerSlot)
    {
        if (InventoryViewBase.Instance is not InventoryViewNonVR view || quickMoveAction == null)
            throw new RuntimeTestUnsupportedException("inventory-quick-move-entry-point-unavailable");
        quickMoveAction.Invoke(view, new object[] { slot, false, isContainerSlot });
    }

    private static void RecordOperation(ScenarioState state,
        ClientboundContainerMutationResultPacket result)
    {
        if (result?.OperationId?.Length != 16) return;
        string operationId = new Guid(result.OperationId).ToString("D");
        state.OperationIds.Add(operationId);
        lock (state.Run) state.Run.Result["operationIds"] = state.OperationIds.ToArray();
    }

    private static int FirstFreeSlot(int capacity, IEnumerable<int> occupied)
    {
        HashSet<int> used = new(occupied ?? Array.Empty<int>());
        for (int slot = 0; slot < capacity; slot++)
            if (!used.Contains(slot)) return slot;
        return -1;
    }

    private static bool IsCompatible(ItemContainer container, GameObject item)
    {
        try { return container != null && item != null && container.ValidItem(item); }
        catch { return false; }
    }

    private static string ActiveViewSummary() =>
        $"active={ColdContainerUiIntegration.Active},handle={ColdContainerUiIntegration.ActiveHandle}," +
        $"revision={ColdContainerUiIntegration.ActiveRevision},rows={ColdContainerUiIntegration.ActiveRowCount}," +
        $"capacity={ColdContainerUiIntegration.ActiveCapacity}";

    private static ushort OptionalUShort(RuntimeTestCommandDto command, string key,
        ushort fallback) => command.Parameters.TryGetValue(key, out string value) &&
        ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out ushort parsed) ? parsed : fallback;

    private static string OptionalString(RuntimeTestCommandDto command, string key,
        string fallback) => command.Parameters.TryGetValue(key, out string value) &&
        !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static bool OptionalBool(RuntimeTestCommandDto command, string key,
        bool fallback) => command.Parameters.TryGetValue(key, out string value) &&
        bool.TryParse(value, out bool parsed) ? parsed : fallback;

    private static int OptionalInt(RuntimeTestCommandDto command, string key,
        int fallback) => command.Parameters.TryGetValue(key, out string value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int parsed) ? parsed : fallback;
}
#endif
