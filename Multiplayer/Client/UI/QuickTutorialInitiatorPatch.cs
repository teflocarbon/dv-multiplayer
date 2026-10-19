using DV.Tutorial.QT;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.UI;

[HarmonyPatch(nameof(QuickTutorialInitiator), nameof(QuickTutorialInitiator.IsPlayerOnLocoThatSupportsQuickTutorial))]
public static class QuickTutorialInitiator_IsPlayerOnLocoThatSupportsQuickTutorial_Patch
{
    private static bool Prefix()
    {
        return NetworkLifecycle.Instance.IsHostPlayingAlone;
    }
}
