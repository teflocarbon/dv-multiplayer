using DV;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Patches.CommsRadio;

/// <summary>
/// The radio's damage mode damaged or derailed the client's copy of the car only (#74): the host,
/// which owns car health and runs the train physics, never heard of it, so a damaged hazmat car
/// never leaked or exploded for anyone, and a derailed car was put back on the rails. On a client,
/// ask the host to do it; the health and train sync then show the result to everyone.
/// </summary>
[HarmonyPatch(typeof(CommsRadioDamage), nameof(CommsRadioDamage.OnUse))]
public static class CommsRadioDamagePatch
{
    private static bool Prefix(CommsRadioDamage __instance)
    {
        if (!NetworkRole.IsRemoteClient || __instance.opMode == CommsRadioDamage.OperationMode.Activation)
            return true;
        TrainCar car = __instance.pointedCar;
        if (car == null)
            return true;  // nothing pointed at: the radio cancels
        if (!car.TryNetworked(out NetworkedTrainCar networkedCar))
            return false;

        bool derail = __instance.opMode == CommsRadioDamage.OperationMode.Derail;
        NetworkLifecycle.Instance.Client.SendSandboxCarDamage(networkedCar.NetId, (byte)__instance.damageLevel, derail);
        if (!derail && AudioManager.Instance != null && AudioManager.Instance.derailHitClip != null)
            AudioManager.Instance.derailHitClip.Play(car.transform.position, 1f, 1f, 0f, 10f, 500f, default, null, car.transform);
        CommsRadioController.PlayAudioFromRadio(__instance.confirmSound, __instance.transform);
        __instance.UpdateDisplay();
        return false;
    }
}
