using HarmonyLib;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Trains;

[HarmonyPatch(typeof(ChainCouplerInteraction))]
public static class ChainCouplerInteractionPatch
{
    [HarmonyPatch(nameof(ChainCouplerInteraction.OnScrewButtonUsed))]
    [HarmonyPostfix]
    private static void OnScrewButtonUsed(ChainCouplerInteraction __instance)
    {
        // A client's player's screw. The host's is in the server's world, which observes the chain
        // settling (NetworkedTrainCarServer.ChainStateChanged).
        if (!NetworkRole.IsRemoteClient)
            return;

        Multiplayer.LogDebug(() => $"OnScrewButtonUsed({__instance?.couplerAdapter?.coupler?.train?.ID}) state: {__instance.state}");

        CouplerInteractionType flag = CouplerInteractionType.Start;
        if (__instance.state == ChainCouplerInteraction.State.Attached_Tightening_Couple || __instance.state == ChainCouplerInteraction.State.Attached_Tight)
            flag = CouplerInteractionType.CouplerTighten;
        else if (__instance.state == ChainCouplerInteraction.State.Attached_Loosening_Uncouple || __instance.state == ChainCouplerInteraction.State.Attached_Loose)
            flag = CouplerInteractionType.CouplerLoosen;
        else
            Multiplayer.LogDebug(() =>
            {
                TrainCar car = __instance?.couplerAdapter?.coupler?.train;
                return $"OnScrewButtonUsed({car?.ID})\r\n{new System.Diagnostics.StackTrace()}";
            });

        if (flag != CouplerInteractionType.NoAction)
            NetworkClient.Instance?.SendCouplerInteraction(flag, __instance?.couplerAdapter?.coupler);
    }

}
