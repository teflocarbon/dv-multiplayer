using DV.MultipleUnit;
using HarmonyLib;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Trains;

/// <summary>
/// The in-cab decoupler gadget couples and uncouples natively and had no patch: on a client the
/// mechanical change stayed local while its side effects (hoses, cocks) reached the host as separate
/// commands, so the host closed the cocks between cars that stayed coupled, or connected hoses
/// between cars that never coupled. Batch vanilla's change and send what actually changed. The
/// coupler it acts on can be several cars from the cab, so it is sent like a remote action.
/// </summary>
[HarmonyPatch(typeof(DecouplerDeviceLogic))]
public static class DecouplerDeviceLogicPatch
{
    public sealed class Before
    {
        public IDisposable Batch;
        public readonly Dictionary<Coupler, (Coupler CoupledTo, bool Hose)> Couplers = new();
    }

    private static Before Capture(IEnumerable<Coupler> couplers)
    {
        Before before = new() { Batch = NetworkedTrainCar.BeginLocalTopologyBatch() };
        foreach (Coupler coupler in couplers.Where(c => c != null).Distinct())
            before.Couplers[coupler] = (coupler.coupledTo, coupler.GetAirHoseConnectedTo() != null);
        return before;
    }

    [HarmonyPatch("Couple")]
    [HarmonyPrefix]
    private static void BeforeCouple(DecouplerDeviceLogic __instance, out Before __state) =>
        __state = Capture(new[] { __instance.frontCouplerInRange, __instance.rearCouplerInRange });

    [HarmonyPatch("Uncouple")]
    [HarmonyPrefix]
    private static void BeforeUncouple(DecouplerDeviceLogic __instance, out Before __state)
    {
        // Mirrors vanilla's choice of coupler.
        int selected = __instance.selectedCoupler;
        Coupler nth = selected > 0
            ? CouplerLogic.GetNthCouplerFrom(__instance.car.frontCoupler, selected - 1)
            : selected < 0 ? CouplerLogic.GetNthCouplerFrom(__instance.car.rearCoupler, -selected - 1) : null;
        __state = Capture(new[] { nth });
    }

    [HarmonyPatch("Couple")]
    [HarmonyPostfix]
    private static void AfterCouple(Before __state) => Report(__state);

    [HarmonyPatch("Uncouple")]
    [HarmonyPostfix]
    private static void AfterUncouple(Before __state) => Report(__state);

    [HarmonyPatch("Couple")]
    [HarmonyFinalizer]
    private static Exception CoupleFinalizer(Exception __exception, Before __state)
    {
        __state?.Batch?.Dispose();
        return __exception;
    }

    [HarmonyPatch("Uncouple")]
    [HarmonyFinalizer]
    private static Exception UncoupleFinalizer(Exception __exception, Before __state)
    {
        __state?.Batch?.Dispose();
        return __exception;
    }

    private static void Report(Before before)
    {
        before?.Batch?.Dispose();
        var client = NetworkClient.Instance;
        if (client == null || before == null)
            return;

        using IDisposable remote = client.BeginRemoteTopologyCommands();
        HashSet<TrainCar> touched = new();
        foreach (KeyValuePair<Coupler, (Coupler CoupledTo, bool Hose)> entry in before.Couplers)
        {
            Coupler coupler = entry.Key;
            if (coupler == null || coupler.coupledTo == entry.Value.CoupledTo)
                continue;
            touched.Add(coupler.train);
            if (entry.Value.CoupledTo != null)
                touched.Add(entry.Value.CoupledTo.train);

            if (coupler.coupledTo != null)
            {
                Coupler other = coupler.coupledTo;
                touched.Add(other.train);
                bool hose = coupler.GetAirHoseConnectedTo() == other;
                client.SendCouplerInteraction(CouplerInteractionType.CoupleViaUI |
                    (hose ? CouplerInteractionType.HoseConnect | CouplerInteractionType.CockOpen : 0), coupler, other);
                if (coupler.state == ChainCouplerInteraction.State.Attached_Tight)
                    client.SendCouplerInteraction(CouplerInteractionType.CouplerTighten, coupler, null);
                if (TrainTopologyNativeAdapter.TryGetMuCable(coupler, out MultipleUnitCable cable) && cable.IsConnected &&
                    TrainTopologyNativeAdapter.TryGetMuCable(other, out MultipleUnitCable otherCable))
                    client.SendMuConnected(cable, otherCable, true);
            }
            else
            {
                bool hoseDropped = entry.Value.Hose && coupler.GetAirHoseConnectedTo() == null;
                client.SendCouplerInteraction(CouplerInteractionType.UncoupleViaUI |
                    (hoseDropped ? CouplerInteractionType.HoseDisconnect | CouplerInteractionType.CockClose : 0),
                    coupler, null);
            }
        }

        // Auto-handbrake from the coupling, if any, made by the same remote-style action.
        foreach (TrainCar car in touched.Where(car => car?.trainset?.cars != null).SelectMany(car => car.trainset.cars).Distinct().ToList())
            if (car.TryNetworked(out NetworkedTrainCar networked))
                networked.Client_FlushHandbrakeViaRemote();
    }
}
