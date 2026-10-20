using DV.MultipleUnit;
using DV.RemoteControls;
using HarmonyLib;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Multiplayer.Client.Session;


namespace Multiplayer.Client.Items;

/// <summary>
/// Remote-control couple/uncouple. Vanilla runs the coupling locally; these patches batch it (so
/// the hose, cock and MU side effects aren't sent on their own ahead of the coupling) and then send
/// what actually changed as host-authoritative topology commands, like the coupling menu does.
/// This replaced a legacy replay that ran the coupling up to three times (locally, on the host with
/// the host's handbrake-bypass input, and on every other client) and left a coupler interaction
/// lock set that was never cleared, blocking every other player's remote on that coupler.
/// </summary>
[HarmonyPatch(typeof(RemoteControllerModule))]
public static class RemoteControllerModulePatch
{
    public sealed class Before
    {
        public IDisposable Batch;
        public Dictionary<Coupler, Coupler> CoupledTo = new();
        public HashSet<TrainCar> Cars = new();
    }

    // The ends of the paired loco's consist: the only couplers a remote couple can join.
    private static IEnumerable<Coupler> ConsistEnds(TrainCar car)
    {
        List<TrainCar> cars = car?.trainset?.cars;
        if (cars == null || cars.Count == 0)
            return car == null ? Array.Empty<Coupler>() : new[] { car.frontCoupler, car.rearCoupler };
        return cars.SelectMany(c => new[] { c.frontCoupler, c.rearCoupler }).Where(c => c != null);
    }

    [HarmonyPatch(nameof(RemoteControllerModule.RemoteControllerCouple))]
    [HarmonyPrefix]
    static void BeforeCouple(RemoteControllerModule __instance, out Before __state)
    {
        __state = new Before { Batch = NetworkedTrainCar.BeginLocalTopologyBatch() };
        if (__instance.car?.trainset?.cars != null)
            __state.Cars.UnionWith(__instance.car.trainset.cars);
        foreach (Coupler coupler in ConsistEnds(__instance.car))
            __state.CoupledTo[coupler] = coupler.coupledTo;
    }

    [HarmonyPatch(nameof(RemoteControllerModule.RemoteControllerCouple))]
    [HarmonyPostfix]
    static void RemoteControllerCouple(Before __state)
    {
        __state?.Batch?.Dispose();
        var client = NetworkClient.Instance;
        if (client == null || __state == null)
            return;

        using IDisposable remote = client.BeginRemoteTopologyCommands();
        foreach (KeyValuePair<Coupler, Coupler> entry in __state.CoupledTo)
        {
            Coupler coupler = entry.Key;
            if (coupler == null || coupler.coupledTo == null || coupler.coupledTo == entry.Value)
                continue;
            Coupler other = coupler.coupledTo;
            // Vanilla's remote couple is a full couple (hoses and cocks) plus MU cables.
            client.SendCouplerInteraction(CouplerInteractionType.CoupleViaUI | CouplerInteractionType.HoseConnect |
                                          CouplerInteractionType.CockOpen, coupler, other);
            if (TrainTopologyNativeAdapter.TryGetMuCable(coupler, out MultipleUnitCable cable) && cable.IsConnected &&
                TrainTopologyNativeAdapter.TryGetMuCable(other, out MultipleUnitCable otherCable))
                client.SendMuConnected(cable, otherCable, true);
            break; // one coupling per press
        }
        FlushRemoteHandbrakes(__state);
    }

    [HarmonyPatch(nameof(RemoteControllerModule.Uncouple))]
    [HarmonyPrefix]
    static void BeforeUncouple(RemoteControllerModule __instance, int selectedCoupler, out Before __state)
    {
        __state = new Before { Batch = NetworkedTrainCar.BeginLocalTopologyBatch() };
        if (__instance.car?.trainset?.cars != null)
            __state.Cars.UnionWith(__instance.car.trainset.cars);
        TrainCar startCar = __instance.car;
        if (startCar == null)
        {
            Multiplayer.LogWarning("Trying to Uncouple from Remote with no paired loco");
            return;
        }
        Coupler nth = CouplerLogic.GetNthCouplerFrom(selectedCoupler > 0 ? startCar.frontCoupler : startCar.rearCoupler,
            Mathf.Abs(selectedCoupler) - 1);
        if (nth != null)
            __state.CoupledTo[nth] = nth.coupledTo;
    }

    [HarmonyPatch(nameof(RemoteControllerModule.Uncouple))]
    [HarmonyPostfix]
    static void Uncouple(Before __state)
    {
        __state?.Batch?.Dispose();
        var client = NetworkClient.Instance;
        if (client == null || __state == null)
            return;

        using IDisposable remote = client.BeginRemoteTopologyCommands();
        foreach (KeyValuePair<Coupler, Coupler> entry in __state.CoupledTo)
        {
            // Vanilla's remote uncouple is a full uncouple (hoses, cocks and MU cables too).
            if (entry.Key != null && entry.Value != null && entry.Key.coupledTo == null)
                client.SendCouplerInteraction(CouplerInteractionType.UncoupleViaUI |
                                              CouplerInteractionType.HoseDisconnect |
                                              CouplerInteractionType.CockClose, entry.Key, null);
        }
        FlushRemoteHandbrakes(__state);
    }

    // Vanilla sets handbrakes on the cars it couples or cuts when auto-handbrake applies. Send those
    // now, marked as remote, or the host rejects them (the player can be far from the cars).
    private static void FlushRemoteHandbrakes(Before state)
    {
        HashSet<TrainCar> cars = new(state.Cars.Where(car => car != null));
        foreach (TrainCar car in state.Cars.Where(car => car?.trainset?.cars != null).ToList())
            cars.UnionWith(car.trainset.cars);
        foreach (Coupler coupler in state.CoupledTo.Keys.Where(c => c?.train?.trainset?.cars != null))
            cars.UnionWith(coupler.train.trainset.cars);
        foreach (TrainCar car in cars)
            if (car.TryNetworked(out NetworkedTrainCar networked))
                networked.Client_FlushHandbrakeViaRemote();
    }

    [HarmonyPatch(nameof(RemoteControllerModule.RemoteControllerCouple))]
    [HarmonyFinalizer]
    static Exception CoupleFinalizer(Exception __exception, Before __state)
    {
        __state?.Batch?.Dispose();
        return __exception;
    }

    [HarmonyPatch(nameof(RemoteControllerModule.Uncouple))]
    [HarmonyFinalizer]
    static Exception UncoupleFinalizer(Exception __exception, Before __state)
    {
        __state?.Batch?.Dispose();
        return __exception;
    }
}
