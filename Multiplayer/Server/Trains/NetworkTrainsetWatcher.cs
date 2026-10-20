using System.Collections.Generic;
using System.Linq;
using DV.Utils;
using UnityEngine;
using JetBrains.Annotations;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Server.Session;

namespace Multiplayer.Server.Trains;

/// <summary>
/// The host's trainsets' motion, sampled every tick and sent to the players. Clients follow it
/// (Client/Trains/TrainsetPhysicsFollower.cs).
/// </summary>
public class NetworkTrainsetWatcher : SingletonBehaviour<NetworkTrainsetWatcher>
{
    private ClientboundTrainsetPhysicsPacket cachedSendPacket;

    // Sets that were moving on the previous tick, to catch the tick a set stops.
    private HashSet<Trainset> movingSets = new();
    private HashSet<Trainset> movingSetsNext = new();

    const float DESIRED_FULL_SYNC_INTERVAL = 2f; // in seconds
    const int MAX_UNSYNC_TICKS = (int)(NetworkLifecycle.TICK_RATE * DESIRED_FULL_SYNC_INTERVAL);

    protected override void Awake()
    {
        base.Awake();
        cachedSendPacket = new ClientboundTrainsetPhysicsPacket();
        // This component can survive a host -> menu -> remote-client transition.
        // Keep one subscription for its lifetime and decide whether to do server work
        // from the live lifecycle role on each tick.
        NetworkLifecycle.Instance.OnTick += Server_OnTick;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (UnloadWatcher.isQuitting)
            return;
        if (NetworkLifecycle.Instance != null)
            NetworkLifecycle.Instance.OnTick -= Server_OnTick;
    }

    #region Server

    // How fast the host's simulation is running against the clock (game time per real second).
    // A host that drops to a couple of frames a second clamps its frame time, so its world runs
    // at a fraction of real time. Clients predict along the track in real time with the speed we
    // send, so sending the raw speed made them run far ahead of the host (hundreds of metres over
    // a long stall) and snap back when it recovered. Send the speed the train is really making.
    //
    // The rate is game time over real time per frame (Time.deltaTime, capped by DV at 1/12 s,
    // against Time.unscaledDeltaTime, which isn't), averaged over about a second. It used to be
    // measured per tick against Time.realtimeSinceStartup, read part-way through a frame, and
    // eased only over two ticks: every ordinary 120 ms hitch read as a rate of ~0.6 for a tick,
    // so the speed sent dipped by a third and climbed back over the next four ticks. Clients took
    // the dip as an impact and followed it at once, so their trains' speed swung by 5-15 km/h
    // every second while the host's held steady.
    private const float SimulationRateSeconds = 1f;
    private float rateGameTime;
    private float rateRealTime;
    private float simulationRate = 1f;

