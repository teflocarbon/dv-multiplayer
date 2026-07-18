#if DEBUG
using DV;
using DV.InventorySystem;
using DV.UI;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Multiplayer.Debugging.RuntimeTests;

/// <summary>
/// Establishes the neutral gameplay surface expected at scenario boundaries:
/// world loaded, inventory closed, pause menu closed, and pickup interaction mode.
/// </summary>
internal static class RuntimeTestGameState
{
    internal static bool WorldAvailable => PlayerManager.PlayerTransform != null &&
        WorldMover.Instance != null && SceneManager.GetActiveScene().name.StartsWith("game_",
            StringComparison.OrdinalIgnoreCase);

    internal static bool InventoryOpen => InventoryViewBase.Instance?.inventoryUI?.IsOpen == true;
    internal static bool PauseMenuOpen => AppUtil.Instance?.IsPauseMenuOpen == true;
    internal static bool ScreenspaceMode => ScreenspaceMouse.Instance?.on == true;
    internal static bool Neutral => !WorldAvailable ||
        (!InventoryOpen && !PauseMenuOpen && !ScreenspaceMode);

    internal static IEnumerator EnsureNeutral(RuntimeTestRunDto run, string resultPrefix,
        float timeoutSeconds = 3f)
    {
        if (!WorldAvailable)
        {
            AddResult(run, resultPrefix, "world-unavailable-noop", true);
            yield break;
        }

        RuntimeTestControlState.SetInteractionMode(RuntimeTestInteractionMode.Pickup);
        CloseInventory();
        ClosePauseMenu();
        yield return null;

        float started = Time.realtimeSinceStartup;
        while (!Neutral && Time.realtimeSinceStartup - started <= timeoutSeconds)
        {
            CloseInventory();
            ClosePauseMenu();
            RuntimeTestControlState.SetInteractionMode(RuntimeTestInteractionMode.Pickup);
            yield return null;
        }

        bool neutral = Neutral;
        AddResult(run, resultPrefix, neutral ? "neutral" : "neutralization-timeout", neutral);
        if (!neutral)
            throw new InvalidOperationException("neutral-world-state-timeout:" + Describe());
    }

    internal static Dictionary<string, object> Snapshot() => new(StringComparer.Ordinal)
    {
        ["worldAvailable"] = WorldAvailable,
        ["scene"] = SceneManager.GetActiveScene().name,
        ["inventoryOpen"] = InventoryOpen,
        ["pauseMenuOpen"] = PauseMenuOpen,
        ["screenspaceMode"] = ScreenspaceMode,
        ["neutral"] = Neutral
    };

    private static void CloseInventory()
    {
        if (InventoryViewBase.Instance?.inventoryUI?.IsOpen == true)
            InventoryViewBase.Instance.inventoryUI.Toggle(false);
    }

    private static void ClosePauseMenu()
    {
        if (AppUtil.Instance?.IsPauseMenuOpen != true) return;
        PauseMenuController controller = UnityEngine.Object.FindObjectOfType<PauseMenuController>();
        controller?.RequestClose();
        if (AppUtil.Instance?.IsTimePaused == true) AppUtil.Instance.UnpauseGame();
    }

    private static string Describe() =>
        $"inventoryOpen={InventoryOpen},pauseMenuOpen={PauseMenuOpen}," +
        $"screenspaceMode={ScreenspaceMode},scene={SceneManager.GetActiveScene().name}";

    private static void AddResult(RuntimeTestRunDto run, string prefix, string status,
        bool neutral)
    {
        if (run == null) return;
        lock (run)
        {
            run.Result[(prefix ?? "neutralWorld") + "Status"] = status;
            run.Result[(prefix ?? "neutralWorld") + "Neutral"] = neutral;
            run.Result[(prefix ?? "neutralWorld") + "State"] = Snapshot();
        }
    }
}
#endif
