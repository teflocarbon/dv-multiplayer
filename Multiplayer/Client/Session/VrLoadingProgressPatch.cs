using HarmonyLib;

namespace Multiplayer.Client.Session;

/// <summary>
/// Keeps a VR player in the VR loading view until the loading screen is removed.
/// </summary>
/// <remarks>
/// DV's VR loading view (<c>VrLoading</c>) suspends the headset's rendering and shows its own
/// overlay until its progress reaches 1, then fades into the world. Progress comes from the loading
/// screen's status messages, and vanilla's own end at 93%: only <c>FinishLoading()</c>, when the
/// world has loaded, takes it to 1. The mod keeps the loading screen up through the world sync and
/// reports 100% before it's done: on "Complete", while the host's items, jobs and the "remove
/// loading screen" behind them are still arriving, and for a mod that clears its ready block. The
/// headset then showed the world being filled in while the desktop still showed the loading
/// screen. That screen's own camera is still rendering then (vanilla destroys it as the headset
/// fades in), the likely cause of the eye players saw upside down.
///
/// Holding progress below 1 leaves <c>FinishLoading()</c>, which the "remove loading screen"
/// handler calls in the same frame it destroys the loading screen, as the only way out, as in
/// vanilla. It only matters in VR: <c>UpdateProgress</c> does nothing without a VR loading view.
/// </remarks>
[HarmonyPatch(typeof(LoadingScreenManager), nameof(LoadingScreenManager.UpdateProgress))]
public static class LoadingScreenManager_UpdateProgress_Patch
{
    private const float MaxProgressWhileLoading = 0.99f;

    private static void Prefix(ref float progress)
    {
        if (progress > MaxProgressWhileLoading)
            progress = MaxProgressWhileLoading;
    }
}
