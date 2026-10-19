using System;
using System.Collections.Generic;
using HarmonyLib;
using LocoSim.Implementations;
using Multiplayer.Core.Trains;
using Multiplayer.Server.Session;

namespace Multiplayer.Server.Trains;

/// <summary>
/// Lets several players pump a handcar at once. Vanilla's HandcarDrive has one handle: the host's
/// own player works it through the car's ports, as vanilla. Other players' handles are kept on the
/// host (<see cref="HandcarPumpers{TPlayer}"/>, filled by NetworkedTrainCar from their ports) and
/// each one's stroke is added to the torque vanilla worked out, capped at two players' worth.
/// Registered only on the host, the one game whose simulation ticks.
/// </summary>
[HarmonyPatch(typeof(HandcarDrive), nameof(HandcarDrive.Tick))]
public static class HandcarDrivePatch
{
    private sealed class Registration
    {
        public HandcarPumpers<ServerPlayer> Pumpers;
        public Func<float, float> PositionDiffToTorque;
    }

    private static readonly Dictionary<HandcarDrive, Registration> registered = [];

    internal static void Register(HandcarDrive drive, HandcarPumpers<ServerPlayer> pumpers) =>
        registered[drive] = new Registration
        {
            Pumpers = pumpers,
            PositionDiffToTorque = diff => drive.positionDiffToTorque.Evaluate(diff),
        };

    internal static void Unregister(HandcarDrive drive) => registered.Remove(drive);

    private static void Postfix(HandcarDrive __instance)
    {
        if (registered.Count == 0 || !registered.TryGetValue(__instance, out Registration registration) ||
            registration.Pumpers.EngagedCount == 0)
            return;

        HandcarDrive drive = __instance;
        float crankRate = drive.wheelRpm.Value / 60f * drive.gearRatio.Value;
        bool localEngaged = drive.handleEngagedExtIn.Value > 0f;
        float torque = registration.Pumpers.CombinedTorque(drive.torqueOut.Value, localEngaged,
            drive.currentPositionOut.Value, drive.directionOut.Value > 0f ? 1 : -1, crankRate,
            drive.maxTorqueProduction, registration.PositionDiffToTorque, out float firstHandlePosition);

        drive.torqueOut.Value = torque;
        drive.actingAgainstOut.Value =
            HandcarPumpers<ServerPlayer>.Sign(torque) != HandcarPumpers<ServerPlayer>.Sign(crankRate) ? 1f : 0f;
        if (!localEngaged)
            drive.engagedHandlePositionOut.Value = firstHandlePosition;
    }
}
