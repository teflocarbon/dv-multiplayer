using DV.Customization.Paint;
using DV.Damage;
using DV.LocoRestoration;
using DV.Logic.Job;
using DV.MultipleUnit;
using DV.ThingTypes;
using DV.Utils;
using LiteNetLib;
using Multiplayer.Client.Trains;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Players;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Common.Train;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Shared.World;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Client.Session;

// NetworkClient's trains handlers and sends (see docs/design/code-layout.md).
public partial class NetworkClient : ITrainClient
{
    public ITrainCarClientPart CreateCarPart(NetworkedTrainCar car) => new global::Multiplayer.Client.Trains.NetworkedTrainCarClient(car);

    public bool TryFollowingHostUncoupleCause(Coupler coupler, Coupler other, out ChangeCause<UncoupleReason> cause)
    {
        cause = default;
        // The host's topology is its own (its loopback client applies none of it).
        if (!NetworkedTrainCar.IsApplyingAuthoritativeTopology || NetworkLifecycle.Instance.IsHost())
            return false;
        if (RefusalUncoupleCause is ChangeCause<UncoupleReason> refused)
            cause = refused;
        else if (!TakeHostUncoupleCause(coupler, other, out cause))
            cause = TrainChangeCauses.Cause(UncoupleReason.FollowingHost);
        return true;
    }

    // The host's own reason for an uncouple this client is following, if it sent one.
    private bool TakeHostUncoupleCause(Coupler coupler, Coupler other, out ChangeCause<UncoupleReason> cause)
    {
        foreach (Coupler end in new[] { coupler, other })
        {
            ushort netId = end?.train?.GetNetId() ?? 0;
            var key = (netId, end != null && end.isFrontCoupler ? TrainEnd.Front : TrainEnd.Rear);
            if (netId != 0 && hostUncoupleCauses.TryGetValue(key, out cause))
            {
                hostUncoupleCauses.Remove(key);
                return true;
            }
        }
        cause = default;
        return false;
    }

    private void OnClientboundSpawnTrainSetPacket(ClientboundSpawnTrainSetPacket packet)
    {
        if (packet == null || packet.SpawnParts == null)
        {
            RejectTrainBaseline("spawn packet contained a null trainset");
            return;
        }

        if (packet.BaselineId != 0)
        {
            TrainBaselineDisposition disposition =
                trainBaseline.Admit(packet.BaselineId, packet.Sequence, out string admissionFailure);
            if (disposition == TrainBaselineDisposition.Duplicate)
                return;
            if (disposition == TrainBaselineDisposition.Reject)
            {
                RejectTrainBaseline(admissionFailure);
                return;
            }
        }

        LogDebug(() => $"Spawning trainset consisting of {string.Join(", ", packet.SpawnParts.Select(p => p == null ? "<null>" : $"{p.CarId} ({p.LiveryId}) with netId: {p.NetId}"))}");

        if (!NetworkedCarSpawner.TrySpawnCars(packet.SpawnParts, packet.AutoCouple, false, out string failure))
        {
            RejectTrainBaseline($"trainset {packet.Sequence} could not be applied: {failure}");
            return;
        }

        if (packet.BaselineId != 0)
        {
            if (!trainBaseline.Commit(packet.Sequence, (uint)packet.SpawnParts.Length))
            {
                RejectTrainBaseline(trainBaseline.Failure);
                return;
            }
            trainSetsSpawned++;
        }
    }

    private void OnClientboundTrainBaselineBeginPacket(ClientboundTrainBaselineBeginPacket packet)
    {
        if (packet == null)
        {
            RejectTrainBaseline("server sent an invalid manifest identity");
            return;
        }

        trainSetsToSpawn = packet.TrainsetCount;
        trainSetsSpawned = 0;
        trainTopology.Reset(packet.BaselineId);
        if (!trainBaseline.Begin(packet.BaselineId, packet.TrainsetCount, packet.CarCount,
            packet.Failure))
        {
            RejectTrainBaseline(trainBaseline.Failure);
            return;
        }
        Log($"Received rolling-stock baseline {packet.BaselineId}: {packet.TrainsetCount} trainsets, {packet.CarCount} cars");
    }

    private void OnClientboundTrainBaselineCompletePacket(ClientboundTrainBaselineCompletePacket packet)
    {
        if (packet == null)
        {
            RejectTrainBaseline("server sent a null completion manifest");
            return;
        }
        if (!trainBaseline.TryComplete(packet.BaselineId, packet.TrainsetCount, packet.CarCount))
        {
            RejectTrainBaseline(trainBaseline.Failure);
            return;
        }
        if (!trainTopology.BaselineApplied || trainTopology.BaselineId != packet.BaselineId)
        {
            if (trainTopology.HasPending)
            {
                StartTrainTopologyRetry();
                LogWarning($"Waiting for rolling-stock baseline {packet.BaselineId} native readiness");
                return;
            }
            RejectTrainBaseline("canonical train topology baseline was not received or was invalid");
            return;
        }
    }

