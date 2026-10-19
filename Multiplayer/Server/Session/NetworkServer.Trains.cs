using DV;
using DV.Customization.Paint;
using DV.Garages;
using DV.InventorySystem;
using DV.LocoRestoration;
using DV.Logic.Job;
using DV.ThingTypes;
using LiteNetLib;
using LiteNetLib.Utils;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.RPCs;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Common.Train;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Server.Trains;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Transport;
using Multiplayer.Shared.Utils;
using Multiplayer.Shared.World;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Trains;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Multiplayer.Server.Session;

// NetworkServer's trains handlers and sends (see docs/design/code-layout.md).
public partial class NetworkServer : ITrainServer
{
    // The checks every player's control change passes (Core/Trains/ControlCommitEnvelope.cs).
    private readonly ControlCommitEnvelope trainControlEnvelope = new();
    private ulong hostControlOperationId;

    partial void RegisterLocalTrains()
    {
        // Lever authority is ordered with the control commits it guards (ReliableOrdered on
        // loopback). The host's commits are made directly (CommitHostControls), so a release,
        // queued after flushing its last commit, still follows it; and a grant needs no order
        // with them, since the host's commits take no lease.
        HandleLocal<ServerboundTrainControlAuthorityPacket>(HandleTrainControlAuthority);
        // The comms radio's car tools.
        HandleLocal<ServerboundTrainDeleteRequestPacket>(HandleTrainDelete);
        HandleLocal<ServerboundTrainRerailRequestPacket>(HandleTrainRerail);
        HandleLocal<ServerboundWorkTrainRequestPacket>(HandleLocalWorkTrain);
    }

    private void HoldForTrainBaseline(ServerPlayer player, NetDataWriter writer, DeliveryMethod deliveryMethod,
        PlayerLoadingState minimumLoadState)
    {
        if (!player.TrainBaselinePending || minimumLoadState != PlayerLoadingState.ReadyForTrainSets ||
            deliveryMethod is DeliveryMethod.Unreliable or DeliveryMethod.Sequenced)
            return;
        if (player.PendingTrainTraffic.Count >= MaxHeldTrainPackets)
        {
            if (player.PendingTrainTraffic.Count == MaxHeldTrainPackets)
                LogWarning($"Holding too much rolling-stock traffic for {player.Username}; dropping further packets");
            return;
        }
        player.PendingTrainTraffic.Add((writer.CopyData(), deliveryMethod));
    }

    private void FlushHeldTrainTraffic(ServerPlayer player)
    {
        if (!player.TrainBaselinePending)
            return;
        player.TrainBaselinePending = false;
        LogDebug(() => $"Delivering {player.PendingTrainTraffic.Count} rolling-stock packets held during {player.Username}'s baseline");
        foreach ((byte[] data, DeliveryMethod delivery) in player.PendingTrainTraffic)
            player.Peer?.Send(NetDataWriter.FromBytes(data, false), delivery);
        player.PendingTrainTraffic.Clear();
    }

    public void SendSpawnTrainset(List<TrainCar> set, bool autoCouple, bool sendToAll, ITransportPeer sendTo = null)
    {

        LogDebug(() =>
        {
            StringBuilder sb = new();

            sb.Append($"SendSpawnTrainSet() Sending trainset {set?.FirstOrDefault()?.GetNetId()} with {set?.Count} cars");

            TrainCar[] noNetId = set?.Where(car => car.GetNetId() == 0).ToArray();

            if (noNetId.Length > 0)
                sb.AppendLine($"Erroneous cars!: {string.Join(", ", noNetId.Select(car => $"{{{car?.ID}, {car?.CarGUID}, {car.logicCar != null}}}"))}");

            return sb.ToString();

        });

        var packet = ClientboundSpawnTrainSetPacket.FromTrainSet(set, autoCouple);

        if (!sendToAll)
        {
            if (sendTo == null)
                LogError($"SendSpawnTrainSet() Trying to send to null peer!");
            else
                SendPacket(sendTo, packet, DeliveryMethod.ReliableOrdered);
        }
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);

        ClientboundTrainTopologySnapshotPacket topology = trainTopologyAuthority.CommitObserved();
        if (sendToAll)
        {
            SendPacketToAll(topology, DeliveryMethod.ReliableOrdered,
                PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
            lastBroadcastTrainTopologyRevision = topology.TopologyRevision;
        }
        else if (sendTo != null)
            SendPacket(sendTo, topology, DeliveryMethod.ReliableOrdered);
    }

