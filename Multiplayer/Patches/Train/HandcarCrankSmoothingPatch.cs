using System.Linq;
using System.Runtime.CompilerServices;
using DV.Simulation.Controllers;
using HarmonyLib;
using LocoSim.Implementations;
using Multiplayer.Components.Networking;
using UnityEngine;

namespace Multiplayer.Patches.Train;

/// <summary>
/// A client's handcar crank, drawn every frame instead of at the network tick.
///
/// Clients don't run the simulation (<see cref="Multiplayer.Patches.World.SimComponent_Tick_Patch"/>),
/// so a client's HandcarController drew the crank at the host's <c>CURRENT_POSITION</c>, which
/// arrives 24 times a second, unevenly and a little late. The crank moved in steps, and pumping
/// it is a matter of pushing ahead of where the crank is: a VR player chasing a stepping crank
/// fell out of rhythm, each stroke's torque cancelled the last, and the handcar barely moved.
///
/// Here the crank advances each frame from the wheels' RPM, which a client's traction feeder
/// updates every frame, turning at the ends as <see cref="HandcarDrive"/> does, and eases toward
/// the host's position. The host still decides the torque and the motion; this is only drawing.
/// </summary>
[HarmonyPatch(typeof(HandcarController), nameof(HandcarController.Tick))]
public static class HandcarCrankSmoothingPatch
{
    // Per second, toward the host's position.
    private const float CORRECTION_RATE = 4f;
    // About how old the host's position is when it lands.
    private const float HOST_LEAD_SECONDS = 0.08f;
    // Further off than this (a resync, a rerail) and the crank jumps to the host's position.
    private const float SNAP_DISTANCE = 0.6f;
    // Near the ends the two can be either side of a turn; elsewhere the host's direction stands.
    private const float DIRECTION_TRUST_LIMIT = 0.7f;

    private class Crank
    {
        public HandcarDrive Drive;
        public float Position;
        public int Direction = 1;
        public bool Started;
    }

    private static readonly ConditionalWeakTable<HandcarController, Crank> cranks = new();

    private static void Postfix(HandcarController __instance, float deltaTime)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !lifecycle.IsClientRunning || lifecycle.IsHost())
            return;
        if (__instance.handlebarAnimator == null || __instance.currentPositionPort == null || __instance.directionPort == null)
            return;

        Crank crank = cranks.GetValue(__instance, controller => new Crank
        {
            Drive = controller.car?.SimController?.simFlow?.OrderedSimComps.OfType<HandcarDrive>().FirstOrDefault()
        });
        if (crank.Drive == null)
            return;

        float host = __instance.currentPositionPort.Value;
        int hostDirection = __instance.directionPort.Value > 0f ? 1 : -1;
        if (!crank.Started)
        {
            crank.Position = host;
            crank.Direction = hostDirection;
            crank.Started = true;
        }

        // HandcarDrive.Tick, without the torque.
        float rate = crank.Drive.wheelRpm.Value / 60f * crank.Drive.gearRatio.Value;
        crank.Position += rate * crank.Direction * deltaTime;
        if (crank.Position >= 1f || crank.Position <= -1f)
            crank.Direction = -crank.Direction;
        crank.Position = Mathf.Clamp(crank.Position, -1f, 1f);

        if (Mathf.Abs(host) < DIRECTION_TRUST_LIMIT && Mathf.Abs(crank.Position) < DIRECTION_TRUST_LIMIT)
            crank.Direction = hostDirection;
        float target = Mathf.Clamp(host + rate * hostDirection * HOST_LEAD_SECONDS, -1f, 1f);
        if (Mathf.Abs(target - crank.Position) > SNAP_DISTANCE)
            crank.Position = target;
        else
            crank.Position += (target - crank.Position) * Mathf.Min(1f, CORRECTION_RATE * deltaTime);

        // HandcarController.Tick's mapping of position and direction to the animation.
        float position = crank.Position;
        float time = crank.Direction > 0 ? 0.25f + (1f - Mathf.Abs(position)) * 0.25f : Mathf.Abs(position) * 0.25f;
        if (position < 0f)
            time = 1f - time;
        __instance.handlebarAnimator.SetFloat(__instance.crankAnimParameter, time);
    }
}
