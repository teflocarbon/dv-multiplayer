using DV.Interaction;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Core.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Multiplayer.Patches.World.Items;

/// <summary>
/// Repairs the Derail Valley non-VR grabber when a forced/rapid inventory transition leaves its
/// state machine in Holding or Dragging after the corresponding handler has already detached.
/// While latched, world hover is suppressed and RequestForceHold silently refuses every hotbar
/// item. A short consecutive-frame grace period avoids touching normal transition frames.
/// </summary>
[HarmonyPatch(typeof(Grabber), nameof(Grabber.DoUpdate))]
internal static class GrabberRecoveryPatch
{
    private const int RecoveryGraceFrames = 8;
    private static readonly Dictionary<int, Observation> observations = new();
    private static readonly FieldInfo stateField = AccessTools.Field(typeof(Grabber), "state");

    private sealed class Observation
    {
        public GrabberRecoveryAction Action;
        public int ConsecutiveFrames;
    }

    [HarmonyPostfix]
    private static void AfterUpdate(Grabber __instance)
    {
        if (__instance == null)
            return;

        GrabberRuntimeState state = ParseState(__instance.GetState());
        AGrabHandler held = __instance.CurrentItemHeld;
        AGrabHandler dragged = __instance.CurrentlyDragged;
        bool heldPresent = held != null;
        bool draggedPresent = dragged != null;
        bool heldReportsGrabbed = heldPresent && held.IsGrabbed();
        GrabberRecoveryAction action = GrabberRecoveryPlanner.Plan(new GrabberRecoveryInput(
            state, heldPresent, heldReportsGrabbed, draggedPresent));

        int instanceId = __instance.GetInstanceID();
        if (action == GrabberRecoveryAction.None)
        {
            observations.Remove(instanceId);
            return;
        }

        if (!observations.TryGetValue(instanceId, out Observation observation) ||
            observation.Action != action)
        {
            observation = new Observation { Action = action };
            observations[instanceId] = observation;
        }

        if (++observation.ConsecutiveFrames < RecoveryGraceFrames)
            return;

        observations.Remove(instanceId);
        Dictionary<string, object> data = Snapshot(__instance, state, held,
            heldReportsGrabbed, dragged, action, observation.ConsecutiveFrames);
        try
        {
            if (action == GrabberRecoveryAction.ReleaseStaleHeldItem)
                __instance.ReleaseHolding();
            else
                ResetEmptyState(__instance);

            data["recoveredState"] = __instance.GetState() ?? string.Empty;
            data["recoveredHolding"] = __instance.IsHoldingItem();
            data["recoveredDragging"] = __instance.IsDragging();
            DebugRuntime.Publish("interaction", "interaction.grabber-stale-state-recovered",
                Side(), DebugSeverity.Warning, "PlayerInteraction", "local", data);
        }
        catch (Exception exception)
        {
            data["exception"] = exception.ToString();
            DebugRuntime.Publish("interaction", "interaction.grabber-stale-state-recovery-failed",
                Side(), DebugSeverity.Error, "PlayerInteraction", "local", data);
        }
    }

    private static void ResetEmptyState(Grabber grabber)
    {
        if (stateField == null)
            throw new MissingFieldException(typeof(Grabber).FullName, "state");
        stateField.SetValue(grabber, Enum.ToObject(stateField.FieldType, 0));
        grabber.Raycaster?.UpdateRaycast();
    }

    private static Dictionary<string, object> Snapshot(Grabber grabber,
        GrabberRuntimeState state, AGrabHandler held, bool heldReportsGrabbed,
        AGrabHandler dragged, GrabberRecoveryAction action, int frames) => new()
    {
        ["action"] = action.ToString(),
        ["state"] = state.ToString(),
        ["rawState"] = grabber.GetState() ?? string.Empty,
        ["consecutiveFrames"] = frames,
        ["heldPresent"] = held != null,
        ["heldName"] = held?.name ?? string.Empty,
        ["heldPath"] = held != null ? held.gameObject.GetPath() : string.Empty,
        ["heldActiveSelf"] = held?.gameObject.activeSelf ?? false,
        ["heldActiveInHierarchy"] = held?.gameObject.activeInHierarchy ?? false,
        ["heldReportsGrabbed"] = heldReportsGrabbed,
        ["heldInteractionAllowed"] = held?.interactionAllowed ?? false,
        ["draggedPresent"] = dragged != null,
        ["draggedName"] = dragged?.name ?? string.Empty,
        ["isGrabbing"] = grabber.IsGrabbing(),
        ["isHolding"] = grabber.IsHoldingItem(),
        ["isDragging"] = grabber.IsDragging()
    };

    private static GrabberRuntimeState ParseState(string value) =>
        Enum.TryParse(value, true, out GrabberRuntimeState state)
            ? state
            : GrabberRuntimeState.Unknown;

    private static DebugRuntimeSide Side() =>
        global::Multiplayer.Components.Networking.NetworkLifecycle.Instance?.IsHost() == true
            ? DebugRuntimeSide.Server
            : DebugRuntimeSide.Client;
}
