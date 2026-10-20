using DV;
using HarmonyLib;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.World;

/// <summary>
///     The host's own fast travel and sleep: the host's clock advances here at once, and the skip
///     goes to the server in-process, which relays it to the other players (and shares the wake
///     time). A remote client's are <c>Client/World/TimeAdvancePatch</c>; each returns true when
///     it isn't its role, so their order doesn't matter.
/// </summary>
[HarmonyPatch(typeof(TimeAdvance), nameof(TimeAdvance.AdvanceTime))]
public static class TimeAdvanceServerPatch
{
    private static bool IsSleep => BedSleepingController.Instance != null && BedSleepingController.Instance.IsSleeping;

    private static bool Prefix(float amountOfTimeToSkipInSeconds)
    {
        NetworkServer server = NetworkLifecycle.Instance.Server;
        if (server == null || !server.IsRunning)
            return true;

        // Host is the only player, or nobody plays here yet - allow time to advance normally
        if (NetworkLifecycle.Instance.IsHostPlayingAlone || server.LocalPlayer == null)
            return true;

        // Applying another player's advance (a client's request or /time) - advance, but don't
        // send it on again or the requester skips twice.
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return true;

        bool sleep = IsSleep;

        // Host with other players connected - fast travel only advances if FastTravelAdvancesTime is enabled
        if (!server.FastTravelAdvancesTime && !sleep)
            return false;

        server.Submit(new ServerboundTimeAdvancePacket
        {
            amountOfTimeToSkipInSeconds = amountOfTimeToSkipInSeconds,
            IsSleep = sleep
        });
        return true;
    }
}