    public void SendDestroyTrainCar(NetworkedTrainCar netTrainCar, ChangeCause<CarRemovalReason> cause,
        ITransportPeer peer = null)
    {
        //ushort netID = trainCar.GetNetId();
        Log($"Sending DestroyTrainCarPacket for [{netTrainCar.CurrentID} {netTrainCar.NetId}]");

        if (netTrainCar.NetId == 0)
        {
            LogWarning($"SendDestroyTrainCar failed. [{netTrainCar.CurrentID} {netTrainCar.NetId}]");
            return;
        }

        var packet = new ClientboundDestroyTrainCarPacket { NetId = netTrainCar.NetId, Cause = cause };

        if (peer == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
        else
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendDerailCause(NetworkedTrainCar netTrainCar, ChangeCause<DerailReason> cause)
    {
        if (netTrainCar == null || netTrainCar.NetId == 0)
            return;
        SendPacketToAll(new ClientboundTrainDerailCausePacket { NetId = netTrainCar.NetId, Cause = cause },
            DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public ITrainCarServerPart CreateCarPart(NetworkedTrainCar car) => new NetworkedTrainCarServer(car);

    public void NoteUncouple(Coupler coupler, ChangeCause<UncoupleReason> cause)
    {
        ushort netId = coupler?.train?.GetNetId() ?? 0;
        if (netId == 0 || pendingUncoupleCauses.Count >= MaxPendingUncoupleCauses)
            return;
        pendingUncoupleCauses.Add(new TrainUncoupleCauseData
        {
            CarNetId = netId,
            End = coupler.isFrontCoupler ? TrainEnd.Front : TrainEnd.Rear,
            Cause = cause
        });
    }

    private void AttachUncoupleCauses(ClientboundTrainTopologySnapshotPacket snapshot)
    {
        snapshot.Uncouples = pendingUncoupleCauses.ToArray();
        pendingUncoupleCauses.Clear();
    }

    // A trainset sample's recipients this tick, with each one's interval (reused).
    private readonly List<(ITransportPeer peer, byte interval)> trainsetRecipients = new();

    /// <summary>
    /// Sends a trainset's motion to the players. <paramref name="intervalFor"/> gives each player's
    /// interval (ticks until their next sample, written into the packet), or 0 when they aren't
    /// due one this tick. Reliable samples for a player still taking the train baseline are held,
    /// as every train broadcast's are.
    /// </summary>
    public void SendTrainsetPhysicsUpdate(ClientboundTrainsetPhysicsPacket packet, bool reliable, Func<ServerPlayer, byte> intervalFor)
    {
        //LogDebug(() => $"Sending Physics packet for netId: {packet.FirstNetId}, tick: {packet.Tick}");
        DeliveryMethod deliveryMethod = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
        trainsetRecipients.Clear();
        foreach (ITransportPeer peer in peers.Values)
        {
            if (peer == null || peer == SelfPeer)
                continue;
            byte interval = 1;
            if (TryGetServerPlayer(peer, out ServerPlayer player))
            {
                if (player.LoadingState < PlayerLoadingState.ReadyForTrainSets)
                {
                    if (reliable)
                    {
                        packet.IntervalTicks = 1;
                        HoldForTrainBaseline(player, WritePacket(packet), deliveryMethod, PlayerLoadingState.ReadyForTrainSets);
                    }
                    continue;
                }
                interval = intervalFor(player);
                if (interval == 0)
                    continue;
            }
            trainsetRecipients.Add((peer, interval));
        }

        // Written once per interval in use: players at the same distance band share a packet.
        while (trainsetRecipients.Count > 0)
        {
            byte interval = trainsetRecipients[trainsetRecipients.Count - 1].interval;
            packet.IntervalTicks = interval;
            NetDataWriter writer = WritePacket(packet);
            for (int i = trainsetRecipients.Count - 1; i >= 0; i--)
            {
                if (trainsetRecipients[i].interval != interval)
                    continue;
                ITransportPeer peer = trainsetRecipients[i].peer;
                DebugTrace.PacketSending(peer, writer, deliveryMethod, DebugRuntimeSide.Server, nameof(ClientboundTrainsetPhysicsPacket));
                peer.Send(writer, deliveryMethod);
                trainsetRecipients.RemoveAt(i);
            }
        }
    }

    public void SendBrakeState(ushort netId, float mainReservoirPressure, float brakePipePressure, float brakeCylinderPressure, float overheatPercent, float overheatReductionFactor, float temperature)
    {
        TrainTopologyNativeAdapter.TryCaptureBrakeLine(netId, out BrakeLineStateData line);
        SendPacketToAll(new ClientboundBrakeStateUpdatePacket
        {
            TopologyRevision = trainTopologyAuthority.Revision,
            NetId = netId,
            BrakeLineAnchorCarNetId = line?.AnchorCarNetId ?? netId,
            BrakeLineMemberDigest = line?.MemberDigest ?? 0,
            MainReservoirPressure = mainReservoirPressure,
            BrakePipePressure = line?.PipePressure ?? brakePipePressure,
            BrakeCylinderPressure = brakeCylinderPressure,
            OverheatPercent = overheatPercent,
            OverheatReductionFactor = overheatReductionFactor,
            Temperature = temperature
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);

        //LogDebug(()=> $"Sending Brake Pressures netId {netId}: {mainReservoirPressure}, {independentPipePressure}, {brakePipePressure}, {brakeCylinderPressure}");
    }

    public void SendExplosion(ushort carNetId, global::Multiplayer.Shared.Trains.ExplosionSync.Kind kind, byte index)
    {
        SendPacketToAll(new ClientboundExplosionPacket { CarNetId = carNetId, Kind = (byte)kind, Index = index },
            DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
    }

    public void SendCargoFire(ushort carNetId, byte index, global::Multiplayer.Shared.Trains.CargoFireSync.FireState state, ITransportPeer peer = null)
    {
        var packet = new ClientboundCargoFirePacket { CarNetId = carNetId, Index = index, State = (byte)state };
        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTiles, excludeSelf: true);
    }

    public void SendCargoState(NetworkedTrainCar netTraincar, bool isLoading, byte cargoModelIndex)
    {
        Car logicCar = netTraincar?.TrainCar?.logicCar;

        //LogDebug(() => $"SendCargoState({netTraincar?.CurrentID}, isLoading: {isLoading}, cargoModelIndex: {cargoModelIndex}), logicCar: {logicCar?.ID}, WareHouseMachineID: {logicCar.CargoOriginWarehouse?.ID}, warehouse track: {logicCar.CargoOriginWarehouse?.WarehouseTrack?.ID}");

        Log($"Sending Cargo State for {netTraincar?.CurrentID}, isLoading: {isLoading}, cargoModelIndex: {cargoModelIndex}");

        if (logicCar == null)
        {
            LogWarning($"Attempted to send cargo state for {netTraincar?.CurrentID}, but logic car does not exist!");
            return;
        }

        CargoType cargoTypeV1 = isLoading ? logicCar.CurrentCargoTypeInCar : logicCar.LastUnloadedCargoType;

        CargoTypeLookup.Instance.TryGetNetId(cargoTypeV1, out uint cargoType);

        ushort netMachineId = 0;
        if (logicCar.CargoOriginWarehouse != null)
        {
            if (!WarehouseMachineLookup.TryGetNetId(logicCar.CargoOriginWarehouse, out netMachineId))
            {
                Log($"Attempting to send cargo state for {netTraincar.CurrentID}, for warehouse machine at track {logicCar.CargoOriginWarehouse?.WarehouseTrack?.ID}, but Warehouse Machine was not found");
                return;
            }
        }

        SendPacketToAll(new ClientboundCargoStatePacket
        {
            NetId = netTraincar.NetId,
            IsLoading = isLoading,
            CargoTypeNetId = cargoType,
            CargoAmount = logicCar.LoadedCargoAmount,
            CargoHealth = netTraincar.TrainCar.CargoDamage.HealthPercentage,
            CargoModelIndex = cargoModelIndex,
            WarehouseMachineNetId = netMachineId,
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendPaintThemeChange(NetworkedTrainCar netTraincar, TrainCarPaint.Target targetArea, uint themeNetId, ServerPlayer sendToPlayer = null)
    {
        var packet = new CommonPaintThemePacket
        {
            NetId = netTraincar.NetId,
            TargetArea = targetArea,
            PaintThemeId = themeNetId
        };

        Log($"Sending paint theme change for {netTraincar.CurrentID}");

        if (sendToPlayer != null)
            SendPacket(sendToPlayer.Peer, packet, DeliveryMethod.ReliableUnordered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForTrainSets, true);
    }

    public void SendRestorationStateChange(ushort netId, LocoRestorationController.RestorationState newState, ushort[] transportCars)
    {
        var packet = new ClientboundRestorationStateChangePacket
        {
            NetId = netId,
            NewState = newState,
            TransportCarNetIds = transportCars
        };

        Log($"Sending restoration state change for {netId}, new state: {newState}, transport cars count: {transportCars?.Count() ?? 0}");

        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForItems, true);
    }

    public void SendCargoHealthUpdate(ushort netId, float currentHealth)
    {
        SendPacketToAll(new ClientboundCargoHealthUpdatePacket
        {
            NetId = netId,
            CargoHealth = currentHealth,
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendCarHealthUpdate(ushort netId, TrainCarHealthData health)
    {

        //LogDebug(() => $"Sending Car Health Update for netId {netId}: BodyHP: {health.BodyHP}, WheelsHP: {health.WheelsHP}, MechanicalPT: {health.MechanicalPT}, ElectricalPT: {health.ElectricalPT}, WindowsBroken: {health.WindowsBroken}");

        SendPacketToAll(new ClientboundCarHealthUpdatePacket
        {
            NetId = netId,
            Health = health
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendRerailTrainCar(ushort netId, ushort rerailTrack, Vector3 worldPos, Vector3 forward)
    {
        SendPacketToAll(new ClientboundRerailTrainPacket
        {
            NetId = netId,
            TrackId = rerailTrack,
            Position = worldPos,
            Forward = forward
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendMoveTrainCarToTrack(ushort netId, ushort destinationTrack, Vector3 worldPos, Vector3 forward, bool isTeleporting)
    {
        LogDebug(() => $"SendMoveTrainCarToTrack({netId}, {destinationTrack}, {worldPos}, {forward}, {isTeleporting})");
        SendPacketToAll
        (
            new ClientboundMoveTrainPacket
            {
                NetId = netId,
                TrackId = destinationTrack,
                Position = worldPos,
                Forward = forward,
                IsTeleporting = isTeleporting
            }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, true
        );
    }

    public void SendWindowsBroken(ushort netId, Vector3 forceDirection)
    {
        SendPacketToAll
        (
            new ClientboundWindowsBrokenPacket
            {
                NetId = netId,
                ForceDirection = forceDirection
            }, DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForTrainSets, SelfPeer
        );
    }

    public void SendWindowsRepaired(ushort netId)
    {
        SendPacketToAll
        (
            new ClientboundWindowsRepairedPacket
            {
                NetId = netId
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForTrainSets,
            excludeSelf: true
        );
    }

    public void SendTrainUncouple(Coupler coupler, bool playAudio, bool dueToBrokenCouple, bool viaChainInteraction)
    {
        ushort couplerNetId = coupler.train.GetNetId();

        if (couplerNetId == 0)
        {
            LogWarning($"SendTrainUncouple failed. Coupler: {coupler.name} {couplerNetId}");
            return;
        }

        LogDebug(() => $"SendTrainUncouple({coupler.train.ID}, {coupler.isFrontCoupler}, {dueToBrokenCouple}, {viaChainInteraction})");

        CommitObservedTrainTopology();
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

        CommitObservedTrainTopology();
    }

    public void SendCockState(ushort netId, Coupler coupler, bool isOpen)
    {
        CommitObservedTrainTopology();
    }

    public void CommitObservedTrainTopology(params ushort[] controlCars)
    {
        trainTopologyCommitPending = true;
        foreach (ushort carNetId in controlCars ?? Array.Empty<ushort>())
            if (carNetId != 0)
                pendingTrainTopologyControlCars.Add(carNetId);
    }

    public void SendTrainControlAuthorityUpdate(ushort netId, uint portNetId, ControlAuthorityState state, ServerPlayer sendToPlayer = null, ServerPlayer excludePlayer = null)
    {
        var packet = new ClientboundTrainControlAuthorityUpdatePacket
        {
            NetId = netId,
            PortNetId = portNetId,
            State = state
        };

        if (sendToPlayer == null)
            if (excludePlayer == null)
                SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, excludeSelf: false);
            else
                SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, excludePlayer.Peer, false);
        else
            SendPacket(sendToPlayer.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendTrainsetRelocation(ClientboundTrainsetRelocationPacket packet)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered,
            PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
        ClientboundTrainTopologySnapshotPacket topology = trainTopologyAuthority.CommitObserved();
        SendPacketToAll(topology, DeliveryMethod.ReliableOrdered,
            PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
        lastBroadcastTrainTopologyRevision = topology.TopologyRevision;
    }

    private void OnServerboundTrainsetRelocationAckPacket(
        ServerboundTrainsetRelocationAckPacket packet, ITransportPeer peer)
    {
        peerToPlayer.TryGetValue(peer, out ServerPlayer player);
        DebugRuntime.Publish("train-relocation", "train-relocation.ack-received",
            DebugRuntimeSide.Server,
            packet.Status == 0 || packet.Status == 1 ? DebugSeverity.Info : DebugSeverity.Error,
            entityId: "train:" + packet.RootNetId, correlationId: packet.OperationId,
            data: new()
            {
                ["revision"] = packet.Revision, ["status"] = packet.Status,
                ["reasonCode"] = packet.ReasonCode ?? string.Empty,
                ["appliedHash"] = packet.AppliedHash,
                ["playerId"] = player?.PlayerId ?? 0,
                ["playerName"] = player?.Username ?? string.Empty
            });
    }

    private void OnServerboundCargoIgnitePacket(ServerboundCargoIgnitePacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player) && NetworkedTrainCar.TryGet(packet.CarNetId, out TrainCar car))
            global::Multiplayer.Server.Trains.CargoFireServer.Ignite(car, packet.Index, packet.Strength, packet.Explode, player.WorldPosition, player.Username);
    }

    private void OnCommonCouplerInteractionPacket(CommonCouplerInteractionPacket packet, ITransportPeer peer)
    {
        if (!peerToPlayer.TryGetValue(peer, out var player))
        {
            LogWarning($"Received Coupler Interaction from {peer.GetType()}, peerId: {peer.Id}, but could not find matching player.");
            return;
        }

        //todo: add validation that to ensure the client is near the coupler - this packet may also be used for remote operations and may need to factor that in in the future
        if (NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
        {
            if (netTrainCar.Server().ValidateCouplerInteraction(packet, player))
            {
                // The listen host owns the canonical train topology. Relaying a client-local
                // coupling without applying it here lets the requesting client join two cars
                // while the host simulation continues to move them independently.
                netTrainCar.SuppressAuthoritativeTopologyObservations();
                if (packet.OtherNetId != 0 &&
                    NetworkedTrainCar.TryGet(packet.OtherNetId, out NetworkedTrainCar otherNetTrainCar))
                    otherNetTrainCar.SuppressAuthoritativeTopologyObservations();
                using (NetworkedTrainCar.BeginAuthoritativeTopologyApplication())
                    netTrainCar.Common_ReceiveCouplerInteraction(packet);

                CouplerInteractionType flags = (CouplerInteractionType)packet.Flags;
                CommitObservedTrainTopology();
                // Chain dragging/screw interaction remains transient presentation. Canonical
                // endpoint state above is the recovery/acknowledgement path.
                if (!flags.HasFlag(CouplerInteractionType.CoupleViaUI) &&
                    !flags.HasFlag(CouplerInteractionType.UncoupleViaUI))
                    SendPacketToAll(packet, DeliveryMethod.ReliableOrdered,
                        PlayerLoadingState.ReadyForTrainSets, peer, excludeSelf: true);
            }
            else
            {
                LogDebug(() => $"OnCommonCouplerInteractionPacket([{packet.Flags}, {netTrainCar.CurrentID}, {packet.NetId}], {player.PlayerId}) Sending validation failure");
                //failed validation notify client
                SendPacket
                (
                    peer,
                    new CommonCouplerInteractionPacket
                    {
                        NetId = packet.NetId,
                        Flags = (ushort)CouplerInteractionType.NoAction,
                        IsFrontCoupler = packet.IsFrontCoupler,
                    },
                    DeliveryMethod.ReliableOrdered
                );
                SendPacket(peer, trainTopologyAuthority.Snapshot(), DeliveryMethod.ReliableOrdered);
            }
        }
        else
        {
            LogWarning($"OnCommonCouplerInteractionPacket([{packet.Flags}, {packet.NetId}], " +
                $"{player.PlayerId}) rejected unknown train car");
        }
    }

    private static bool TryResolveCoupler(ushort netId, bool front, out Coupler coupler)
    {
        coupler = null;
        if (!NetworkedTrainCar.TryGet(netId, out TrainCar car) || car == null)
            return false;
        coupler = front ? car.frontCoupler : car.rearCoupler;
        return coupler != null;
    }

    private void OnCommonBrakeCylinderReleasePacket(CommonBrakeCylinderReleasePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) ||
            !NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar car))
            return;
        TrainControlCommitResult result = ValidateControlEnvelope(player, car,
            packet.ExpectedRevision, packet.OperationId, requireLease: false, null);
        if (result == TrainControlCommitResult.Accepted)
        {
            try
            {
                // The commit below carries it: the host's own release event isn't a second one.
                using (ApplyingRemoteChange())
                    car.TrainCar.brakeSystem.ReleaseBrakeCylinderPressure();
                if (!car.TrainCar.brakeSystem.controlReservoirPressureReleased)
                    result = TrainControlCommitResult.InvalidValue;
            }
            catch
            {
                result = TrainControlCommitResult.InvalidValue;
            }
        }
        ClientboundTrainControlCommitPacket commit = ControlCommit(car, packet.OperationId,
            result, brakeCylinderReleased: result == TrainControlCommitResult.Accepted);
        SendControlCommit(commit, peer);
    }

    private void OnCommonHandbrakePositionPacket(CommonHandbrakePositionPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) ||
            !NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar car))
            return;
        TrainControlCommitResult result = float.IsNaN(packet.Position) || float.IsInfinity(packet.Position)
            ? TrainControlCommitResult.InvalidValue
            : ValidateControlEnvelope(player, car, packet.ExpectedRevision,
                packet.OperationId, requireLease: false, null, skipRangeCheck: packet.ViaRemote);
        if (result == TrainControlCommitResult.Accepted)
        {
            car.TrainCar.brakeSystem.SetHandbrakePosition(Mathf.Clamp01(packet.Position));
            car.Common_OnRemoteHandbrakeChange();
        }
        ClientboundTrainControlCommitPacket commit = ControlCommit(car, packet.OperationId, result,
            includesHandbrake: true);
        SendControlCommit(commit, peer);
    }

    private void OnCommonPaintThemePacket(CommonPaintThemePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
            return;

        if (!PaintThemeLookup.Instance.TryGet(packet.PaintThemeId, out PaintTheme paint) || paint == null)
        {
            LogWarning($"Received paint theme change for {netTrainCar?.CurrentID}, but paint theme id '{packet.PaintThemeId}' does not exist.");
            return;
        }

        Log($"Received paint theme change for {netTrainCar?.CurrentID}, theme '{paint.AssetName}'");

        LogDebug(() => $"OnCommonPaintThemePacket() [{netTrainCar?.CurrentID}, {packet.NetId}], area: {packet.TargetArea}, paint: [{paint?.AssetName}, {packet.PaintThemeId}]");

        netTrainCar?.Server()?.ValidatePaintThemeChange(packet.TargetArea, paint, player);

        //SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnServerboundAddCoalPacket(ServerboundAddCoalPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        //is value valid?
        if (float.IsNaN(packet.CoalMassDelta))
            return;

        //is player close enough to add coal?
        if ((player.WorldPosition - networkedTrainCar.transform.position).sqrMagnitude <= networkedTrainCar.CarLengthSq)
            networkedTrainCar.firebox?.fireboxCoalControlPort.ExternalValueUpdate(packet.CoalMassDelta);
    }

    private void OnServerboundTenderCoalPacket(ServerboundTenderCoalPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        // is value valid?
        if (float.IsNaN(packet.CoalMassDelta))
            return;

        //is player close enough to add/remove coal?
        if ((player.WorldPosition - networkedTrainCar.transform.position).sqrMagnitude <= networkedTrainCar.CarLengthSq)
            networkedTrainCar.coalPile?.coalConsumePort.ExternalValueUpdate(packet.CoalMassDelta);
    }

    private void OnServerboundFireboxIgnitePacket(ServerboundFireboxIgnitePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        //is player close enough to ignite firebox?
        if ((player.WorldPosition - networkedTrainCar.transform.position).sqrMagnitude <= networkedTrainCar.CarLengthSq)
            networkedTrainCar.firebox?.Ignite();
    }

    private void OnCommonTrainPortsPacket(CommonTrainPortsPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        bool shapeValid = packet.PortIds != null && packet.PortValues != null &&
                          packet.PortIds.Length > 0 &&
                          packet.PortIds.Length == packet.PortValues.Length &&
                          packet.PortValues.All(value => !float.IsNaN(value)); // infinity is legitimate (e.g. DE traction motor resistance at rest)
        TrainControlCommitResult result = shapeValid
            ? ValidateControlEnvelope(player, networkedTrainCar, packet.ExpectedRevision,
                packet.OperationId, requireLease: true, packet.PortIds)
            : TrainControlCommitResult.InvalidValue;
        if (result == TrainControlCommitResult.Accepted)
        {
            if (!networkedTrainCar.Server().ValidateClientSimFlowPacket(player, packet))
                result = TrainControlCommitResult.AuthorityDenied;
            else
                networkedTrainCar.Server().ApplyPlayerPorts(player, packet);
        }
        if (!networkedTrainCar.TryCapturePortValues(packet.PortIds ?? Array.Empty<uint>(),
                out float[] acceptedValues))
            result = TrainControlCommitResult.UnknownControl;
        ClientboundTrainControlCommitPacket commit = ControlCommit(networkedTrainCar,
            packet.OperationId, result, packet.PortIds, acceptedValues);
        SendControlCommit(commit, peer);
    }

    private void OnServerboundTrainControlAuthorityPacket(ServerboundTrainControlAuthorityPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleTrainControlAuthority(packet, player);
    }

    // Taking or releasing a lever: a player's request, the host's player's too (in-process, see
    // RegisterLocalTrains), validated the same way.
    private void HandleTrainControlAuthority(ServerboundTrainControlAuthorityPacket packet, ServerPlayer player)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        networkedTrainCar.Server().ReceiveAuthorityRequest(packet.PortNetId, player, packet.RequestAuthority,
            CurrentWorldPosition(player));
    }

    // Where a player is now. The host's own player is read from its game: its position packets
    // reach the server a poll or more late, which could put it out of range of a lever it's at.
    // TODO: remove once LocalPlayer.WorldPosition is current (docs/design/host-local-session.md,
    // "Position read from the tracker"); use player.WorldPosition then.
    private Vector3 CurrentWorldPosition(ServerPlayer player) =>
        player == LocalPlayer && PlayerManager.PlayerTransform != null
            ? PlayerManager.PlayerTransform.position
            : player.WorldPosition;

    /// <summary>
    ///     The host's own simulation: its changed ports, fuses and handbrake, or a brake-cylinder
    ///     release, committed with the values it read this tick and broadcast. Not a player's
    ///     request, so there's no lease, range or operation check, and nothing to apply: they came
    ///     from the server's world. The host player's levers are part of that world; what keeps
    ///     the host off a lever someone else holds is the lease, which blocks it in the host's cab.
    /// </summary>
    public void CommitHostControls(NetworkedTrainCar car, uint[] portIds = null, float[] portValues = null,
        uint[] fuseIds = null, bool[] fuseValues = null, bool handbrake = false,
        bool brakeCylinderReleased = false)
    {
        if (car?.TrainCar == null || car.NetId == 0)
            return;
        // No player's operation (player ids start at 1), but not 0 either: clients tell a
        // committed handbrake change from a join's state by its operation.
        ulong operationId = ++hostControlOperationId & 0x00FFFFFFFFFFFFFFUL;
        if (operationId == 0)
            operationId = ++hostControlOperationId & 0x00FFFFFFFFFFFFFFUL;
        SendControlCommit(ControlCommit(car, operationId, TrainControlCommitResult.Accepted,
            portIds, portValues, fuseIds, fuseValues, includesHandbrake: handbrake,
            brakeCylinderReleased: brakeCylinderReleased), requester: null);
    }

    private void OnCommonTrainFusesPacket(CommonTrainFusesPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) ||
            !NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar car))
            return;
        bool shapeValid = packet.FuseIds != null && packet.FuseValues != null &&
                          packet.FuseIds.Length > 0 &&
                          packet.FuseIds.Length == packet.FuseValues.Length;
        TrainControlCommitResult result = shapeValid
            ? ValidateControlEnvelope(player, car, packet.ExpectedRevision,
                packet.OperationId, requireLease: false, null)
            : TrainControlCommitResult.InvalidValue;
        if (result == TrainControlCommitResult.Accepted)
            car.Common_UpdateFuses(packet);
        if (!car.TryCaptureFuseValues(packet.FuseIds ?? Array.Empty<uint>(), out bool[] acceptedValues))
            result = TrainControlCommitResult.UnknownControl;
        ClientboundTrainControlCommitPacket commit = ControlCommit(car, packet.OperationId,
            result, fuseIds: packet.FuseIds, fuseValues: acceptedValues);
        SendControlCommit(commit, peer);
    }

    private TrainControlCommitResult ValidateControlEnvelope(ServerPlayer player,
        NetworkedTrainCar car, uint expectedRevision, ulong operationId, bool requireLease,
        uint[] portIds, bool skipRangeCheck = false)
    {
        if (player == null || car?.TrainCar == null)
            return TrainControlCommitResult.UnknownControl;
        bool inRange = skipRangeCheck ||
            (player.WorldPosition - car.transform.position).sqrMagnitude <= car.CarLengthSq;
        bool mayWrite = !requireLease || car.Server().MayWritePorts(player, portIds);
        return trainControlEnvelope.Validate(player.PlayerId, car.NetId, inRange, mayWrite,
                expectedRevision, car.ControlRevision, operationId) switch
        {
            ControlEnvelopeResult.OutOfRange => TrainControlCommitResult.OutOfRange,
            ControlEnvelopeResult.AuthorityDenied => TrainControlCommitResult.AuthorityDenied,
            ControlEnvelopeResult.StaleRevision => TrainControlCommitResult.StaleRevision,
            _ => TrainControlCommitResult.Accepted,
        };
    }

    private static ClientboundTrainControlCommitPacket ControlCommit(NetworkedTrainCar car,
        ulong operationId, TrainControlCommitResult result, uint[] portIds = null,
        float[] portValues = null, uint[] fuseIds = null, bool[] fuseValues = null,
        bool includesHandbrake = false, bool brakeCylinderReleased = false)
    {
        uint revision = result == TrainControlCommitResult.Accepted
            ? car.Server().CommitControlRevision()
            : car.ControlRevision;
        return new ClientboundTrainControlCommitPacket
        {
            NetId = car.NetId,
            Revision = revision,
            OperationId = operationId,
            Result = result,
            PortIds = portIds ?? Array.Empty<uint>(),
            PortValues = portValues ?? Array.Empty<float>(),
            FuseIds = fuseIds ?? Array.Empty<uint>(),
            FuseValues = fuseValues ?? Array.Empty<bool>(),
            IncludesHandbrake = includesHandbrake,
            HandbrakePosition = car.TrainCar.brakeSystem.handbrakePosition,
            BrakeCylinderReleased = brakeCylinderReleased,
            Tick = NetworkLifecycle.Instance.Tick
        };
    }

    private void SendControlCommit(ClientboundTrainControlCommitPacket commit, ITransportPeer requester)
    {
        if (commit.Result == TrainControlCommitResult.Accepted)
            SendPacketToAll(commit, DeliveryMethod.ReliableOrdered,
                PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
        else
            SendPacket(requester, commit, DeliveryMethod.ReliableOrdered);
    }

    // The radio's sandbox tools (cargo loader, damage) are only there when the host's settings make
    // them available, as vanilla's CommsRadioController decides; and only for a car in range.
    private bool TrySandboxRadioCar(ITransportPeer peer, ushort carNetId, string tool, out ServerPlayer player, out TrainCar car)
    {
        car = null;
        if (!TryGetServerPlayer(peer, out player))
            return false;
        GameParams gameParams = Globals.G.GameParams;
        bool freeRoam = DV.UserManagement.UserManager.Instance.CurrentUser.CurrentSession.GameMode.Equals("FreeRoam");
        if (!(gameParams.CommsRadioSandboxCheatMode && freeRoam || gameParams.CommsRadioCheatMode))
        {
            LogWarning($"{player.Username} used the {tool}, but it isn't enabled");
            return false;
        }
        if (!NetworkedTrainCar.TryGet(carNetId, out car) || car == null || car.logicCar == null)
            return false;
        if ((player.WorldPosition - car.transform.position).sqrMagnitude > MaxSandboxCargoDistance * MaxSandboxCargoDistance)
        {
            LogWarning($"{player.Username} used the {tool} on {car.ID} from too far away");
            return false;
        }
        return true;
    }

    private void OnServerboundSandboxCarDamagePacket(ServerboundSandboxCarDamagePacket packet, ITransportPeer peer)
    {
        if (!TrySandboxRadioCar(peer, packet.CarNetId, "radio's damage mode", out ServerPlayer player, out TrainCar car))
            return;
        if (packet.Derail)
        {
            using (TrainChangeCauses.Scope(PlayerChangeCauses.Cause(DerailReason.CommsRadio, player)))
                car.Derail();
            return;
        }
        // What vanilla's CommsRadioDamage does, on the host's car.
        float level = Mathf.Clamp(packet.DamageLevel, 1, 10);
        Log($"{player.Username} damaged {car.ID} to level {level:F0}/10 with the comms radio");
        car.GetComponent<TrainCarCollisions>()?.CarDamaged?.Invoke(car.CarDamage.maxHealth * (level / 10f), Vector3.zero);
    }

    private void OnServerboundSandboxCargoPacket(ServerboundSandboxCargoPacket packet, ITransportPeer peer)
    {
        if (!TrySandboxRadioCar(peer, packet.CarNetId, "sandbox cargo loader", out ServerPlayer player, out TrainCar car))
            return;

        if (car.LoadedCargo != CargoType.None)
            car.logicCar.UnloadCargo(car.LoadedCargoAmount, car.LoadedCargo);
        if (packet.CargoTypeNetId == 0)
        {
            Log($"{player.Username} emptied {car.ID} with the comms radio");
            return;
        }
        if (!CargoTypeLookup.Instance.TryGet(packet.CargoTypeNetId, out CargoType_v2 cargo) || cargo == null ||
            !cargo.IsLoadableOnCarType(car.carLivery.parentType))
        {
            LogWarning($"{player.Username} tried to load cargo {packet.CargoTypeNetId} that {car.ID} can't carry");
            return;
        }
        Log($"{player.Username} loaded {car.ID} with {cargo.v1} with the comms radio");
        car.logicCar.LoadCargo(car.cargoCapacity, cargo.v1);
    }

    private void OnServerboundTrainSyncRequestPacket(ServerboundTrainSyncRequestPacket packet,
        ITransportPeer peer)
    {
        if (NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
        {
            networkedTrainCar.Server()?.DirtyAllState();
            // Never to the host's own client: it would apply an old snapshot to the world it owns.
            if (peer != SelfPeer)
                SendPacket(peer, trainTopologyAuthority.Snapshot(), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>Distance from a point to the car: the closest point on the line between its couplers.</summary>
    private static float DistanceToCar(Vector3 point, TrainCar car)
    {
        if (car.frontCoupler == null || car.rearCoupler == null)
            return Vector3.Distance(point, car.transform.position);
        Vector3 front = car.frontCoupler.transform.position, rear = car.rearCoupler.transform.position;
        Vector3 along = rear - front;
        float t = along.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector3.Dot(point - front, along) / along.sqrMagnitude) : 0f;
        return Vector3.Distance(point, front + along * t);
    }

    private void OnServerboundTrainTopologyCommandPacket(
        ServerboundTrainTopologyCommandPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) || packet == null)
            return;

        string rangeFailure = null;
        bool IsInRange()
        {
            if (NetworkLifecycle.Instance.IsHost(player))
                return true;
            if (!NetworkedTrainCar.TryGet(packet.PrimaryCarNetId,
                    out NetworkedTrainCar primary) || primary?.TrainCar == null)
            {
                rangeFailure = $"the host has no car {packet.PrimaryCarNetId}";
                return false;
            }
            // A remote controller is used from a distance; only the coupler check below applies.
            // Measured to the car itself (the line between its couplers), not its transform: some
            // cars' origins are far from their middle (an FEF-3 tender's rear coupler is 24 m from
            // it), which put a player at that coupler out of range of a centre-based check.
            float playerDistance = DistanceToCar(player.WorldPosition, primary.TrainCar);
            if (!packet.ViaRemote && playerDistance > MaxTopologyPlayerDistance)
            {
                rangeFailure = $"the host has the player {playerDistance:F1} m from " +
                    $"{primary.CurrentID} (limit {MaxTopologyPlayerDistance:F0} m" +
                    $"{(player.CarId != 0 ? $", player is on car {player.CarId}" : string.Empty)})";
                return false;
            }
            if (!packet.HasSecondary)
                return true;
            // The player acts from the primary car (often its cab, so the other car's centre can be
            // well over a car length away); what matters for the second car is that its coupler is
            // actually next to the primary's, which also stops a lagging view coupling across a gap.
            if (!NetworkedTrainCar.TryGet(packet.SecondaryCarNetId, out NetworkedTrainCar secondary) ||
                secondary?.TrainCar == null)
            {
                rangeFailure = $"the host has no car {packet.SecondaryCarNetId}";
                return false;
            }
            Coupler primaryCoupler = packet.PrimaryEnd == TrainEnd.Front
                ? primary.TrainCar.frontCoupler : primary.TrainCar.rearCoupler;
            Coupler secondaryCoupler = packet.SecondaryEnd == TrainEnd.Front
                ? secondary.TrainCar.frontCoupler : secondary.TrainCar.rearCoupler;
            if (primaryCoupler == null || secondaryCoupler == null)
            {
                rangeFailure = "a coupler is missing on the host";
                return false;
            }
            float gapSq = (primaryCoupler.transform.position - secondaryCoupler.transform.position).sqrMagnitude;
            if (gapSq <= MaxTopologyCouplerGap * MaxTopologyCouplerGap)
                return true;
            rangeFailure = $"the host has the couplers {Mathf.Sqrt(gapSq):F1} m apart " +
                $"(limit {MaxTopologyCouplerGap:F0} m), speeds {primary.TrainCar.GetForwardSpeed():F2} and " +
                $"{secondary.TrainCar.GetForwardSpeed():F2} m/s";
            return false;
        }

        ClientboundTrainTopologySnapshotPacket snapshot;
        var uncoupleCause = PlayerChangeCauses.Cause(
            packet.ViaRemote ? UncoupleReason.Remote : UncoupleReason.PlayerCommand, player,
            $"{packet.Kind} command");
        using (TrainChangeCauses.Scope(uncoupleCause))
            snapshot = trainTopologyAuthority.Execute(
                player.PlayerId, packet, runtimeAuthorized: () => true, runtimeInRange: IsInRange);
        string detail = snapshot.Result == TrainTopologyResultCode.OutOfRange
            ? rangeFailure
            : trainTopologyAuthority.LastRejectionDetail;
        LogTopologyCommand(player, packet, snapshot, detail);
        if (snapshot.Result == TrainTopologyResultCode.Accepted)
        {
            AttachUncoupleCauses(snapshot);
            SendPacketToAll(snapshot, DeliveryMethod.ReliableOrdered,
                PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
            lastBroadcastTrainTopologyRevision = snapshot.TopologyRevision;
        }
        else
        {
            // So the player's own log says why, not only the host's.
            snapshot.Detail = detail ?? string.Empty;
            SendPacket(peer, snapshot, DeliveryMethod.ReliableOrdered);
        }

        DebugRuntime.Publish("train", "train.topology-command-result", DebugRuntimeSide.Server,
            entityType: "Topology", entityId: packet.OperationId.ToString(),
            data: DebugValueSnapshotter.SnapshotObject(new
            {
                player.PlayerId,
                player.Username,
                packet.OperationId,
                packet.ExpectedRevision,
                committedRevision = snapshot.TopologyRevision,
                command = packet.Kind.ToString(),
                result = snapshot.Result.ToString(),
                packet.PrimaryCarNetId,
                primaryEnd = packet.PrimaryEnd.ToString(),
                packet.SecondaryCarNetId,
                secondaryEnd = packet.SecondaryEnd.ToString()
            }));
    }

    // Couplings come apart in the host's log either as a player's refused command (the client
    // showed it coupled until the refusal reached it) or as an uncouple, which
    // CouplerUncoupleLogPatch logs with its cause.
    private void LogTopologyCommand(ServerPlayer player, ServerboundTrainTopologyCommandPacket packet,
        ClientboundTrainTopologySnapshotPacket snapshot, string detail)
    {
        string what = $"{player.Username}'s {packet.Kind} of {DescribeEndpoint(packet.PrimaryCarNetId, packet.PrimaryEnd)}" +
            (packet.HasSecondary ? $" and {DescribeEndpoint(packet.SecondaryCarNetId, packet.SecondaryEnd)}" : string.Empty) +
            (packet.Kind == TrainTopologyCommandKind.SetChainState ? $" to {packet.RequestedChainState}" : string.Empty) +
            (packet.ViaRemote ? " via remote" : string.Empty);
        if (snapshot.Result != TrainTopologyResultCode.Accepted)
            LogWarning($"Refused {what}: {snapshot.Result}" +
                (string.IsNullOrEmpty(detail) ? string.Empty : $", {detail}") +
                $" (op {packet.OperationId & 0x00FFFFFFFFFFFFFFUL}, topology r{snapshot.TopologyRevision})");
        else if (packet.Kind == TrainTopologyCommandKind.Couple)
            Log($"Accepted {what} (topology r{snapshot.TopologyRevision})");
    }

    private static string DescribeEndpoint(ushort carNetId, TrainEnd end) =>
        NetworkedTrainCar.TryGet(carNetId, out NetworkedTrainCar car)
            ? $"{car.CurrentID} ({end})"
            : $"car {carNetId} ({end})";

    private void OnServerboundTrainTopologySyncRequestPacket(
        ServerboundTrainTopologySyncRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out _) || peer == SelfPeer)
            return;
        SendPacket(peer, trainTopologyAuthority.Snapshot(), DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundTrainDeleteRequestPacket(ServerboundTrainDeleteRequestPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleTrainDelete(packet, player);
    }

    // The comms radio's car deleter, the host's player's too (in-process).
    private void HandleTrainDelete(ServerboundTrainDeleteRequestPacket packet, ServerPlayer player)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        if (networkedTrainCar.HasPlayers)
        {
            LogWarning($"{player.Username} tried to delete a train with players in it!");
            return;
        }

        TrainCar trainCar = networkedTrainCar.TrainCar;
        float cost = trainCar.playerSpawnedCar ? 0.0f : Mathf.RoundToInt(Globals.G.GameParams.DeleteCarMaxPrice);
        if (!Inventory.Instance.RemoveMoney(cost))
        {
            LogWarning($"{player.Username} tried to delete a train without enough money to do so!");
            return;
        }

        Job job = JobsManager.Instance.GetJobOfCar(trainCar.logicCar);
        switch (job?.State)
        {
            case JobState.Available:
                job.ExpireJob();
                break;
            case JobState.InProgress:
                JobsManager.Instance.AbandonJob(job);
                break;
        }

        // Deleting a car from the middle of a train splits it; the removal and its uncouples name
        // the player (CarSpawnerPatch).
        var garageRef = trainCar.GetComponent<HomeGarageReference>();
        if (garageRef != null && garageRef.garageCarSpawner != null)
        {
            using (TrainChangeCauses.Scope(PlayerChangeCauses.Cause(CarRemovalReason.ReturnedToGarage, player)))
                garageRef.garageCarSpawner.ReturnCarHome(trainCar);
        }
        else
        {
            using (TrainChangeCauses.Scope(PlayerChangeCauses.Cause(CarRemovalReason.PlayerDeleted, player)))
                CarSpawner.Instance.DeleteCar(trainCar);
            UnusedTrainCarDeleter.Instance.ClearInvalidCarReferencesAfterManualDelete();
        }
    }

    private void OnServerboundTrainRerailRequestPacket(ServerboundTrainRerailRequestPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleTrainRerail(packet, player);
    }

    // The comms radio's rerailer, the host's player's too (in-process).
    private void HandleTrainRerail(ServerboundTrainRerailRequestPacket packet, ServerPlayer player)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (!NetworkedRailTrack.TryGet(packet.TrackId, out NetworkedRailTrack networkedRailTrack))
            return;

        TrainCar trainCar = networkedTrainCar.TrainCar;
        Vector3 position = packet.Position + WorldMover.currentMove;

        //Check if player is a Newbie (currently shared with all players)
        float cost = TutorialHelper.InRestrictedMode || rerailController != null && rerailController.isPlayerNewbie ? 0f :
            RerailController.CalculatePrice((networkedTrainCar.transform.position - position).magnitude, trainCar.carType, Globals.G.GameParams.RerailMaxPrice);

        if (!Inventory.Instance.RemoveMoney(cost))
        {
            LogWarning($"{player.Username} tried to rerail a train without enough money to do so!");
            return;
        }

        Log($"{player.Username} rerailed {networkedTrainCar.CurrentID}");
        using (TrainChangeCauses.Scope(PlayerChangeCauses.Cause(UncoupleReason.CarRerailed, player)))
            trainCar.Rerail(networkedRailTrack.RailTrack, position, packet.Forward);
    }

    private void OnServerboundTrainSpawnRequestPacket(ServerboundTrainSpawnRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedRailTrack.TryGet(packet.TrackNetId, out NetworkedRailTrack networkedRailTrack) || networkedRailTrack == null)
        {
            LogWarning($"{player.Username} tried to spawn a car on invalid track netId: {packet.TrackNetId}");
            return;
        }

        if (!global::Multiplayer.Shared.Trains.TrainComponentLookup.Instance.LiveryFromId(packet.LiveryId, out TrainCarLivery livery) || livery == null || livery.prefab == null)
        {
            LogWarning($"{player.Username} tried to spawn a car with invalid livery Id: {packet.LiveryId}");
            return;
        }

        // Check spawn location on track is valid
        var kinked = networkedRailTrack.RailTrack.GetKinkedPointSet().points;
        if (packet.Index < 0 || packet.Index >= kinked.Length)
        {
            LogWarning($"{player.Username} tried to spawn a car at an invalid index: {packet.Index}");
            return;
        }

        var spawnPoint = kinked[packet.Index];
        var startPoint = (Vector3)kinked.First().position;// + WorldMover.currentMove;
        var endpoint = (Vector3)kinked.Last().position;// + WorldMover.currentMove;

        // Check there's enough space for the car
        var carBounds = CarSpawner.GetBoundsOfCar(livery.prefab);
        if (!CarSpawner.IsThereSpaceForCarOnPoint(spawnPoint, startPoint, endpoint, carBounds.extents))
        {
            LogWarning($"{player.Username} tried to spawn a car, but there's no room on the track");
            return;
        }

        // Check player is within range of the spawn point
        float playerDistanceToSpawn = (player.AbsoluteWorldPosition - (Vector3)spawnPoint.position).magnitude;
        if (playerDistanceToSpawn > CommsRadioCarSpawner.SIGNAL_RANGE && !Mathf.Approximately(playerDistanceToSpawn, CommsRadioCarSpawner.SIGNAL_RANGE))
        {
            LogWarning($"{player.Username} tried to spawn a train {playerDistanceToSpawn:F2}m away (max: {CommsRadioCarSpawner.SIGNAL_RANGE}m)");
            return;
        }

        Vector3 forward = packet.WithTrackDirection ? spawnPoint.forward : -spawnPoint.forward;

        TrainCar spawnedCar = CarSpawner.Instance.SpawnCarFromRemote(livery.prefab, networkedRailTrack.RailTrack, (Vector3)spawnPoint.position, forward);
    }

    // Every request is answered, or the radio waits out its ticket. A refusal with no reason of its
    // own answers "in use".
    private void OnServerboundWorkTrainRequestPacket(ServerboundWorkTrainRequestPacket packet, ITransportPeer peer)
    {
        SpawnResponse.ResponseType response = TryGetServerPlayer(peer, out ServerPlayer player)
            ? HandleWorkTrain(packet, player)
            : SpawnResponse.ResponseType.InUse;
        SendRpcResponse(packet.TicketId, new SpawnResponse { Response = response }, peer);
    }

    // The host's player's answer goes back through its client, which holds the radio's ticket.
    private void HandleLocalWorkTrain(ServerboundWorkTrainRequestPacket packet, ServerPlayer player) =>
        SendRpcResponse(packet.TicketId, new SpawnResponse { Response = HandleWorkTrain(packet, player) }, player.Peer);

    // The comms radio's work-train summon. Charges only once every other check has passed.
    private SpawnResponse.ResponseType HandleWorkTrain(ServerboundWorkTrainRequestPacket packet, ServerPlayer player)
    {
        TrainCar trainCar;

        LogDebug(() => $"HandleWorkTrain() from : {player.Username}, trackNetId: {packet.TrackNetId}, liveryId: {packet.LiveryId}, index: {packet.Index}, withTrackDirection: {packet.WithTrackDirection}");

        if (!NetworkedRailTrack.TryGet(packet.TrackNetId, out NetworkedRailTrack networkedRailTrack) || networkedRailTrack == null)
        {
            LogWarning($"{player.Username} tried to request a work train on invalid track netId: {packet.TrackNetId}");
            return SpawnResponse.ResponseType.InUse;
        }

        if (!global::Multiplayer.Shared.Trains.TrainComponentLookup.Instance.LiveryFromId(packet.LiveryId, out TrainCarLivery livery) || livery == null || livery.prefab == null)
        {
            LogWarning($"{player.Username} tried to request a work train with invalid livery Id: {packet.LiveryId}");
            return SpawnResponse.ResponseType.InUse;
        }

        // Check if this is a garage loco
        bool isGarageCar = GarageCarSpawner.Spawners.TryGetValue(livery, out var selectedGarageSpawner);

        // Check funds now, but only charge once every other check has passed: failures used to
        // return after the money was taken.
        float price = isGarageCar
            ? Mathf.Min(selectedGarageSpawner.garageType.summonPrice, Globals.G.GameParams.WorkTrainSummonMaxPrice)
            : 0f;
        bool Charge()
        {
            if (price <= 0f || Inventory.Instance.RemoveMoney(price))
                return true;
            LogWarning($"{player.Username} tried to request a work train without enough money to do so!");
            return false;
        }
        if (price > 0f && Inventory.Instance.PlayerMoney < price)
        {
            LogWarning($"{player.Username} tried to request a work train without enough money to do so!");
            return SpawnResponse.ResponseType.InsufficientFunds;
        }

        trainCar = isGarageCar ? selectedGarageSpawner.GetCar(livery) : CarSpawner.Instance.AllCars.FirstOrDefault(tc => tc.carLivery == livery);

        // Check spawn location on track is valid
        var kinked = networkedRailTrack.RailTrack.GetKinkedPointSet().points;
        if (packet.Index < 0 || packet.Index >= kinked.Length)
        {
            LogWarning($"{player.Username} tried to spawn a car at an invalid index: {packet.Index}");
            return SpawnResponse.ResponseType.InUse;
        }

        var spawnPoint = kinked[packet.Index];
        var startPoint = (Vector3)kinked.First().position;
        var endpoint = (Vector3)kinked.Last().position;

        // Check there's enough space for the car
        var carBounds = CarSpawner.GetBoundsOfCar(livery.prefab);
        if (!CarSpawner.IsThereSpaceForCarOnPoint(spawnPoint, startPoint, endpoint, carBounds.extents))
        {
            LogWarning($"{player.Username} tried to spawn a car, but there's no room on the track");
            return SpawnResponse.ResponseType.InUse;
        }

        // Check player is within range of the spawn point
        float playerDistanceToSpawn = (CurrentWorldPosition(player) - WorldMover.currentMove - (Vector3)spawnPoint.position).magnitude;
        if (playerDistanceToSpawn > CommsRadioCarSpawner.SIGNAL_RANGE && !Mathf.Approximately(playerDistanceToSpawn, CommsRadioCarSpawner.SIGNAL_RANGE))
        {
            LogWarning($"{player.Username} tried to spawn a train {playerDistanceToSpawn:F2}m away (max: {CommsRadioCarSpawner.SIGNAL_RANGE}m)");
            return SpawnResponse.ResponseType.InUse;
        }

        Vector3 forward = packet.WithTrackDirection ? spawnPoint.forward : -spawnPoint.forward;

        if (trainCar == null)
        {
            LogDebug(() => $"HandleWorkTrain() {player.Username} tried to request a work train of {livery.id} but no existing car found, spawning new car");
            if (!Charge())
                return SpawnResponse.ResponseType.InsufficientFunds;
            trainCar = CarSpawner.Instance.SpawnCrewVehicle(livery, networkedRailTrack.RailTrack, (Vector3)spawnPoint.position, forward, selectedGarageSpawner);

            SendSpawnTrainset([trainCar], true, true);
        }
        else
        {
            // Check if is currently in use/recently used
            if (trainCar.IsTeleporting || !trainCar.isStationary)
            {
                LogDebug(() => $"HandleWorkTrain() {player.Username} tried to request a work train of {livery.id} but the car is in use teleporting: {trainCar.IsTeleporting}, stationary: {trainCar.isStationary}");

                return SpawnResponse.ResponseType.InUse;
            }

            bool recentlyVisited = false;
            bool attachedToJob = false;
            int ctr = 0;
            var cars = trainCar.trainset.cars;
            while (!recentlyVisited && ctr < cars.Count)
            {
                var car = cars[ctr];

                if (car.IsLoco)
                {
                    var visitChecker = car.visitChecker;
                    recentlyVisited = (visitChecker != null && visitChecker.IsRecentlyVisited);
                }
                else
                {
                    var job = JobsManager.Instance.GetJobOfCar(car.logicCar);
                    attachedToJob = (job != null && job.State == JobState.InProgress);
                }
                ctr++;
            }

            if (recentlyVisited || attachedToJob)
            {
                LogDebug(() => $"HandleWorkTrain() {player.Username} tried to request a work train of {livery.id} but the car is in use ({(recentlyVisited ? "recently visited" : "")}{(recentlyVisited && attachedToJob ? ", " : "")}{(attachedToJob ? "attached to an active job" : "")})");
                return SpawnResponse.ResponseType.InUse;
            }

            // Confirm no players are currently in the car
            if (!NetworkedTrainCar.TryGetFromTrainCar(trainCar, out var networkedTrainCar))
            {
                LogError($"HandleWorkTrain() {player.Username} tried to request a work train of {livery.id} but NetworkedTrainCar was not found for {trainCar?.ID}");
                return SpawnResponse.ResponseType.InUse;
            }

            if (networkedTrainCar.HasPlayers)
            {
                LogDebug(() => $"HandleWorkTrain() {player.Username} tried to request a work train of {livery.id} but the car is occupied");
                return SpawnResponse.ResponseType.InUse;
            }

            // Checks passed, call the work train
            if (!Charge())
                return SpawnResponse.ResponseType.InsufficientFunds;
            trainCar = CarSpawner.Instance.SpawnCrewVehicle(livery, networkedRailTrack.RailTrack, (Vector3)spawnPoint.position, forward, selectedGarageSpawner);
        }

        return SpawnResponse.ResponseType.Success;
    }
}