    private void Update()
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        rateGameTime += Time.deltaTime;
        rateRealTime += Time.unscaledDeltaTime;
    }

    private void UpdateSimulationRate()
    {
        if (rateRealTime <= 0f)
            return;
        float rate = Mathf.Clamp01(rateGameTime / rateRealTime);
        simulationRate = Mathf.Lerp(simulationRate, rate, 1f - Mathf.Exp(-rateRealTime / SimulationRateSeconds));
        rateGameTime = rateRealTime = 0f;
    }

    private void Server_OnTick(uint tick)
    {
        // IsHost() was only checked in Awake previously. If a player hosted a game,
        // returned to the menu, then joined somebody else, this stale watcher kept
        // trying to publish physics through a null server every tick.
        if (!NetworkLifecycle.Instance.IsHost() || NetworkServer.Instance == null)
            return;

        UpdateSimulationRate();
        cachedSendPacket.Tick = tick;
        foreach (Trainset set in Trainset.allSets)
        {
            if (UnloadWatcher.isUnloading || UnloadWatcher.isQuitting)
                return;

            if (set != null && set.cars != null)
                Server_TickSet(set, tick);
            else
                Multiplayer.LogWarning($"Server_OnTick(): Trainset or cars are null. Set Id: {set?.id}, Cars: {set?.cars?.Count}");
        }

        (movingSets, movingSetsNext) = (movingSetsNext, movingSets);
        movingSetsNext.Clear();
    }

    private void Server_TickSet(Trainset set, uint tick)
    {
        bool anyCarMoving = false;
        bool anyCarTeleporting = false;
        bool maxTicksReached = false;
        bool anyTracksDirty = false;

        if (UnloadWatcher.isUnloading || UnloadWatcher.isQuitting)
            return;

        if (set == null)
        {
            Multiplayer.LogWarning("Server_TickSet() called with null Trainset!");
            return;
        }

        if (set.firstCar == null || set.lastCar == null)
        {
            Multiplayer.LogWarning($"Trainset {set?.id} has null end cars! firstCar: {set?.firstCar != null}, lastCar: {set?.lastCar != null}");
            return;
        }
        cachedSendPacket.FirstNetId = set.firstCar.GetNetId();
        cachedSendPacket.LastNetId = set.lastCar.GetNetId();

        // Car may not be initialised, missing a valid NetID
        if (cachedSendPacket.FirstNetId == 0 || cachedSendPacket.LastNetId == 0)
            return;

        foreach (TrainCar trainCar in set.cars)
        {
            if (trainCar == null || trainCar.gameObject == null || !trainCar.gameObject.activeSelf)
            {
                Multiplayer.LogError($"Trainset {set?.id} ({set.firstCar?.GetNetId()}) has a null or inactive car! trainCar: {trainCar != null}, gameObject: {trainCar?.gameObject != null}, active: {trainCar?.gameObject?.activeSelf}");
                return;
            }

            // Check if Bogies array is valid before proceeding
            if (trainCar.Bogies == null || trainCar.Bogies.Length < 2)
            {
                Multiplayer.LogError($"TrainCar {trainCar?.ID} in set {set?.id} Bogies array are null: {trainCar.Bogies == null}, Length: {trainCar.Bogies?.Length}");
                return;
            }

            if (trainCar.Bogies[0] == null || trainCar.Bogies[1] == null)
            {
                Multiplayer.LogError($"TrainCar {trainCar?.ID} in set {set?.id} is missing Bogies! Bogie[0] is null: {trainCar.Bogies[0] == null}, Bogie[1] is null: {trainCar.Bogies[1] == null}");
                return;
            }

            // If we can locate the networked car, we'll add to the ticks counter and check if any tracks are dirty
            if (NetworkedTrainCar.TryGetFromTrainCar(trainCar, out NetworkedTrainCar netTC) && netTC != null)
            {
                maxTicksReached |= netTC.TicksSinceSync >= MAX_UNSYNC_TICKS; //Even if the car is stationary, if the max ticks has been exceeded we will still sync
                anyTracksDirty |= netTC.BogieTracksDirty;
            }
            else
            {
                Multiplayer.LogError($"NetworkedTrainCar not found for TrainCar {trainCar?.ID} in set {set?.id} ({set.firstCar?.GetNetId()})");
                return;
            }
            
            if (trainCar.derailed)
            {
                if (trainCar?.rb == null)
                {
                    Multiplayer.LogError($"Rigid body not found for TrainCar {trainCar?.ID} in set {set?.id} ({set.firstCar?.GetNetId()})");
                    return;
                }

                // A derailed car never quite stops (handbrakes don't act on derailed bogies), so
                // "moving" is whether it has moved 2 cm in the last second: otherwise its endless
                // slow slide was streamed forever and clients kept chasing it.
                if (!netTC.Server().DerailedSettled(trainCar.rb.position, tick))
                    anyCarMoving = true;
            }
            else if (!trainCar.isStationary)
                anyCarMoving = true;

            // Preserve an earlier car's teleport state while scanning the whole set.
            // Assignment here allowed a later non-teleporting car to clear the flag and
            // publish a partially relocated trainset as ordinary physics.
            anyCarTeleporting |= trainCar.IsTeleporting;
            if (anyCarTeleporting)
                Multiplayer.LogDebug(() => $"Server_TickSet() {trainCar?.ID} in set {set.id} is teleporting");
            // Every car is checked (no early exit): a dirty track on a later car must still make
            // the packet reliable, and a set stopping must not be mistaken for one due a resync.
        }

        // The tick a set stops, send where it stopped, reliably. Clients hold a stopped car at
        // the host's last sample; with only the unreliable periodic resync after this, a lost
        // packet or a client frame stall right at the stop left their car metres off.
        bool justStopped = !anyCarMoving && movingSets.Contains(set);
        if (anyCarMoving)
            movingSetsNext.Add(set);
        maxTicksReached |= justStopped;

        // If any car is dirty or exceeded its max ticks we will re-sync the entire train
        if (!anyCarMoving && !maxTicksReached || anyCarTeleporting)
            return;

        TrainsetMovementPart[] trainsetParts = new TrainsetMovementPart[set.cars.Count];
        
        for (int i = 0; i < set.cars.Count; i++)
        {
            TrainCar trainCar = set.cars[i];
            if (!trainCar.TryNetworked(out NetworkedTrainCar networkedTrainCar))
            {
                Multiplayer.LogDebug(() => $"TrainCar {trainCar?.ID} is not networked! Is active? {trainCar?.gameObject?.activeInHierarchy}");
                continue;
            }

            if (trainCar.derailed)
            {
                trainsetParts[i] = new TrainsetMovementPart(networkedTrainCar.NetId, RigidbodySnapshot.From(trainCar.rb));
            }
            else
            {
                Vector3? position = null;
                Quaternion? rotation = null;

                // Have we exceeded the max ticks?
                if (maxTicksReached)
                {
                    // The body's pose is the simulated one (the transform is interpolated), but a
                    // car only just spawned hasn't had a physics step yet: its body is still
                    // where the pool parked it, 2 km under the map. A new car syncs on its first
                    // tick, and clients snapped the car body there while its bogies stayed on the
                    // rails, flinging it across the world (shaking, falling through the ground).
                    bool bodyCaughtUp = (trainCar.rb.position - trainCar.transform.position).sqrMagnitude < 25f;
                    position = (bodyCaughtUp ? trainCar.rb.position : trainCar.transform.position) - WorldMover.currentMove;
                    rotation = bodyCaughtUp ? trainCar.rb.rotation : trainCar.transform.rotation;

                    networkedTrainCar.TicksSinceSync = 0;
                }

                trainsetParts[i] = new TrainsetMovementPart(
                    networkedTrainCar.NetId,
                    trainCar.GetForwardSpeed() * simulationRate,
                    trainCar.stress.slowBuildUpStress,
                    BogieData.FromBogie(trainCar.Bogies[0]),
                    BogieData.FromBogie(trainCar.Bogies[1]),
                    position,   //only used in full sync
                    rotation    //only used in full sync
                );
            }

            //reset this car's states
            networkedTrainCar.BogieTracksDirty = false;
        }

        cachedSendPacket.TrainsetParts = trainsetParts;
        NetworkServer.Instance.SendTrainsetPhysicsUpdate(cachedSendPacket, anyTracksDirty || justStopped);
    }
    #endregion

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkTrainsetWatcher)}]";
    }
}