    private void OnClientboundTrainTopologySnapshotPacket(
        ClientboundTrainTopologySnapshotPacket packet)
    {
        if (packet?.OperationId != 0 && packet.Result != TrainTopologyResultCode.Accepted)
        {
            LogWarning($"Train topology operation {packet.OperationId} was rejected: " +
                       $"{packet.Result}{(string.IsNullOrEmpty(packet.Detail) ? string.Empty : $", {packet.Detail}")}; " +
                       $"authoritative revision {packet.TopologyRevision}");
            // Undoing our own change: the uncouples it makes are the refusal's.
            if (packet.OperationId >> 56 == PlayerId)
                RefusalUncoupleCause = TrainChangeCauses.LocalCause(UncoupleReason.CoupleRefused,
                    string.IsNullOrEmpty(packet.Detail) ? packet.Result.ToString() : $"{packet.Result}, {packet.Detail}");
        }
        if (packet?.Uncouples != null)
        {
            if (hostUncoupleCauses.Count > 512)
                hostUncoupleCauses.Clear(); // causes for couplings this client never had
            foreach (TrainUncoupleCauseData uncouple in packet.Uncouples)
                if (uncouple != null)
                    hostUncoupleCauses[(uncouple.CarNetId, uncouple.End)] = uncouple.Cause;
        }

        if (trainTopology.Receive(packet, out string failure))
        {
            RefusalUncoupleCause = null;
            DebugRuntime.Publish("train", "train.topology-snapshot-applied",
                DebugRuntimeSide.Client, entityType: "Topology",
                entityId: packet.TopologyRevision.ToString(),
                data: DebugValueSnapshotter.SnapshotObject(new
                {
                    packet.BaselineId,
                    packet.TopologyRevision,
                    packet.OperationId,
                    result = packet.Result.ToString(),
                    endpointCount = packet.Endpoints?.Length ?? 0,
                    brakeLineCount = packet.BrakeLines?.Length ?? 0
                }));
            return;
        }

        LogWarning($"Deferred/rejected topology snapshot r{packet?.TopologyRevision}: {failure}");
        if (!trainTopology.HasPending)
            RefusalUncoupleCause = null;
        if (trainTopology.HasPending)
        {
            StartTrainTopologyRetry();
            return;
        }
        if (packet?.BaselineId != 0 && LoadingState == PlayerLoadingState.ReadyForTrainSets)
        {
            RejectTrainBaseline(failure);
            return;
        }
        if (LoadingState > PlayerLoadingState.ReadyForTrainSets &&
            !failure.StartsWith("endpoint-not-ready:"))
            RequestTrainTopologySync();
    }

    private void StartTrainTopologyRetry()
    {
        if (trainTopologyRetryRunning)
            return;
        trainTopologyRetryRunning = true;
        SingletonBehaviour<CoroutineManager>.Instance.Run(RetryPendingTrainTopology());
    }

    private IEnumerator RetryPendingTrainTopology()
    {
        float deadline = Time.realtimeSinceStartup + 15f;
        string failure = string.Empty;
        try
        {
            while (trainTopology.HasPending && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(0.05f);
                if (trainTopology.TryApplyPending(out failure))
                {
                    Log($"Applied deferred canonical train topology r{trainTopology.Revision}");
                    yield break;
                }
            }

            if (!trainTopology.HasPending)
                yield break;
            string reason = string.IsNullOrEmpty(failure)
                ? "native readiness timeout"
                : failure;
            if (LoadingState == PlayerLoadingState.ReadyForTrainSets &&
                trainBaseline.Started && !trainTopology.BaselineApplied)
                RejectTrainBaseline("canonical train topology timeout: " + reason);
            else
            {
                LogWarning("Canonical train topology retry timed out: " + reason);
                RequestTrainTopologySync();
            }
        }
        finally
        {
            trainTopologyRetryRunning = false;
            RefusalUncoupleCause = null;
        }
    }

    private void RejectTrainBaseline(string reason)
    {
        if (LoadingState == PlayerLoadingState.ReadyForTrainSets)
            trainBaseline.Reject(reason);
        LogError($"Rolling-stock baseline rejected: {reason}");
    }

    private void OnClientboundDestroyTrainCarPacket(ClientboundDestroyTrainCarPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
        {
            LogWarning($"Received DestroyTrainCarPacket for netId: {packet.NetId}, but NetworkedTrainCar was not found.");
            return;
        }

        Log($"Received DestroyTrainCarPacket for [{netTrainCar.CurrentID} {packet.NetId}]");

        // Players on the car (us included) are stepped off it by CarDeletePlayerSafetyPatch;
        // clearing PlayerManager.Car alone left our player parented to the deleted car.

        //Protect other players from getting deleted in race conditions - this should be a temporary fix, if another playe's game object is deleted we should just recreate it
        if (netTrainCar == null || netTrainCar.gameObject == null || netTrainCar.TrainCar == null)
        {
            LogDebug(() => $"OnClientboundDestroyTrainCarPacket({packet?.NetId}) networkedTrainCar: {netTrainCar != null}, trainCar: {netTrainCar?.TrainCar != null}");
        }
        else
        {
            NetworkedPlayer[] componentsInChildren = (netTrainCar?.gameObject != null) ? netTrainCar.GetComponentsInChildren<NetworkedPlayer>() : [];

            foreach (NetworkedPlayer networkedPlayer in componentsInChildren)
            {
                networkedPlayer.UpdateCar(0);
            }

            netTrainCar.TrainCar.UpdateJobIdOnCarPlates(string.Empty);
            using (TrainChangeCauses.Scope(packet.Cause))
                CarSpawner.Instance.DeleteCar(netTrainCar.TrainCar);
        }
    }

