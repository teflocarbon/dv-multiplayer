using DV;
using HarmonyLib;
using Multiplayer.Shared.Session;
using System;
using System.Diagnostics;

namespace Multiplayer.Client.World;

[HarmonyPatch(typeof(TimeAdvance))]
public static class TimeAdvancePatch
{
    public static bool FastTravelAdvancesTime { get; set; } = true;

    // Sleeping in a bed, as opposed to fast travel: TimeAdvance is called from the sleep coroutine.
    // Sleep has its own rule (the shared sleep cooldown the host checks), not the fast travel setting.
    private static bool IsSleep => BedSleepingController.Instance != null && BedSleepingController.Instance.IsSleeping;

    [HarmonyPatch(typeof(TimeAdvance), nameof(TimeAdvance.AdvanceTime))]
    private static bool Prefix(float amountOfTimeToSkipInSeconds)
    {
        // Todo: fast travel should be a request to the server, and the server should decide whether to allow it or not and handle time advancement. This is a temporary solution

        // No client - allow time to advance normally
        if (!NetworkLifecycle.Instance.IsClientRunning)
            return true;

        // Host is the only player - allow time to advance normally
        if (NetworkLifecycle.Instance.IsHostPlayingAlone)
            return true;

        // Applying a relayed advance (on the host too: its local client receives the relay of a
        // client's request) - advance, but don't send it on again or the requester skips twice.
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return true;

        bool sleep = IsSleep;

        if (NetworkLifecycle.Instance.IsHost())
        {
            // Host with other players connected - fast travel only advances if FastTravelAdvancesTime is enabled
            if (!FastTravelAdvancesTime && !sleep)
                return false;

            NetworkLifecycle.Instance.Client.SendTimeAdvance(amountOfTimeToSkipInSeconds, sleep);
            return true;
        }

        // Client sleeping: the bed menu already showed the shared cooldown; the host checks it again.
        if (sleep)
        {
            NetworkLifecycle.Instance.Client.SendTimeAdvance(amountOfTimeToSkipInSeconds, true);
            return true;
        }

        // Client: local fast travel - only allow if FastTravelAdvancesTime is enabled
        if (!FastTravelAdvancesTime)
            return false;

        NetworkLifecycle.Instance.Client.SendTimeAdvance(amountOfTimeToSkipInSeconds);
        return true;
    }
}
