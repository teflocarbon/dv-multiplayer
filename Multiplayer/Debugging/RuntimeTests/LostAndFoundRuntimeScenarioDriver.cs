#if DEBUG
using DV.InventorySystem;
using DV.Utils;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Integrations.Inventory;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Managers.Client;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests;

internal sealed class LostAndFoundRuntimeScenarioDriver
{
    private sealed class State
    {
        public RuntimeTestScenarioContext Context;
        public RuntimeTestRunDto Run;
        public NetworkedItem Item;
        public ushort NetId;
        public int OriginalSlot;
        public byte OwnerPlayerId;
        public NetworkClient Client;
        public uint PendingRequestId;
        public ClientboundLostItemRetrieveResultPacket Result;
        public Action<ClientboundLostItemRetrieveResultPacket> ResultHandler;
        public Vector3 OriginalPlayerAbsolute;
        public Quaternion OriginalPlayerRotation;
        public bool ExpectedPersonal = true;
    }

    private static uint nextRequestId = 4000000000;

    public IEnumerator AutomaticRoundTrip(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, AutomaticRoundTripBody);

    public IEnumerator StaleRevisionRecovery(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, StaleRevisionRecoveryBody);

    public IEnumerator FullInventoryRecovery(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, FullInventoryRecoveryBody);

    public IEnumerator EssentialStarRecall(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, EssentialStarRecallBody);

    public IEnumerator RepeatedRoundTrip(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, RepeatedRoundTripBody);

    public IEnumerator OwnerNearbyProtection(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, OwnerNearbyProtectionBody);

    public IEnumerator OtherPlayerNearbyProtection(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, OtherPlayerNearbyProtectionBody);

    public IEnumerator NonPersonalExclusion(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, NonPersonalExclusionBody);

    public IEnumerator PrivateOwnerList(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, PrivateOwnerListBody);

    public IEnumerator UnknownHandleRejection(RuntimeTestCommandDto command,
        RuntimeTestRunDto run) => Run(command, run, UnknownHandleRejectionBody);

    private static IEnumerator Run(RuntimeTestCommandDto command, RuntimeTestRunDto run,
        Func<State, IEnumerator> body)
    {
        RuntimeTestScenarioContext context = new(command, run);
        State state = new() { Context = context, Run = run };
        return RuntimeTestScenarioRunner.Run(context, Execute(command, state, body));
    }