    private void OnClientboundTrainDerailCausePacket(ClientboundTrainDerailCausePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar car))
            return;
        car.LastDerail = packet.Cause;
        Log($"{car.CurrentID} derailed on the host: {packet.Cause}");
    }

    public void OnClientboundTrainPhysicsPacket(ClientboundTrainsetPhysicsPacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client);
        //LogDebug(() => $"Received Physics packet for netId: {packet.FirstNetId}, tick: {packet.Tick}");
        global::Multiplayer.Client.Trains.TrainsetPhysicsFollower.HandleTrainsetPhysicsUpdate(packet);
    }

    private void OnCommonCouplerInteractionPacket(CommonCouplerInteractionPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
        {
            LogError($"OnCommonCouplerInteractionPacket netId: {packet.NetId}, TrainCar not found!");
            return;
        }

        netTrainCar.Common_ReceiveCouplerInteraction(packet);
    }

    private void OnClientboundTrainControlAuthorityUpdatePacket(ClientboundTrainControlAuthorityUpdatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        networkedTrainCar.Client()?.ReceiveAuthorityUpdate(packet.PortNetId, packet.State);
    }

    private void OnClientboundBrakeStateUpdatePacket(ClientboundBrakeStateUpdatePacket packet)
    {
        if (packet.TopologyRevision > trainTopology.Revision)
        {
            if (packet.TopologyRevision != lastDeferredBrakeTopologyRevision ||
                Time.realtimeSinceStartup >= nextDeferredBrakeTopologyWarningAt)
            {
                lastDeferredBrakeTopologyRevision = packet.TopologyRevision;
                nextDeferredBrakeTopologyWarningAt = Time.realtimeSinceStartup + 2f;
                LogWarning($"Deferred brake state for topology r{packet.TopologyRevision}; local r{trainTopology.Revision}");
            }
            RequestTrainTopologySync();
            return;
        }
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        if (networkedTrainCar.Client()?.ReceiveBrakeStateUpdate(packet) == false)
        {
            LogWarning($"Brake line membership mismatch for car {packet.NetId}; requesting topology resync");
            RequestTrainTopologySync();
        }

        //LogDebug(() => $"Received Brake Pressures netId {packet.NetId}: {packet.MainReservoirPressure}, {packet.IndependentPipePressure}, {packet.BrakePipePressure}, {packet.BrakeCylinderPressure}");
    }

    private void OnClientboundExplosionPacket(ClientboundExplosionPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.CarNetId, out TrainCar car) || car == null)
            return;
        Log($"The host's {car.ID} exploded ({(global::Multiplayer.Shared.Trains.ExplosionSync.Kind)packet.Kind})");
        global::Multiplayer.Client.Trains.ExplosionPlayback.Play(car, (global::Multiplayer.Shared.Trains.ExplosionSync.Kind)packet.Kind, packet.Index);
    }

    private void OnClientboundCargoFirePacket(ClientboundCargoFirePacket packet)
    {
        if (NetworkedTrainCar.TryGet(packet.CarNetId, out TrainCar car) && car != null)
            global::Multiplayer.Client.Trains.CargoFirePlayback.Receive(car, packet.Index, (global::Multiplayer.Shared.Trains.CargoFireSync.FireState)packet.State);
    }

    private void OnClientboundCargoStatePacket(ClientboundCargoStatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        LogDebug(() => $"OnClientboundCargoStatePacket() {networkedTrainCar.CurrentID}, IsLoading: {packet.IsLoading}, CargoType: {packet.CargoTypeNetId}, CargoAmount: {packet.CargoAmount}, Health: {packet.CargoHealth}, CargoModelIndex: {packet.CargoModelIndex}, WarehouseMachineId: {packet.WarehouseMachineNetId}");

        networkedTrainCar.CargoModelIndex = packet.CargoModelIndex;
        Car logicCar = networkedTrainCar.TrainCar.logicCar;

        if (logicCar == null)
        {
            LogWarning($"OnClientboundCargoStatePacket() Failed to find logic car for [{networkedTrainCar.TrainCar.ID}, {packet.NetId}] is initialised: {networkedTrainCar.Client_Initialized}");
            return;
        }

        CargoTypeLookup.Instance.TryGet(packet.CargoTypeNetId, out CargoType cargoType);

        if (cargoType == CargoType.None && logicCar.CurrentCargoTypeInCar == CargoType.None)
            return;

        //packet.CargoAmount is the total amount, not the amount to load/unload
        float cargoAmount = Mathf.Clamp(packet.CargoAmount, 0, logicCar.capacity);

        WarehouseMachine warehouseMachine = null;
        if (packet.WarehouseMachineNetId != 0 && (!WarehouseMachineLookup.TryGet(packet.WarehouseMachineNetId, out warehouseMachine) || warehouseMachine == null))
        {
            LogWarning($"OnClientboundCargoStatePacket() Failed to find WarehouseMachine for netId {packet.WarehouseMachineNetId}");
            return;
        }

        if (packet.IsLoading)
        {
            LogDebug(() => $"OnClientboundCargoStatePacket() Loading cargo: {cargoType} into {networkedTrainCar.CurrentID}, current amount: {packet.CargoAmount}");
            //Check correct cargo is loaded and the amount is correct
            if (logicCar.LoadedCargoAmount == cargoAmount && logicCar.CurrentCargoTypeInCar == cargoType)
                return;

            //We need either no cargo or the same cargo - if it's different, we need to remove it first
            if (logicCar.CurrentCargoTypeInCar != CargoType.None && logicCar.CurrentCargoTypeInCar != cargoType)
                logicCar.DumpCargo();

            //We have the correct cargo, but not the right amount, calculate the delta
            if (logicCar.CurrentCargoTypeInCar == cargoType)
                cargoAmount -= logicCar.LoadedCargoAmount;

            if (cargoAmount > 0)
            {
                logicCar.LoadCargo(cargoAmount, cargoType, warehouseMachine);
            }

            networkedTrainCar.TrainCar.CargoDamage.LoadCargoDamageState(packet.CargoHealth);
        }
        else
        {
            LogDebug(() => $"OnClientboundCargoStatePacket() Unloading cargo: {cargoType} into {networkedTrainCar.CurrentID}, current amount: {packet.CargoAmount}");

            //Check correct cargo is loaded and the amount is correct
            if (logicCar.LoadedCargoAmount == cargoAmount && logicCar.CurrentCargoTypeInCar == cargoType)
                return;

            //If there is different cargo we need to remove it, then load the appropriate amount
            if (logicCar.CurrentCargoTypeInCar == CargoType.None || logicCar.CurrentCargoTypeInCar != cargoType)
            {
                //avoid triggering the load event by backdooring it
                logicCar.LastUnloadedCargoType = logicCar.CurrentCargoTypeInCar;
                logicCar.CurrentCargoTypeInCar = cargoType;
                logicCar.LoadedCargoAmount = cargoAmount;
            }

            //We have the correct cargo, calculate the delta
            if (logicCar.CurrentCargoTypeInCar == cargoType)
                cargoAmount = logicCar.LoadedCargoAmount - cargoAmount;

            if (cargoAmount > 0)
                logicCar.UnloadCargo(cargoAmount, cargoType, warehouseMachine);
        }
    }

    private void OnClientboundCargoHealthUpdatePacket(ClientboundCargoHealthUpdatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        CargoDamageModel cargoDamageModel = networkedTrainCar.TrainCar.CargoDamage;

        if (networkedTrainCar.TrainCar == null || cargoDamageModel == null)
            return;

        float deltaHealth = cargoDamageModel.currentHealth - packet.CargoHealth;

        //LogDebug(() => $"OnClientboundCargoHealthUpdatePacket() {networkedTrainCar.CurrentID}, current health: {cargoDamageModel.currentHealth}, new health: {packet.CargoHealth}, delta: {cargoDamageModel}, applySensitivity: {packet.CargoHealth > 0}");

        if (deltaHealth > 0)
            cargoDamageModel.ApplyDamageToCargo(deltaHealth, packet.CargoHealth > 0);
    }

    private void OnClientboundCarHealthUpdatePacket(ClientboundCarHealthUpdatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;

        packet.Health.LoadTo(trainCar);
    }

    private void OnClientboundRerailTrainPacket(ClientboundRerailTrainPacket packet)
    {
        LogDebug(() => $"OnClientboundRerailTrainPacket() NetId: {packet.NetId}, TrackId: {packet.TrackId}, Position: {packet.Position}, Forward: {packet.Forward}, currentMove: {WorldMover.currentMove}");
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (!NetworkedRailTrack.TryGet(packet.TrackId, out NetworkedRailTrack networkedRailTrack))
            return;
        TrainCar trainCar = networkedTrainCar.TrainCar;

        Log($"Rerailing [{trainCar?.ID}, {packet.NetId}] to track {networkedRailTrack?.RailTrack?.LogicTrack()?.ID}");
        LogDebug(() => $"Rerailing [{trainCar?.ID}, {packet.NetId}] track: [{networkedRailTrack?.RailTrack?.LogicTrack()?.ID}, {packet.TrackId}], raw position: {packet.Position}, adjusted position: {packet.Position + WorldMover.currentMove}, forward: {packet.Forward}");
        networkedTrainCar.Client()?.RerailFromHost(networkedRailTrack.RailTrack, packet.Position + WorldMover.currentMove, packet.Forward);
    }

    private void OnClientboundMoveTrainPacket(ClientboundMoveTrainPacket packet)
    {
        LogDebug(() => $"OnClientboundMoveTrainPacket() received for netId: {packet.NetId}, trackId: {packet.TrackId}, position: {packet.Position}, forward: {packet.Forward}, isTeleporting: {packet.IsTeleporting}");
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (!NetworkedRailTrack.TryGet(packet.TrackId, out NetworkedRailTrack networkedRailTrack))
            return;
        TrainCar trainCar = networkedTrainCar.TrainCar;

        Log($"Moving [{trainCar?.ID}, {packet.NetId}] to track {networkedRailTrack?.RailTrack?.LogicTrack()?.ID}");
        LogDebug(() => $"Moving [{trainCar?.ID}, {packet.NetId}] track: [{networkedRailTrack?.RailTrack?.LogicTrack()?.ID}, {packet.TrackId}], raw position: {packet.Position}, adjusted position: {packet.Position + WorldMover.currentMove}, forward: {packet.Forward}, front coupled: {trainCar.frontCoupler.coupledTo != null}, rear coupled:{trainCar.rearCoupler.coupledTo != null}, derailed: {trainCar.derailed}");

        if (!packet.IsTeleporting)
            networkedTrainCar.Client()?.MoveToTrackFromHost(networkedRailTrack.RailTrack, packet.Position + WorldMover.currentMove, packet.Forward);
        else
            LogDebug(() => $"OnClientboundMoveTrainPacket() netId: {packet.NetId} Attempting to move train, teleport not implemented");
    }

    private void OnClientboundWindowsBrokenPacket(ClientboundWindowsBrokenPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;
        DamageController damageController = trainCar.GetComponent<DamageController>();
        if (damageController == null)
            return;
        WindowsBreakingController windowsController = damageController.windows;
        if (windowsController == null)
            return;
        windowsController.BreakWindowsFromCollision(packet.ForceDirection);
    }

    private void OnClientboundWindowsRepairedPacket(ClientboundWindowsRepairedPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;
        DamageController damageController = trainCar.GetComponent<DamageController>();
        if (damageController == null)
            return;
        WindowsBreakingController windowsController = damageController.windows;
        if (windowsController == null)
            return;
        windowsController.RepairWindows();
    }

    private void OnCommonPaintThemePacket(CommonPaintThemePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
            return;

        if (!PaintThemeLookup.Instance.TryGet(packet.PaintThemeId, out PaintTheme paint) || paint == null)
        {
            LogWarning($"Received paint theme change for {netTrainCar?.CurrentID}, but paint theme id '{packet.PaintThemeId}' does not exist.");
            return;
        }

        Log($"Received paint theme change for {netTrainCar?.CurrentID}, theme '{paint.AssetName}'");

        LogDebug(() => $"OnCommonPaintThemePacket() [{netTrainCar?.CurrentID}, {packet.NetId}], area: {packet.TargetArea}, paint: [{paint?.AssetName}, {packet.PaintThemeId}]");
        netTrainCar?.Common_ReceivePaintThemeUpdate(packet.TargetArea, paint);
    }

    private void OnClientboundRestorationStateChangePacket(ClientboundRestorationStateChangePacket packet)
    {
        LogDebug(() => $"OnClientboundRestorationStateChangePacket() NetId: {packet.NetId}, NewState: {packet.NewState}, TransportCarNetIds: [{string.Join(", ", packet?.TransportCarNetIds)}]");

        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
        {
            LogWarning($"Received restoration state change for netId: {packet.NetId}, but TrainCar was not found.");
            return;
        }

        Log($"Received restoration state change for {trainCar?.ID}");

        var controller = LocoRestorationController.GetForTrainCar(trainCar);
        if (controller == null)
        {
            LogWarning($"Received restoration state change for {trainCar?.ID}, but LocoRestorationController was not found.");
            return;
        }

        switch (packet.NewState)
        {
            case LocoRestorationController.RestorationState.S5_PartOrdered:
                controller.orderPartsModule.SetUnitsToBuy(0f);
                controller.OnPartsOrdered();
                break;

            case LocoRestorationController.RestorationState.S6_PartPickedUp:
                if (packet.TransportCarNetIds == null || packet.TransportCarNetIds.Length == 0)
                {
                    LogError($"Received restoration state change for {trainCar?.ID}, but transport cars are empty");
                    return;
                }

                List<Car> transportCars = [];
                foreach (var netId in packet.TransportCarNetIds)
                {
                    if (!NetworkedTrainCar.TryGet(netId, out Car transportCar) || transportCar == null)
                    {
                        LogError($"Received restoration state change for {trainCar?.ID}, but failed to find transport car with netId {netId}");
                        continue;
                    }
                    transportCars.Add(transportCar);
                }
                controller.OnOrderedPartLoadedCargo(transportCars);
                break;

            case LocoRestorationController.RestorationState.S7_PartDelivered:
                if (controller.transportingCars != null && controller.transportingCars.Count > 0)
                {
                    foreach (var transportCar in controller.transportingCars)
                    {
                        trainCar.preventFastTravelWithCar = false;
                        trainCar.preventDelete = false;
                    }
                }
                controller.transportingCars = null;
                controller.SetState(packet.NewState);
                controller.installPartsModule.AddThingToCart();
                break;

            case LocoRestorationController.RestorationState.S8_PartInstalled:
                controller.installPartsModule.SetUnitsToBuy(0f);
                controller.OnInstallPartsPaid();
                break;

            case LocoRestorationController.RestorationState.S9_LocoServiced:
                controller.OnServiceDone();
                break;

            case LocoRestorationController.RestorationState.S10_PaintJobDone:
                controller.OnPaintJobChanged();
                break;
        }
    }

    public void SendCouplerInteraction(CouplerInteractionType flags, Coupler coupler, Coupler otherCoupler = null)
    {
        ushort couplerNetId = coupler?.train?.GetNetId() ?? 0;
        ushort otherCouplerNetId = otherCoupler?.train?.GetNetId() ?? 0;
        bool couplerIsFront = coupler?.isFrontCoupler ?? false;
        bool otherCouplerIsFront = otherCoupler?.isFrontCoupler ?? false;

        if (couplerNetId == 0)
        {
            LogWarning($"SendCouplerInteraction failed. Coupler: {coupler.name} {couplerNetId}");
            return;
        }

        LogDebug(() => $"SendCouplerInteraction([{flags}], {coupler?.train?.ID}, {otherCoupler?.train?.ID}) coupler isFront: {couplerIsFront}, otherCoupler isFront: {otherCouplerIsFront}");

        if (coupler == null)
            return;

        if (flags.HasFlag(CouplerInteractionType.CoupleViaUI))
        {
            SendTrainTopologyCommand(TrainTopologyCommandKind.Couple, coupler, otherCoupler,
                cascadeHose: flags.HasFlag(CouplerInteractionType.HoseConnect),
                cascadeMu: false, playAudio: true);
            return;
        }
        if (flags.HasFlag(CouplerInteractionType.UncoupleViaUI))
        {
            SendTrainTopologyCommand(TrainTopologyCommandKind.Uncouple, coupler, null,
                cascadeHose: flags.HasFlag(CouplerInteractionType.HoseDisconnect),
                cascadeMu: true, playAudio: true);
            return;
        }
        if (flags.HasFlag(CouplerInteractionType.CouplerCouple) && otherCoupler != null)
        {
            SendTrainTopologyCommand(TrainTopologyCommandKind.Couple, coupler, otherCoupler,
                cascadeHose: false, cascadeMu: false, playAudio: false);
            return;
        }
        if (flags.HasFlag(CouplerInteractionType.CouplerLoosen))
        {
            SendTrainTopologyCommand(TrainTopologyCommandKind.SetChainState, coupler, null,
                false, false, false, chainState: TrainChainState.AttachedLoose);
            return;
        }
        if (flags.HasFlag(CouplerInteractionType.CouplerTighten))
        {
            SendTrainTopologyCommand(TrainTopologyCommandKind.SetChainState, coupler, null,
                false, false, false, chainState: TrainChainState.AttachedTight);
            return;
        }

        // The host's chain has already moved in the world it shares with the server. Sending it on
        // made the server apply it to that coupler a second time: a Start (the screw used in an
        // unexpected state) locked the host's own screw button and left the coupler marked in use.
        if (NetworkLifecycle.Instance.IsHost())
            return;

        Log($"Sending coupler interaction [{flags}] for {coupler?.train?.ID}, {(couplerIsFront ? "Front" : "Rear")}");

        SendPacketToServer(new CommonCouplerInteractionPacket
        {
            NetId = couplerNetId,
            IsFrontCoupler = couplerIsFront,
            OtherNetId = otherCouplerNetId,
            IsFrontOtherCoupler = otherCouplerIsFront,
            Flags = (ushort)flags,
        }, DeliveryMethod.ReliableOrdered);
    }


    public void SendHoseConnected(Coupler coupler, Coupler otherCoupler, bool playAudio)
    {
        if (coupler == null || otherCoupler == null)
        {
            LogWarning($"Failed to send HoseConnected, {(coupler == null ? "Coupler is null" : coupler?.train?.ID)}, {(otherCoupler == null ? "Other Coupler is null" : otherCoupler?.train?.ID)}");
            return;
        }

        ushort couplerNetId = coupler.train.GetNetId();
        ushort otherCouplerNetId = otherCoupler.train.GetNetId();

        if (couplerNetId == 0 || otherCouplerNetId == 0)
        {
            LogWarning($"SendHoseConnected failed. Coupler: {coupler?.train?.ID} {couplerNetId}, OtherCoupler: {otherCoupler?.train?.ID} {otherCouplerNetId}");
            return;
        }

        SendTrainTopologyCommand(TrainTopologyCommandKind.ConnectHose, coupler, otherCoupler,
            false, false, playAudio);
    }

    public void SendHoseDisconnected(Coupler coupler, bool playAudio)
    {
        ushort couplerNetId = coupler.train.GetNetId();

        if (couplerNetId == 0)
        {
            LogWarning($"SendHoseDisconnected failed. Coupler: {coupler.name} {couplerNetId}");
            return;
        }

        LogDebug(() => $"SendHoseDisconnected({coupler.train.ID}, {coupler.isFrontCoupler}, {playAudio})");

        SendTrainTopologyCommand(TrainTopologyCommandKind.DisconnectHose, coupler, null,
            false, false, playAudio);
    }

    public void SendMuConnected(MultipleUnitCable cable, MultipleUnitCable otherCable, bool playAudio)
    {
        ushort cableNetId = cable.muModule.train.GetNetId();
        ushort otherCableNetId = otherCable.muModule.train.GetNetId();

        if (cableNetId == 0 || otherCableNetId == 0)
        {
            LogWarning($"SendMuConnected failed. Cable: {cable.muModule.train.name} {cableNetId}, OtherCable: {otherCable.muModule.train.name} {otherCableNetId}");
            return;
        }

        Coupler coupler = cable.isFront ? cable.muModule.train.frontCoupler : cable.muModule.train.rearCoupler;
        Coupler otherCoupler = otherCable.isFront ? otherCable.muModule.train.frontCoupler : otherCable.muModule.train.rearCoupler;
        SendTrainTopologyCommand(TrainTopologyCommandKind.ConnectMultipleUnit, coupler,
            otherCoupler, false, false, playAudio);
    }

    public void SendMuDisconnected(ushort netId, MultipleUnitCable cable, bool playAudio)
    {

        Coupler coupler = cable.isFront ? cable.muModule.train.frontCoupler : cable.muModule.train.rearCoupler;
        SendTrainTopologyCommand(TrainTopologyCommandKind.DisconnectMultipleUnit, coupler,
            null, false, false, playAudio);
    }

    public void SendCockState(ushort netId, Coupler coupler, bool isOpen)
    {
        SendTrainTopologyCommand(TrainTopologyCommandKind.SetCock, coupler, null,
            false, false, false, requestedValue: isOpen);
    }

    private void OnClientboundTrainControlCommitPacket(ClientboundTrainControlCommitPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (packet.Result != TrainControlCommitResult.Accepted)
            LogWarning($"Train control operation {packet.OperationId} for car {packet.NetId} " +
                       $"was rejected: {packet.Result}; authoritative revision {packet.Revision}");
        networkedTrainCar.Client()?.ReceiveControlCommit(packet);
        DebugRuntime.Publish("train", "train.control-commit-applied", DebugRuntimeSide.Client,
            entityType: "TrainCar", entityId: packet.NetId.ToString(),
            data: DebugValueSnapshotter.SnapshotObject(new
            {
                packet.NetId,
                packet.Revision,
                packet.OperationId,
                result = packet.Result.ToString(),
                portCount = packet.PortIds?.Length ?? 0,
                fuseCount = packet.FuseIds?.Length ?? 0,
                packet.IncludesHandbrake,
                packet.BrakeCylinderReleased
            }));
    }

    public IDisposable BeginRemoteTopologyCommands()
    {
        remoteTopologyDepth++;
        return new ScopeExit(() => remoteTopologyDepth--);
    }

    private void SendTrainTopologyCommand(TrainTopologyCommandKind kind, Coupler primary,
        Coupler secondary, bool cascadeHose, bool cascadeMu, bool playAudio,
        bool requestedValue = false, TrainChainState chainState = TrainChainState.Parked)
    {
        ushort primaryNetId = primary?.train?.GetNetId() ?? 0;
        ushort secondaryNetId = secondary?.train?.GetNetId() ?? 0;
        if (primaryNetId == 0)
        {
            LogWarning($"Cannot send topology {kind}: primary endpoint has no NetId");
            return;
        }
        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.Server?.CommitObservedTrainTopology();
            return;
        }
        string signature = $"{(byte)kind}:{primaryNetId}:{primary?.isFrontCoupler == true}:" +
                           $"{secondaryNetId}:{secondary?.isFrontCoupler == true}:" +
                           $"{cascadeHose}:{cascadeMu}:{requestedValue}:{(byte)chainState}";
        // Some native chain transitions raise the same completion callback twice in one
        // frame. Sending two distinct operations makes the second command stale after the
        // first advances the topology revision and can visibly undo a successful action.
        if (lastTrainTopologyCommandFrame == Time.frameCount &&
            string.Equals(lastTrainTopologyCommandSignature, signature,
                StringComparison.Ordinal))
        {
            LogDebug(() => $"Coalesced duplicate topology command {signature}");
            return;
        }
        lastTrainTopologyCommandFrame = Time.frameCount;
        lastTrainTopologyCommandSignature = signature;
        ulong sequence = ++nextTrainTopologyOperationId;
        if (sequence == 0)
            sequence = ++nextTrainTopologyOperationId;
        SendPacketToServer(new ServerboundTrainTopologyCommandPacket
        {
            OperationId = ((ulong)PlayerId << 56) | (sequence & 0x00FFFFFFFFFFFFFFUL),
            ExpectedRevision = trainTopology.Revision,
            Kind = kind,
            PrimaryCarNetId = primaryNetId,
            PrimaryEnd = primary.isFrontCoupler ? TrainEnd.Front : TrainEnd.Rear,
            HasSecondary = secondaryNetId != 0,
            SecondaryCarNetId = secondaryNetId,
            SecondaryEnd = secondary?.isFrontCoupler == true ? TrainEnd.Front : TrainEnd.Rear,
            RequestedValue = requestedValue,
            RequestedChainState = chainState,
            CascadeHose = cascadeHose,
            CascadeMultipleUnit = cascadeMu,
            PlayAudio = playAudio,
            ViaRemote = remoteTopologyDepth > 0
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendChainPlacement(Coupler coupler, TrainChainState finalState,
        bool detachMechanical)
    {
        SendTrainTopologyCommand(detachMechanical
                ? TrainTopologyCommandKind.Uncouple
                : TrainTopologyCommandKind.SetChainState,
            coupler, null, cascadeHose: false, cascadeMu: false, playAudio: false,
            chainState: finalState);
    }

    private void RequestTrainTopologySync()
    {
        // The host's topology is the canonical one: there is nothing to sync it with.
        if (NetworkLifecycle.Instance.IsHost())
            return;
        // A single topology failure can defer every car's brake heartbeat. Coalesce their
        // identical repair requests instead of turning one native mismatch into hundreds
        // of reliable packets and repeated snapshots per second.
        if (Time.realtimeSinceStartup < nextTrainTopologySyncRequestAt)
            return;
        nextTrainTopologySyncRequestAt = Time.realtimeSinceStartup + 1f;
        SendPacketToServer(new ServerboundTrainTopologySyncRequestPacket
        {
            KnownBaselineId = trainTopology.BaselineId,
            KnownTopologyRevision = trainTopology.Revision
        }, DeliveryMethod.ReliableOrdered);
    }

    internal string DescribeHostCoupling(ushort carNetId, TrainEnd end) =>
        trainTopology.DescribeEndpoint(carNetId, end);

    internal void RestoreCanonicalTrainTopology()
    {
        // On the host its own couplings are the canonical ones; this client holds no snapshot of them.
        if (NetworkLifecycle.Instance.IsHost())
            return;
        if (trainTopology.ReapplyCurrent(out string failure))
            return;

        LogWarning("Failed to restore canonical topology after local physics: " + failure);
        RequestTrainTopologySync();
        if (trainTopology.HasPending)
            StartTrainTopologyRetry();
    }

    public void SendBrakeCylinderReleased(ushort netId)
    {
        uint revision = NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar car)
            ? car.ControlRevision : 0;
        SendPacketToServer(new CommonBrakeCylinderReleasePacket
        {
            OperationId = NextTrainControlOperationId(),
            ExpectedRevision = revision,
            NetId = netId
        }, DeliveryMethod.ReliableOrdered);
    }

    public ulong SendHandbrakePositionChanged(ushort netId, float position, bool viaRemote = false)
    {
        uint revision = NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar car)
            ? car.ControlRevision : 0;
        ulong operationId = NextTrainControlOperationId();
        SendPacketToServer(new CommonHandbrakePositionPacket
        {
            OperationId = operationId,
            ExpectedRevision = revision,
            NetId = netId,
            Position = Mathf.Clamp01(position),
            ViaRemote = viaRemote
        }, DeliveryMethod.ReliableOrdered);
        return operationId;
    }

    public void SendAddCoal(ushort netId, float coalMassDelta)
    {
        SendPacketToServer(new ServerboundAddCoalPacket
        {
            NetId = netId,
            CoalMassDelta = coalMassDelta
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendTenderCoalPileInteraction(ushort netId, float coalMassDelta)
    {
        SendPacketToServer(new ServerboundTenderCoalPacket
        {
            NetId = netId,
            CoalMassDelta = coalMassDelta
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendFireboxIgnition(ushort netId)
    {
        SendPacketToServer(new ServerboundFireboxIgnitePacket
        {
            NetId = netId,
        }, DeliveryMethod.ReliableOrdered);
    }

    public ulong SendPorts(ushort netId, uint[] portIds, float[] portValues)
    {
        uint revision = NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar car)
            ? car.ControlRevision : 0;
        ulong operationId = NextTrainControlOperationId();
        SendPacketToServer(new CommonTrainPortsPacket
        {
            OperationId = operationId,
            ExpectedRevision = revision,
            NetId = netId,
            PortIds = portIds,
            PortValues = portValues,
            Tick = NetworkLifecycle.Instance.Tick
        }, DeliveryMethod.ReliableOrdered);

        /*
        string log=$"Sending ports netId: {netId}";
        for (int i = 0; i < portIds.Length; i++) {
            log += $"\r\n\t{portIds[i]}: {portValues[i]}";
        }

        LogDebug(() => log);
        */
        return operationId;
    }

    public ulong SendFuses(ushort netId, uint[] fuseIds, bool[] fuseValues)
    {
        uint revision = NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar car)
            ? car.ControlRevision : 0;
        ulong operationId = NextTrainControlOperationId();
        SendPacketToServer(new CommonTrainFusesPacket
        {
            OperationId = operationId,
            ExpectedRevision = revision,
            NetId = netId,
            FuseIds = fuseIds,
            FuseValues = fuseValues
        }, DeliveryMethod.ReliableOrdered);
        return operationId;
    }

    /// <summary>True when a train control operation id was issued by this client.</summary>
    public bool IsOwnTrainControlOperation(ulong operationId) =>
        operationId != 0 && (byte)(operationId >> 56) == PlayerId;

    private ulong NextTrainControlOperationId()
    {
        ulong sequence = ++nextTrainControlOperationId;
        if (sequence == 0)
            sequence = ++nextTrainControlOperationId;
        return ((ulong)PlayerId << 56) | (sequence & 0x00FFFFFFFFFFFFFFUL);
    }

    public void SendSandboxCargo(ushort carNetId, uint cargoTypeNetId)
    {
        SendPacketToServer(new ServerboundSandboxCargoPacket { CarNetId = carNetId, CargoTypeNetId = cargoTypeNetId },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendSandboxCarDamage(ushort carNetId, byte damageLevel, bool derail)
    {
        SendPacketToServer(new ServerboundSandboxCarDamagePacket { CarNetId = carNetId, DamageLevel = damageLevel, Derail = derail },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendCargoIgnite(ushort carNetId, byte index, float strength, bool explode)
    {
        SendPacketToServer(new ServerboundCargoIgnitePacket { CarNetId = carNetId, Index = index, Strength = strength, Explode = explode },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendTrainSyncRequest(ushort netId)
    {
        // The host has the car's state already; asking only made it resend the car to everyone.
        if (NetworkLifecycle.Instance.IsHost())
            return;
        SendPacketToServer(new ServerboundTrainSyncRequestPacket
        {
            NetId = netId
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendTrainDeleteRequest(ushort netId)
    {
        SendPacketToServer(new ServerboundTrainDeleteRequestPacket
        {
            NetId = netId
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendTrainRerailRequest(ushort netId, ushort trackId, Vector3 position, Vector3 forward)
    {
        SendPacketToServer(new ServerboundTrainRerailRequestPacket
        {
            NetId = netId,
            TrackId = trackId,
            Position = position,
            Forward = forward
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendTrainSpawnRequest(string liveryId, ushort trackNetId, int position, bool withTrackDirection)
    {
        SendPacketToServer(new ServerboundTrainSpawnRequestPacket
        {
            LiveryId = liveryId,
            TrackNetId = trackNetId,
            Index = position,
            WithTrackDirection = withTrackDirection
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendWorkTrainRequest(uint ticketId, string liveryId, ushort trackNetId, int position, bool withTrackDirection)
    {
        SendPacketToServer(new ServerboundWorkTrainRequestPacket
        {
            TicketId = ticketId,
            LiveryId = liveryId,
            TrackNetId = trackNetId,
            Index = position,
            WithTrackDirection = withTrackDirection
        }, DeliveryMethod.ReliableUnordered);
    }

    private void OnClientboundTrainsetRelocationPacket(ClientboundTrainsetRelocationPacket packet)
    {
        if (packet == null || packet.RootNetId == 0 || packet.Cars == null) return;
        if (trainsetRelocationRevisions.TryGetValue(packet.RootNetId, out uint applied) &&
            packet.Revision <= applied)
        {
            LogDebug(() => $"Ignoring stale trainset relocation {packet.OperationId} " +
                $"revision {packet.Revision}, applied {applied}");
            SendTrainsetRelocationAck(packet, 1, "stale-ignored", applied);
            return;
        }
        if (pendingTrainsetRelocationRevisions.TryGetValue(packet.RootNetId, out uint pending) &&
            packet.Revision <= pending)
        {
            SendTrainsetRelocationAck(packet, 1, "pending-newer-or-equal", pending);
            return;
        }
        pendingTrainsetRelocationRevisions[packet.RootNetId] = packet.Revision;
        SingletonBehaviour<CoroutineManager>.Instance.Run(ApplyTrainsetRelocation(packet));
    }

    private IEnumerator ApplyTrainsetRelocation(ClientboundTrainsetRelocationPacket packet)
    {
        float deadline = Time.realtimeSinceStartup + 10f;
        string failure;
        while (!NetworkedCarSpawner.TryApplyRelocation(packet.Cars, packet.HostTick, out failure))
        {
            if (!pendingTrainsetRelocationRevisions.TryGetValue(packet.RootNetId,
                    out uint pending) || pending != packet.Revision)
            {
                SendTrainsetRelocationAck(packet, 1, "superseded-before-apply", pending);
                yield break;
            }
            bool dependency = failure.StartsWith("missing-car:", StringComparison.Ordinal) ||
                failure.StartsWith("missing-track:", StringComparison.Ordinal);
            if (!dependency || Time.realtimeSinceStartup >= deadline)
            {
                LogError($"Trainset relocation {packet.OperationId} could not be applied: {failure}");
                SendTrainsetRelocationAck(packet, 2,
                    dependency ? "deferred-dependency-timeout:" + failure : failure, 0);
                if (pendingTrainsetRelocationRevisions.TryGetValue(packet.RootNetId,
                        out uint current) && current == packet.Revision)
                    pendingTrainsetRelocationRevisions.Remove(packet.RootNetId);
                yield break;
            }
            yield return null;
        }
        trainsetRelocationRevisions[packet.RootNetId] = packet.Revision;
        pendingTrainsetRelocationRevisions.Remove(packet.RootNetId);
        uint appliedHash = TrainsetRelocationHash.Compute(packet.Cars);
        DebugRuntime.Publish("train-relocation", "train-relocation.client-applied",
            DebugRuntimeSide.Client, entityId: "train:" + packet.RootNetId,
            correlationId: packet.OperationId, data: new()
            {
                ["revision"] = packet.Revision, ["carCount"] = packet.Cars.Length,
                ["hostTick"] = packet.HostTick
            });
        if (packet.CommittedHash != 0 && packet.CommittedHash != appliedHash)
            SendTrainsetRelocationAck(packet, 3, "hash-mismatch", appliedHash);
        else
            SendTrainsetRelocationAck(packet, 0, "applied", appliedHash);
    }

    private void SendTrainsetRelocationAck(ClientboundTrainsetRelocationPacket packet,
        byte status, string reason, uint hash)
    {
        SendPacketToServer(new ServerboundTrainsetRelocationAckPacket
        {
            OperationId = packet.OperationId, Revision = packet.Revision,
            RootNetId = packet.RootNetId, Status = status,
            ReasonCode = reason ?? string.Empty, AppliedHash = hash
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendPaintThemeChange(NetworkedTrainCar netTraincar, TrainCarPaint.Target targetArea, uint themeId)
    {
        Log($"Sending paint theme change for {netTraincar.CurrentID}");

        SendPacketToServer(new CommonPaintThemePacket { NetId = netTraincar.NetId, TargetArea = targetArea, PaintThemeId = themeId }, DeliveryMethod.ReliableUnordered);
    }

    public void SendTrainControlAuthorityRequest(ushort netId, uint portNetId, bool requestAuthority)
    {
        SendPacketToServer
        (
            new ServerboundTrainControlAuthorityPacket
            {
                NetId = netId,
                PortNetId = portNetId,
                RequestAuthority = requestAuthority
            },
            DeliveryMethod.ReliableOrdered
        );
    }
}
