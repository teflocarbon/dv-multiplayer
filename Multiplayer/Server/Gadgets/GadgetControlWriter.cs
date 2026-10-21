using DV.CabControls.Spec;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.HUD;
using DV.Simulation.Controllers;
using DV.Simulation.Ports;
using HarmonyLib;
using Multiplayer.Core.Gadgets;
using Multiplayer.Server.Trains;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Gadgets;

/// <summary>
/// The server's gadgets writing their train (gadgets.md, "Logic that writes the train"). Only the
/// server runs the gadgets that move controls (ATS, anti-slip, overheat protection; on a remote client
/// they're off, GadgetServerOnlyPatches), and what they move is committed and broadcast like any
/// server-side control change. While one of them runs, a control it moves or sets:
/// - is taken from the player holding its lease (the gadget wins), as vanilla's TryUnhandControl
///   makes its own player let go: on a player's game the lever leaves their hand;
/// - with no cab loaded (a dedicated host always), moves by the cab lever's scroll step per notch, as
///   it would with the cab loaded, instead of jumping across its whole range.
/// </summary>
[HarmonyPatch]
internal static class GadgetControlWriter
{
    // The server-run gadget whose Update is running, if any, or the control panel whose input is being
    // applied; whether what it moves is taken from its holder (a panel is refused a held lever instead).
    private static GadgetBase driving;
    private static bool takes;
    private static readonly Dictionary<(string livery, string port), float> steps = new();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetATS), "Update")]
    private static void AtsStart(GadgetATS __instance) => Start(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetAntiSlip), "Update")]
    private static void AntiSlipStart(GadgetAntiSlip __instance) => Start(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetOverheatProtection), "Update")]
    private static void OverheatStart(GadgetOverheatProtection __instance) => Start(__instance);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GadgetATS), "Update")]
    private static void AtsEnd() => driving = null;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GadgetAntiSlip), "Update")]
    private static void AntiSlipEnd() => driving = null;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GadgetOverheatProtection), "Update")]
    private static void OverheatEnd() => driving = null;

    private static void Start(GadgetBase gadget)
    {
        driving = NetworkRole.IsServer ? gadget : null;
        takes = true;
    }

    /// <summary>
    /// Runs a control panel's move on the server: stepped as a gadget's, but nobody's lever is taken
    /// (GadgetPanelServer checks the lease first, as for a cab lever).
    /// </summary>
    internal static void Stepping(GadgetBase panel, System.Action move)
    {
        GadgetBase wasDriving = driving;
        bool wasTaking = takes;
        driving = panel;
        takes = false;
        try
        {
            move();
        }
        finally
        {
            driving = wasDriving;
            takes = wasTaking;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(OverridableBaseControl), nameof(OverridableBaseControl.Move))]
    private static bool Move(OverridableBaseControl __instance, float notches, TrainCar ___car,
        InteriorControlsManager ___interiorControlsManager)
    {
        if (driving == null || ___car == null)
            return true;
        if (takes)
            TakeFromHolder(___car, __instance);
        if (___interiorControlsManager != null || __instance.IsNotched || __instance.IsControlBlocked)
            return true;
        __instance.Set(__instance.Value + notches * Step(___car, __instance.portId));
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(OverridableBaseControl), nameof(OverridableBaseControl.Set))]
    private static void Set(OverridableBaseControl __instance, TrainCar ___car)
    {
        if (driving != null && takes && ___car != null)
            TakeFromHolder(___car, __instance);
    }

    private static void TakeFromHolder(TrainCar car, OverridableBaseControl control)
    {
        if (string.IsNullOrEmpty(control.portId) || !NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networked))
            return;
        networked.Server()?.RevokeLease(NetworkedTrainCar.GetPortNetId(control.portId),
            $"{driving.name} moved {control.ControlType}");
    }

    /// <summary>The cab lever's scroll step for the control's port on this livery, as a fraction of its range.</summary>
    private static float Step(TrainCar car, string portId)
    {
        string livery = car.carLivery != null ? car.carLivery.id : car.carType.ToString();
        if (steps.TryGetValue((livery, portId), out float step))
            return step;
        step = GadgetControlStep.Fallback;
        GameObject interior = car.carLivery != null ? car.carLivery.interiorPrefab : null;
        InteractablePortFeeder feeder = interior == null ? null : interior.GetComponentsInChildren<InteractablePortFeeder>(true)
            .FirstOrDefault(f => f.portId == portId);
        if (feeder != null && feeder.GetComponent<ControlSpec>() is { } spec)
        {
            if (spec is Lever lever)
                step = GadgetControlStep.Lever(lever.scrollWheelHoverScroll, lever.useSteppedJoint, lever.notches,
                    lever.jointLimitMin, lever.jointLimitMax);
            else if (spec is Puller puller)
                step = GadgetControlStep.Puller(puller.scrollWheelHoverScroll);
        }
        else
        {
            Multiplayer.LogDebug(() => $"No cab lever for {portId} on {livery}: a gadget steps it by {step}");
        }
        steps[(livery, portId)] = step;
        return step;
    }
}
