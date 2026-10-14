using System;
using System.Linq;
using UnityEngine;

namespace Dvmp.Agent;

/// <summary>
/// Keeps harness-launched games cheap and quiet so they can run in the background (e.g. while
/// the user is in VR): muted, frame-capped, lowest graphics quality. Only active when the game
/// was started with -dvmp-role, so a normal play session is never affected. Nothing here is
/// persisted to the game's settings.
///
/// DV sets frame rate and master volume from several places (FrameLimiter, AudioManager,
/// LoadingScreenManager), so values are re-asserted every frame after DV's own updates.
/// </summary>
public sealed class HarnessMode : MonoBehaviour
{
    public static bool Active { get; private set; }

    /// <summary>Frame cap while the window has focus. Probes may change it at runtime.</summary>
    public static int FpsCap { get; set; } = 30;

    /// <summary>Frame cap while unfocused or minimised.</summary>
    public static int BackgroundFpsCap { get; set; } = 20;

    public static bool Muted { get; set; } = true;
    public static bool LowGraphics { get; set; } = true;

    /// <summary>
    /// Whether real keyboard/mouse input reaches the game. Off by default so an accidental click
    /// on a background test window can't move the player or capture the cursor. Toggle in-game
    /// with Ctrl+Shift+F5, or launch with -dvmp-input.
    /// </summary>
    public static bool UserInput { get; set; }

    private static readonly object inputRequester = new();
    private bool? appliedUserInput;

    private float nextGraphicsPass;
    private float nextForegroundCheck;
    private bool foreground;
    private static readonly int ownPid = System.Diagnostics.Process.GetCurrentProcess().Id;

    internal static void InstallIfRequested(GameObject host)
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!args.Contains("-dvmp-role")) return;

        Active = true;
        FpsCap = IntArg(args, "-dvmp-fps", FpsCap);
        BackgroundFpsCap = IntArg(args, "-dvmp-bg-fps", BackgroundFpsCap);
        Muted = !args.Contains("-dvmp-sound");
        LowGraphics = !args.Contains("-dvmp-full-graphics");
        UserInput = args.Contains("-dvmp-input");
        host.AddComponent<HarnessMode>();
    }

    private void LateUpdate()
    {
        // Unity's isFocused stays true for a minimised window, so ask Windows instead.
        if (Time.unscaledTime >= nextForegroundCheck)
        {
            nextForegroundCheck = Time.unscaledTime + 1f;
            foreground = IsForeground();
        }

        if (Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.F5))
            UserInput = !UserInput;
        ApplyInputOwnership();

        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = foreground ? FpsCap : BackgroundFpsCap;
        if (Muted) AudioListener.volume = 0f;

        // Graphics options are re-applied periodically because DV re-applies its own
        // preferences on scene loads.
        if (LowGraphics && Time.unscaledTime >= nextGraphicsPass)
        {
            nextGraphicsPass = Time.unscaledTime + 5f;
            if (QualitySettings.GetQualityLevel() != 0) QualitySettings.SetQualityLevel(0, applyExpensiveChanges: true);
            QualitySettings.shadows = ShadowQuality.Disable;
            QualitySettings.pixelLightCount = 0;
            QualitySettings.lodBias = 0.3f;
            QualitySettings.antiAliasing = 0;
        }
    }

    // Uses DV's own input/cursor request systems (keyed by requester), so probes that take and
    // release control (Hands) stack with this instead of overriding it. Re-applied every frame
    // until the managers exist (they are recreated across scene loads).
    private void ApplyInputOwnership()
    {
        try
        {
            if (appliedUserInput == UserInput && DV.UI.CursorManager.Instance != null) return;
            DV.Interaction.Inputs.InputManager.SetKeyboardAndMouseEnabled(inputRequester, UserInput);
            if (DV.UI.CursorManager.Instance == null) return;
            if (UserInput) DV.UI.CursorManager.Instance.RemoveRequest(inputRequester);
            else DV.UI.CursorManager.Instance.RequestCursor(inputRequester, true, 5000);
            appliedUserInput = UserInput;
        }
        catch { /* input systems not up yet (bootstrap); retry next frame */ }
    }

    internal static object Snapshot() => Active
        ? new { active = true, fpsCap = FpsCap, backgroundFpsCap = BackgroundFpsCap, muted = Muted, lowGraphics = LowGraphics, userInput = UserInput, targetFrameRate = Application.targetFrameRate }
        : (object)new { active = false };

    private static bool IsForeground()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out int pid);
            return pid == ownPid;
        }
        catch { return Application.isFocused; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

    private static int IntArg(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int value) ? value : fallback;
    }
}
