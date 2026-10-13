using DV.HUD;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data.Train;
using Newtonsoft.Json.Linq;
using System;

namespace Multiplayer.Patches.Train;


[HarmonyPatch(typeof(UICouplingHelper))]
public static class UICouplingHelperPatch
{
    [HarmonyPatch(nameof(UICouplingHelper.HandleCoupling))]
    [HarmonyPrefix]
    private static void BeforeHandleCoupling(out IDisposable __state)
    {
        __state = NetworkedTrainCar.BeginLocalTopologyBatch();
    }

    [HarmonyPatch(nameof(UICouplingHelper.HandleCoupling))]
    [HarmonyPostfix]
    private static void HandleCoupling(UICouplingHelper __instance, Coupler coupler, bool advanced,
        IDisposable __state)
    {
        __state?.Dispose();
        Multiplayer.LogDebug(() => $"UICouplingHelper.HandleCoupling({coupler?.train?.ID}, {advanced})");

        if (coupler == null)
            return;

        Coupler otherCoupler = null;
        CouplerInteractionType interaction = CouplerInteractionType.NoAction;

        if (coupler.IsCoupled())
        {
            interaction |= CouplerInteractionType.CoupleViaUI;
            otherCoupler = coupler.coupledTo;

            if(advanced)
            {
                interaction |= CouplerInteractionType.HoseConnect | CouplerInteractionType.CockOpen;
            }

            Multiplayer.LogDebug(() => $"UICouplingHelper.HandleCoupling({coupler?.train?.ID}, {advanced}) coupler is front: {coupler?.isFrontCoupler}, otherCoupler: {otherCoupler?.train?.ID}, otherCoupler is front: {otherCoupler?.isFrontCoupler}, action: {interaction}");

            if (otherCoupler == null)
                return;

            /* fix for bug in vanilla game */
            coupler.SetChainTight(true);
            coupler.ChainScript.enabled = false;
            coupler.ChainScript.enabled = true;
            /* end fix for bug */
        }
        else
        {
            interaction |= CouplerInteractionType.UncoupleViaUI;

            if (advanced)
            {
                interaction |= CouplerInteractionType.HoseDisconnect | CouplerInteractionType.CockClose;
            }

            /* fix for bug in vanilla game */
            coupler.state = ChainCouplerInteraction.State.Parked;
            coupler.ChainScript.enabled = false;
            coupler.ChainScript.enabled = true;
            /* end fix for bug */
        }

        NetworkLifecycle.Instance.Client.SendCouplerInteraction(interaction, coupler, otherCoupler);
    }

    [HarmonyPatch(nameof(UICouplingHelper.HandleCoupling))]
    [HarmonyFinalizer]
    private static Exception DisposeTopologyBatch(Exception __exception, IDisposable __state)
    {
        __state?.Dispose();
        return __exception;
    }
}
