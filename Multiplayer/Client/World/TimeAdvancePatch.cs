using DV;
using HarmonyLib;
using Multiplayer.Shared.Session;
using System;
using System.Diagnostics;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.World;

[HarmonyPatch(typeof(TimeAdvance))]
public static class TimeAdvancePatch
{
    /// <summary>The host's fast travel setting, from its game params.</summary>
    public static bool FastTravelAdvancesTime { get; set; } = true;

    // Sleeping in a bed, as opposed to fast travel: TimeAdvance is called from the sleep coroutine.
    // Sleep has its own rule (the shared sleep cooldown the host checks), not the fast travel setting.
    private static bool IsSleep => BedSleepingController.Instance != null && BedSleepingController.Instance.IsSleeping;

    [HarmonyPatch(typeof(TimeAdvance), nameof(TimeAdvance.AdvanceTime))]
    private static bool Prefix(float amountOfTimeToSkipInSeconds)
    {
        // Todo: fast travel should be a request to the server, and the server should decide whether to allow it or not and handle time advancement. This is a temporary solution

        // No client, or the host's own skip (Server/World/TimeAdvanceServerPatch) - allow time to advance normally
        if (!NetworkRole.IsRemoteClient)
            return true;

        // Applying the host's relay of another player's advance - advance, but don't send it on
        // again or the requester skips twice.
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return true;

        bool sleep = IsSleep;

        // Client sleeping: the bed menu already showed the shared cooldown; the host checks it again.
        if (sleep)
        {
            NetworkClient.Instance.SendTimeAdvance(amountOfTimeToSkipInSeconds, true);
            return true;
        }

        // Client: local fast travel - only allow if FastTravelAdvancesTime is enabled
        if (!FastTravelAdvancesTime)
            return false;

        NetworkClient.Instance.SendTimeAdvance(amountOfTimeToSkipInSeconds);
        return true;
    }
}