    private static IEnumerator Execute(RuntimeTestCommandDto command, State state,
        Func<State, IEnumerator> body)
    {
        state.Context.EnterPhase("prepare", "resolve-lost-and-found-fixture");
        if (!command.Parameters.TryGetValue("itemNetId", out string raw) ||
            !ushort.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out state.NetId) || state.NetId == 0)
            throw new ArgumentException("missing-or-invalid-itemNetId");
        if (!NetworkedItem.TryGet(state.NetId, out state.Item) || state.Item == null)
            throw new InvalidOperationException("fixture-item-not-projected:" + state.NetId);
        if (command.Parameters.TryGetValue("itemFixtureToken", out string fixtureToken))
            InventoryRuntimeFixtureDriver.TrackLocalFixture(state.Item, fixtureToken);
        if (command.Parameters.TryGetValue("itemBelongsToPlayer", out string rawPersonal) &&
            bool.TryParse(rawPersonal, out bool expectedPersonal))
            state.ExpectedPersonal = expectedPersonal;
        state.Client = NetworkLifecycle.Instance.Client ??
            throw new RuntimeTestUnsupportedException("network-client-unavailable");
        state.OriginalSlot = Inventory.Instance?.IndexOf(state.Item.gameObject) ?? -1;
        state.OriginalPlayerAbsolute = PlayerManager.PlayerTransform.GetWorldAbsolutePosition();
        state.OriginalPlayerRotation = PlayerManager.PlayerTransform.rotation;
        state.OwnerPlayerId = state.Item.PersistentOwnerPlayerId;
        state.ResultHandler = packet =>
        {
            if (packet != null && packet.RequestId == state.PendingRequestId)
                state.Result = packet;
        };
        state.Client.LostItemRetrieveCompleted += state.ResultHandler;
        state.Context.RegisterResource("network-event", "lost-item-retrieve-result",
            "unsubscribed", () =>
                state.Client.LostItemRetrieveCompleted -= state.ResultHandler);
        state.Context.RegisterResource("player-position", "local-player",
            "restored-to-pre-scenario-position", () => RestorePlayer(state));
        lock (state.Run)
        {
            state.Run.Result["itemNetId"] = state.NetId;
            state.Run.Result["originalSlot"] = state.OriginalSlot;
            state.Run.Result["ownerPlayerId"] = state.OwnerPlayerId;
        }
        IEnumerator operation = body(state);
        while (operation.MoveNext()) yield return operation.Current;
    }

    private static IEnumerator AutomaticRoundTripBody(State state)
    {
        AssertOwnedPersonalFixture(state);
        yield return CollectFarAway(state);
        LostItemData lost = RequireListed(state);
        state.Context.EnterPhase("act", "retrieve-through-network-api");
        RequestNormal(state, lost, state.OriginalSlot);
        yield return AwaitResult(state, true, string.Empty);
        yield return AssertRestored(state, lost, state.OriginalSlot);
    }

    private static IEnumerator StaleRevisionRecoveryBody(State state)
    {
        AssertOwnedPersonalFixture(state);
        yield return CollectFarAway(state);
        LostItemData lost = RequireListed(state);
        state.Context.EnterPhase("act", "reject-stale-lost-item-revision");
        state.PendingRequestId = NextRequestId();
        state.Result = null;
        state.Client.RequestLostItemRetrieval(state.PendingRequestId, lost.Handle,
            state.NetId, lost.Revision == 0 ? uint.MaxValue : lost.Revision - 1,
            state.OriginalSlot);
        yield return AwaitResult(state, false, "stale-lost-item-revision");
        state.Context.Assert("stale-rejection-preserved-list-entry",
            FindListed(state.NetId) != null, SnapshotList());
        state.Context.EnterPhase("act", "recover-with-current-revision");
        RequestNormal(state, lost, state.OriginalSlot);
        yield return AwaitResult(state, true, string.Empty);
        yield return AssertRestored(state, lost, state.OriginalSlot);
    }

    private static IEnumerator FullInventoryRecoveryBody(State state)
    {
        AssertOwnedPersonalFixture(state);
        yield return CollectFarAway(state);
        LostItemData lost = RequireListed(state);
        state.Context.EnterPhase("act", "reject-forged-full-inventory-evidence");
        int capacity = Math.Max(1, Inventory.Instance?.Capacity ?? 1);
        state.PendingRequestId = NextRequestId();
        state.Result = null;
        state.Client.RequestLostItemRetrievalForRuntimeTest(state.PendingRequestId,
            lost.Handle, lost.Revision, -1, -1, capacity,
            Enumerable.Range(0, capacity).ToArray());
        yield return AwaitResult(state, false, "inventory-full");
        state.Context.Assert("full-inventory-rejection-preserved-list-entry",
            FindListed(state.NetId) != null, SnapshotList());
        RequestNormal(state, lost, state.OriginalSlot);
        yield return AwaitResult(state, true, string.Empty);
        yield return AssertRestored(state, lost, state.OriginalSlot);
    }

    private static IEnumerator EssentialStarRecallBody(State state)
    {
        AssertOwnedPersonalFixture(state);
        state.Context.Assert("fixture-is-essential",
            state.Item.Item?.InventorySpecs?.IsEssential == true,
            state.Item.Item?.InventorySpecs?.IsEssential);
        state.Context.Assert("essential-fixture-has-inventory-slot", state.OriginalSlot >= 0,
            state.OriginalSlot);
        yield return CollectFarAway(state);
        LostItemData lost = RequireListed(state);
        state.Context.EnterPhase("act", "invoke-base-game-star-recall-route");
        state.Context.Assert("star-recall-request-routed",
            NetworkedItemManager.Instance.RequestItemRecall(state.Item,
                state.OriginalSlot));
        yield return state.Context.Eventually("star-recall-removed-lost-entry",
            () => FindListed(state.NetId) == null, 15f, SnapshotList);
        yield return state.Context.Eventually("star-recall-revived-exact-slot",
            () => Inventory.Instance.IndexOf(state.Item.gameObject) == state.OriginalSlot &&
                InventoryIntegration.ContainsActive(state.Item.gameObject), 15f,
            () => Inventory.Instance.IndexOf(state.Item.gameObject));
        state.Context.Assert("star-recall-advanced-authority-revision",
            state.Item.AuthorityRevision > lost.Revision, state.Item.AuthorityRevision);
    }

    private static IEnumerator RepeatedRoundTripBody(State state)
    {
        AssertOwnedPersonalFixture(state);
        uint previousHandle = 0;
        uint previousRevision = 0;
        for (int cycle = 1; cycle <= 2; cycle++)
        {
            state.Context.EnterPhase("act", "collection-cycle-" + cycle);
            yield return CollectFarAway(state);
            LostItemData lost = RequireListed(state);
            state.Context.Assert("cycle-" + cycle + "-handle-allocated",
                lost.Handle != 0, lost.Handle);
            state.Context.Assert("cycle-" + cycle + "-revision-advanced",
                lost.Revision > previousRevision, lost.Revision);
            if (previousHandle != 0)
                state.Context.Assert("second-cycle-uses-new-compact-handle",
                    lost.Handle != previousHandle, lost.Handle);
            previousHandle = lost.Handle;
            previousRevision = lost.Revision;
            RequestNormal(state, lost, state.OriginalSlot);
            yield return AwaitResult(state, true, string.Empty);
            yield return AssertRestored(state, lost, state.OriginalSlot);
        }
    }

    private static IEnumerator OwnerNearbyProtectionBody(State state)
    {
        AssertOwnedPersonalFixture(state);
        state.Context.EnterPhase("act", "drop-personal-item-near-owner");
        yield return PlaceWorld(state, PlayerManager.PlayerTransform.GetWorldAbsolutePosition() +
            PlayerManager.PlayerTransform.forward * 1.5f);
        float wait = global::Multiplayer.Multiplayer.Settings
            .LostItemCollectionGraceSeconds + 2f;
        yield return WaitSeconds(wait);
        state.Context.Assert("near-owner-item-not-collected",
            FindListed(state.NetId) == null, SnapshotList());
        state.Context.Assert("near-owner-world-representation-preserved",
            NetworkedItem.TryGet(state.NetId, out NetworkedItem item) && item != null &&
            item.gameObject.activeSelf);
    }

    private static IEnumerator OtherPlayerNearbyProtectionBody(State state)
    {
        AssertOwnedPersonalFixture(state);
        state.Context.EnterPhase("act", "leave-item-with-nearby-host-player");
        var nearbyPlayer = state.Client.ClientPlayerManager.Players
            .FirstOrDefault(player => player != null &&
                player.PlayerId != InventoryIntegration.LocalPlayerId);
        state.Context.Assert("remote-player-projection-available", nearbyPlayer != null,
            nearbyPlayer?.PlayerId ?? 0);
        Vector3 sharedLocation = nearbyPlayer.transform.GetWorldAbsolutePosition() +
            nearbyPlayer.transform.forward * 1.5f;
        yield return PlaceWorld(state, sharedLocation);
        Vector3 remote = state.OriginalPlayerAbsolute +
            new Vector3(global::Multiplayer.Multiplayer.Settings.LostItemOwnerDistance + 500f,
                20f, global::Multiplayer.Multiplayer.Settings.LostItemOwnerDistance + 500f);
        PlayerManager.TeleportPlayer(remote + WorldMover.currentMove,
            state.OriginalPlayerRotation, null, true, false);
        yield return WaitSeconds(1f);
        float wait = global::Multiplayer.Multiplayer.Settings
            .LostItemCollectionGraceSeconds + 2f;
        yield return WaitSeconds(wait);
        state.Context.Assert("other-nearby-player-protected-item",
            FindListed(state.NetId) == null, SnapshotList());
    }

    private static IEnumerator NonPersonalExclusionBody(State state)
    {
        state.Context.Assert("host-fixture-requested-as-nonpersonal", !state.ExpectedPersonal,
            state.ExpectedPersonal);
        yield return PlaceFarAway(state);
        yield return WaitSeconds(3f);
        state.Context.Assert("nonpersonal-item-never-listed",
            FindListed(state.NetId) == null, SnapshotList());
        state.Context.Assert("nonpersonal-world-item-remains-materialized",
            NetworkedItem.TryGet(state.NetId, out NetworkedItem item) && item != null &&
            item.gameObject.activeSelf);
    }

    private static IEnumerator PrivateOwnerListBody(State state)
    {
        state.Context.Assert("fixture-owned-by-different-player",
            state.OwnerPlayerId != InventoryIntegration.LocalPlayerId,
            state.OwnerPlayerId);
        yield return PlaceFarAway(state);
        float timeout = global::Multiplayer.Multiplayer.Settings
            .LostItemCollectionGraceSeconds + 12f;
        yield return state.Context.Eventually("foreign-owned-item-collected-out-of-interest",
            () => !NetworkedItem.TryGet(state.NetId, out NetworkedItem projected) ||
                projected == null || !projected.gameObject.activeSelf,
            timeout, () => NetworkedItem.TryGet(state.NetId, out NetworkedItem item) &&
                item != null ? item.DebugCurrentState.ToString() : "absent");
        state.Context.Assert("foreign-owner-entry-not-disclosed-to-client",
            FindListed(state.NetId) == null, SnapshotList());
    }

    private static IEnumerator UnknownHandleRejectionBody(State state)
    {
        state.Context.EnterPhase("act", "request-unknown-lost-item-handle");
        state.PendingRequestId = NextRequestId();
        state.Result = null;
        state.Client.RequestLostItemRetrievalForRuntimeTest(state.PendingRequestId,
            uint.MaxValue, 1, -1, -1, Math.Max(1, Inventory.Instance.Capacity),
            Array.Empty<int>());
        yield return AwaitResult(state, false, "unknown-lost-item");
        state.Context.Assert("unknown-request-did-not-mutate-fixture",
            Inventory.Instance.IndexOf(state.Item.gameObject) == state.OriginalSlot,
            Inventory.Instance.IndexOf(state.Item.gameObject));
    }

    private static void AssertOwnedPersonalFixture(State state)
    {
        state.Context.Assert("fixture-owned-by-target-player",
            state.OwnerPlayerId == InventoryIntegration.LocalPlayerId,
            state.OwnerPlayerId);
        state.Context.Assert("fixture-is-personal",
            state.Item.Item?.InventorySpecs?.BelongsToPlayer == true,
            state.Item.Item?.InventorySpecs?.BelongsToPlayer);
    }

    private static IEnumerator CollectFarAway(State state)
    {
        state.Context.EnterPhase("act", "move-item-beyond-collection-distance");
        yield return PlaceFarAway(state);
        float timeout = global::Multiplayer.Multiplayer.Settings
            .LostItemCollectionGraceSeconds + 12f;
        yield return state.Context.Eventually("item-automatically-listed-for-owner",
            () => FindListed(state.NetId) != null, timeout, SnapshotList);
        LostItemData lost = FindListed(state.NetId);
        state.Context.Assert("lost-handle-is-compact-and-nonzero",
            lost?.Handle > 0, lost?.Handle);
        state.Context.Assert("lost-record-retains-runtime-netid",
            lost?.NetId == state.NetId, lost?.NetId);
        state.Context.Assert("lost-record-revision-is-authoritative",
            lost != null && lost.Revision > 0, lost?.Revision);
        lock (state.Run)
        {
            state.Run.Result["lostHandle"] = lost?.Handle ?? 0;
            state.Run.Result["lostRevision"] = lost?.Revision ?? 0;
        }
    }

    private static IEnumerator PlaceFarAway(State state)
    {
        Vector3 remote = state.OriginalPlayerAbsolute +
            new Vector3(global::Multiplayer.Multiplayer.Settings.LostItemOwnerDistance + 500f,
                20f, global::Multiplayer.Multiplayer.Settings.LostItemOwnerDistance + 500f);
        PlayerManager.TeleportPlayer(remote + WorldMover.currentMove,
            state.OriginalPlayerRotation, null, true, false);
        yield return WaitSeconds(1f);
        yield return PlaceWorld(state, remote + PlayerManager.PlayerTransform.forward * 1.5f);
        yield return WaitSeconds(1f);
        PlayerManager.TeleportPlayer(state.OriginalPlayerAbsolute + WorldMover.currentMove,
            state.OriginalPlayerRotation, null, true, false);
        yield return WaitSeconds(1f);
    }

    private static IEnumerator RestorePlayer(State state)
    {
        if (PlayerManager.PlayerTransform != null)
            PlayerManager.TeleportPlayer(state.OriginalPlayerAbsolute + WorldMover.currentMove,
                state.OriginalPlayerRotation, null, true, false);
        yield return null;
    }

    private static IEnumerator PlaceWorld(State state, Vector3 absolute)
    {
        state.Item.Item?.ForceEndInteraction();
        Inventory.Instance.DropItemFromHandsOrInventory(state.Item.gameObject);
        state.Item.transform.position = absolute + WorldMover.currentMove;
        yield return null;
        yield return new WaitForEndOfFrame();
        state.Item.ProcessLocalStateObservation("runtime-lost-and-found-arrangement");
        yield return null;
    }

    private static void RequestNormal(State state, LostItemData lost, int slot)
    {
        state.PendingRequestId = NextRequestId();
        state.Result = null;
        state.Client.RequestLostItemRetrieval(state.PendingRequestId, lost.Handle,
            state.NetId, lost.Revision, slot);
    }

    private static IEnumerator AwaitResult(State state, bool accepted, string reason)
    {
        yield return state.Context.Eventually("retrieve-result-received-" +
            state.PendingRequestId, () => state.Result != null, 15f,
            () => state.PendingRequestId);
        state.Context.Assert("retrieve-result-acceptance-" + state.PendingRequestId,
            state.Result.Accepted == accepted, state.Result.Accepted);
        state.Context.Assert("retrieve-result-reason-" + state.PendingRequestId,
            string.Equals(state.Result.RejectionReason ?? string.Empty, reason,
                StringComparison.Ordinal), state.Result.RejectionReason ?? string.Empty);
    }

    private static IEnumerator AssertRestored(State state, LostItemData lost, int slot)
    {
        yield return state.Context.Eventually("retrieved-entry-removed-from-list",
            () => FindListed(state.NetId) == null, 15f, SnapshotList);
        yield return state.Context.Eventually("retrieved-item-restored-to-inventory",
            () => Inventory.Instance.IndexOf(state.Item.gameObject) == slot &&
                InventoryIntegration.ContainsActive(state.Item.gameObject), 15f,
            () => Inventory.Instance.IndexOf(state.Item.gameObject));
        state.Context.Assert("retrieval-preserved-persistent-owner",
            state.Item.PersistentOwnerPlayerId == state.OwnerPlayerId,
            state.Item.PersistentOwnerPlayerId);
        state.Context.Assert("retrieval-advanced-authority-revision",
            state.Item.AuthorityRevision > lost.Revision, state.Item.AuthorityRevision);
    }

    private static LostItemData RequireListed(State state) => FindListed(state.NetId) ??
        throw new InvalidOperationException("lost-item-not-listed:" + state.NetId);

    private static LostItemData FindListed(ushort netId) =>
        NetworkedLostAndFoundManager.ClientItems.FirstOrDefault(item => item.NetId == netId);

    private static object SnapshotList() => NetworkedLostAndFoundManager.ClientItems
        .Select(item => new { item.Handle, item.NetId, item.Revision, item.PrefabName })
        .ToArray();

    private static uint NextRequestId() => unchecked(++nextRequestId);

    private static IEnumerator WaitSeconds(float seconds)
    {
        float started = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - started < seconds) yield return null;
    }
}
#endif
