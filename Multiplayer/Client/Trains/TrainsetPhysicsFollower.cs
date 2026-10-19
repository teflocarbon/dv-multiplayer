using System.Collections.Generic;
using System.Linq;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using UnityEngine;

namespace Multiplayer.Client.Trains;

/// <summary>
/// A client's trainsets following the host's motion (Server/Trains/NetworkTrainsetWatcher.cs sends it).
/// </summary>
public static class TrainsetPhysicsFollower
{
    // When the host's packets for a trainset keep disagreeing with ours about how many cars it
    // has, our couplings have drifted from the host's: two cars coupled here that the host has
    // apart get pulled apart by their corrections against the joint (#64). Restore the host's
    // topology. A coupling that's only late (the host's physics arrives before its topology
    // snapshot) clears up well within the delay.
    private const float TOPOLOGY_MISMATCH_SECONDS = 1f;
    private static readonly Dictionary<int, (float since, float seen)> setMismatches = new();
    private static float lastTopologyRestore = -100f;

    private static void NoteSetMismatch(int firstNetId, Trainset set, int hostCars)
    {
        float now = Time.realtimeSinceStartup;
        if (!setMismatches.TryGetValue(firstNetId, out var mismatch) || now - mismatch.seen > 0.5f)
            mismatch.since = now;
        mismatch.seen = now;
        setMismatches[firstNetId] = mismatch;

        var client = NetworkLifecycle.Instance.Client;
        // A deferred snapshot is already being retried.
        if (client == null || client.TrainTopologyPending || now - mismatch.since < TOPOLOGY_MISMATCH_SECONDS ||
            now - lastTopologyRestore < TOPOLOGY_MISMATCH_SECONDS)
            return;
        lastTopologyRestore = now;
        setMismatches.Remove(firstNetId);
        Multiplayer.LogWarning($"Trainset of {set.firstCar.ID} has {set.cars.Count} cars here but {hostCars} on the host " +
                               $"for {now - mismatch.since:F1} s; restoring the host's couplings");
        client.RestoreCanonicalTrainTopology();
    }

    public static void HandleTrainsetPhysicsUpdate(ClientboundTrainsetPhysicsPacket packet)
    {
        bool derailmentTransition = false;
        Trainset set = Trainset.allSets.Find(set => set.firstCar.GetNetId() == packet.FirstNetId || set.lastCar.GetNetId() == packet.FirstNetId ||
                                                    set.firstCar.GetNetId() == packet.LastNetId || set.lastCar.GetNetId() == packet.LastNetId);

        if (set == null)
        {
            Multiplayer.LogWarning($"Received {nameof(ClientboundTrainsetPhysicsPacket)} for unknown trainset with FirstNetId: {packet.FirstNetId} and LastNetId: {packet.LastNetId}");
            return;
        }

        if (set.cars.Count != packet.TrainsetParts.Length)
        {
            //log the discrepancies
            //Multiplayer.LogWarning(
            //    $"Received {nameof(ClientboundTrainsetPhysicsPacket)} for trainset with FirstNetId: {packet.FirstNetId} and LastNetId: {packet.LastNetId} with {packet.TrainsetParts.Length} parts, but trainset has {set.cars.Count} parts");

            for (int i = 0; i < packet.TrainsetParts.Length; i++)
            {
                if (NetworkedTrainCar.TryGet(packet.TrainsetParts[i].NetId ,out NetworkedTrainCar networkedTrainCar))
                {
                    //Multiplayer.LogDebug(()=>$"Applying TrainPhysicsUpdate to {packet.TrainsetParts[i].NetId}");
                    derailmentTransition |= networkedTrainCar.Client()?.ReceiveTrainPhysicsUpdate(
                        in packet.TrainsetParts[i], packet.Tick, packet.TrainsetParts.Length, packet.IntervalTicks) == true;
                }
                else
                {
                    Multiplayer.LogWarning($"Unable to apply TrainPhysicsUpdate to {packet.TrainsetParts[i].NetId}, NetworkedTrainCar not found!");
                }
            }
            if (derailmentTransition)
                NetworkLifecycle.Instance.Client?.RestoreCanonicalTrainTopology();
            else
                NoteSetMismatch(packet.FirstNetId, set, packet.TrainsetParts.Length);
            return;
        }

        //Check direction of trainset vs packet
        if(set.firstCar.GetNetId() == packet.LastNetId)
            packet.TrainsetParts = packet.TrainsetParts.Reverse().ToArray();

        //Multiplayer.Log($"Client_HandleTrainsetPhysicsUpdate({set.firstCar.ID}):, tick: {packet.Tick}");

        for (int i = 0; i < packet.TrainsetParts.Length; i++)
        {
            if(set.cars[i].TryNetworked(out NetworkedTrainCar networkedTrainCar))
                derailmentTransition |= networkedTrainCar.Client()?.ReceiveTrainPhysicsUpdate(
                    in packet.TrainsetParts[i], packet.Tick, packet.TrainsetParts.Length, packet.IntervalTicks) == true;
            else
                Multiplayer.LogWarning($"Unable to apply TrainPhysicsUpdate to TrainSet with FirstNetId: {packet.FirstNetId}, NetworkedTrainCar not found!");
        }

        // Native derailment can alter couplers as a side effect. Consume every car's
        // transition first, then restore the host's last committed topology once; doing
        // this per car can create long, explosive joints to cars not processed yet.
        if (derailmentTransition)
            NetworkLifecycle.Instance.Client?.RestoreCanonicalTrainTopology();
    }
}
