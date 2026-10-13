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

    internal static object Snapshot() => Active
        ? new { active = true, fpsCap = FpsCap, backgroundFpsCap = BackgroundFpsCap, muted = Muted, lowGraphics = LowGraphics, targetFrameRate = Application.targetFrameRate }
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
