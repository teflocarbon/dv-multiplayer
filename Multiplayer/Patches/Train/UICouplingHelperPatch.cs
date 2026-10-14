using DV.HUD;
using DV.MultipleUnit;
using Multiplayer.Utils;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Managers.Client;
using System;

namespace Multiplayer.Patches.Train;


[HarmonyPatch(typeof(UICouplingHelper))]
public static class UICouplingHelperPatch
{
    // What the coupler looked like before the press. Vanilla's HandleCoupling does several things
    // at once (couple, hose, cocks, MU); the individual patches are suppressed by the batch, so the
    // postfix works out from the difference what actually changed and sends exactly that.
    public sealed class Before
    {
        public IDisposable Batch;
        public Coupler CoupledTo;
        public Coupler HoseTo;
        public bool CockOpen;
        public bool OtherCockOpen;
        public bool MuConnected;
    }

    [HarmonyPatch(nameof(UICouplingHelper.HandleCoupling))]
    [HarmonyPrefix]
    private static void BeforeHandleCoupling(Coupler coupler, out Before __state)
    {
        __state = new Before
        {
            Batch = NetworkedTrainCar.BeginLocalTopologyBatch(),
            CoupledTo = coupler?.coupledTo,
            HoseTo = coupler?.GetAirHoseConnectedTo(),
            CockOpen = coupler != null && coupler.IsCockOpen,
            OtherCockOpen = coupler?.coupledTo != null && coupler.coupledTo.IsCockOpen,
            MuConnected = IsMuConnected(coupler)
        };
    }

    [HarmonyPatch(nameof(UICouplingHelper.HandleCoupling))]
    [HarmonyPostfix]
    private static void HandleCoupling(UICouplingHelper __instance, Coupler coupler, bool advanced, Before __state)
    {
        __state?.Batch?.Dispose();
        Multiplayer.LogDebug(() => $"UICouplingHelper.HandleCoupling({coupler?.train?.ID}, {advanced})");

        var client = NetworkLifecycle.Instance?.Client;
        if (coupler == null || client == null || __state == null)
            return;

        if (coupler.IsCoupled())
        {
            Coupler otherCoupler = coupler.coupledTo;
            if (otherCoupler == null)
                return;

            /* fix for bug in vanilla game */
            coupler.SetChainTight(true);
            RefreshChainScript(coupler);
            /* end fix for bug */

            if (__state.CoupledTo == otherCoupler)
            {
                // Already coupled to this car: the press only completed the connections (an
                // advanced couple after hooking the chain). Sending a Couple here was rejected as
                // "endpoint occupied" and rolled the client's hoses back.
                SendCompletedConnections(client, coupler, otherCoupler, __state);
                return;
            }

            CouplerInteractionType interaction = CouplerInteractionType.CoupleViaUI;
            if (advanced)
                interaction |= CouplerInteractionType.HoseConnect | CouplerInteractionType.CockOpen;
            client.SendCouplerInteraction(interaction, coupler, otherCoupler);
            // The host couples via the chain path, which leaves it loose; the menu tightens it
            // (above), so tell the host, or a client's couple ends loose and the host's tight.
            client.SendCouplerInteraction(CouplerInteractionType.CouplerTighten, coupler, null);
            if (advanced && !__state.MuConnected && IsMuConnected(coupler) &&
                TrainTopologyNativeAdapter.TryGetMuCable(coupler, out MultipleUnitCable cable) &&
                TrainTopologyNativeAdapter.TryGetMuCable(otherCoupler, out MultipleUnitCable otherCable))
                client.SendMuConnected(cable, otherCable, true);
            return;
        }

        // Nothing changed (e.g. nothing in range to couple to): don't report an uncouple.
        if (__state.CoupledTo == null)
            return;

        CouplerInteractionType uncouple = CouplerInteractionType.UncoupleViaUI;
        if (advanced)
            uncouple |= CouplerInteractionType.HoseDisconnect | CouplerInteractionType.CockClose;

        /* fix for bug in vanilla game */
        coupler.state = ChainCouplerInteraction.State.Parked;
        RefreshChainScript(coupler);
        /* end fix for bug */

        client.SendCouplerInteraction(uncouple, coupler, null);
    }

    [HarmonyPatch(nameof(UICouplingHelper.HandleCoupling))]
    [HarmonyFinalizer]
    private static Exception DisposeTopologyBatch(Exception __exception, Before __state)
    {
        __state?.Batch?.Dispose();
        return __exception;
    }

    private static void SendCompletedConnections(NetworkClient client, Coupler coupler, Coupler other, Before before)
    {
        Coupler hoseTo = coupler.GetAirHoseConnectedTo();
        if (hoseTo == other && before.HoseTo != other)
            client.SendHoseConnected(coupler, other, true);
        if (coupler.IsCockOpen != before.CockOpen)
            client.SendCockState(coupler.train.GetNetId(), coupler, coupler.IsCockOpen);
        if (other.IsCockOpen != before.OtherCockOpen)
            client.SendCockState(other.train.GetNetId(), other, other.IsCockOpen);
        if (!before.MuConnected && IsMuConnected(coupler) &&
            TrainTopologyNativeAdapter.TryGetMuCable(coupler, out MultipleUnitCable cable) &&
            TrainTopologyNativeAdapter.TryGetMuCable(other, out MultipleUnitCable otherCable))
            client.SendMuConnected(cable, otherCable, true);
    }

    private static bool IsMuConnected(Coupler coupler) =>
        coupler != null && TrainTopologyNativeAdapter.TryGetMuCable(coupler, out MultipleUnitCable cable) &&
        cable.IsConnected;

    private static void RefreshChainScript(Coupler coupler)
    {
        ChainCouplerInteraction chain = coupler.ChainScript;
        if (chain == null)
            return;
        chain.enabled = false;
        chain.enabled = true;
    }
}
