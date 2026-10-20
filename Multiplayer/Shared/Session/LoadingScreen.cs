using UnityEngine;

namespace Multiplayer.Shared.Session;

/// <summary>
///     Takes this game out of DV's loading screen once the session has synced its world.
///     <c>DisplayLoadingInfo_Start_Patch</c> unhooks DV's own "loading finished", so this is the
///     only thing that does it: on a remote client when the server says its world is synced, on the
///     host when its own player has finished loading.
/// </summary>
public static class LoadingScreen
{
    public static void Finish()
    {
        DisplayLoadingInfo displayLoadingInfo = Object.FindObjectOfType<DisplayLoadingInfo>();
        if (displayLoadingInfo != null)
            displayLoadingInfo.OnLoadingFinished();
        else
            Multiplayer.LogWarning($"Finishing loading, but couldn't find {nameof(DisplayLoadingInfo)}");

        // Only FinishLoading() leaves loading mode. Without it a VR player is left in the world (the
        // VR loading overlay closes by itself at 100%) with the hand controllers switched off, their
        // own controllers showing and no teleport (#72).
        if (LoadingScreenManager.IsLoading)
        {
            Multiplayer.LogWarning("Still in loading mode after removing the loading screen, finishing it");
            LoadingScreenManager.Instance.FinishLoading();
        }
    }
}
