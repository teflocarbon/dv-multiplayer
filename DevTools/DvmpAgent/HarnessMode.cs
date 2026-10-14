using System;
using System.Linq;
using HarmonyLib;
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
    /// Mip levels dropped when textures load (QualitySettings.masterTextureLimit) while
    /// LowGraphics is on: 2 loads every texture at a quarter of its width and height. -1 leaves
    /// the game's value.
    /// </summary>
    public static int TextureLimit { get; set; } = 2;

    /// <summary>VRAM budget for DV's streamed textures, in MB, while LowGraphics is on. 0 leaves the game's value.</summary>
    public static int TextureBudgetMb { get; set; } = 512;

    /// <summary>
    /// World tile loading range, in tiles, for DV's near (detailed) and far (distant scenery)
    /// streamers. -1 leaves DV's range. Track is in its own scene, so this only changes scenery.
    /// </summary>
    public static int NearRange { get; set; } = -1;
    public static int FarRange { get; set; } = -1;

    private static readonly AccessTools.FieldRef<WorldStreamingInit, Streamer> nearStreamer =
        AccessTools.FieldRefAccess<WorldStreamingInit, Streamer>("nearScenesStreamer");
    private static readonly AccessTools.FieldRef<WorldStreamingInit, Streamer> farStreamer =
        AccessTools.FieldRefAccess<WorldStreamingInit, Streamer>("farScenesStreamer");
    private static WorldStreamingInit streamingInit;
    private float nextStreamingCheck;

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
        TextureLimit = IntArg(args, "-dvmp-texture-limit", TextureLimit);
        TextureBudgetMb = IntArg(args, "-dvmp-texture-budget", TextureBudgetMb);
        NearRange = IntArg(args, "-dvmp-near-range", NearRange);
        FarRange = IntArg(args, "-dvmp-far-range", FarRange);
        UserInput = args.Contains("-dvmp-input");
        // Before the world loads, so textures are read at the reduced size in the first place.
        if (LowGraphics) ApplyTextureSettings();
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
            ApplyTextureSettings();
        }

        // Checked often enough to catch the streamers before their first tiles load.
        if (Time.unscaledTime >= nextStreamingCheck)
        {
            nextStreamingCheck = Time.unscaledTime + 0.25f;
            ApplyStreamingRanges();
        }
    }

    private static void ApplyTextureSettings()
    {
        if (TextureLimit >= 0 && QualitySettings.masterTextureLimit != TextureLimit)
            QualitySettings.masterTextureLimit = TextureLimit;
        if (TextureBudgetMb > 0 && !Mathf.Approximately(QualitySettings.streamingMipmapsMemoryBudget, TextureBudgetMb))
            QualitySettings.streamingMipmapsMemoryBudget = TextureBudgetMb;
    }

    private static void ApplyStreamingRanges()
    {
        if (NearRange < 0 && FarRange < 0) return;
        // A new WorldStreamingInit comes with every world load (e.g. a save reload).
        if (streamingInit == null) streamingInit = FindObjectOfType<WorldStreamingInit>();
        if (streamingInit == null) return;
        SetRange(nearStreamer(streamingInit), NearRange);
        SetRange(farStreamer(streamingInit), FarRange);
    }

    private static void SetRange(Streamer streamer, int range)
    {
        if (streamer == null || range < 0 || (int)streamer.loadingRange.x == range) return;
        // Keep DV's gap between the load and unload ranges so tiles don't flicker at the edge.
        float margin = Mathf.Max(1f, streamer.deloadingRange.x - streamer.loadingRange.x);
        streamer.loadingRange = new Vector3(range, streamer.loadingRange.y, range);
        streamer.deloadingRange = new Vector3(range + margin, streamer.deloadingRange.y, range + margin);
        if (streamer.useLoadingRangeMin)
            streamer.loadingRangeMin = Vector3.Min(streamer.loadingRangeMin, streamer.loadingRange);
    }

    private static object StreamerSnapshot(Streamer streamer) => streamer == null ? null : new
    {
        loading = new[] { streamer.loadingRange.x, streamer.loadingRange.y, streamer.loadingRange.z },
        deloading = new[] { streamer.deloadingRange.x, streamer.deloadingRange.y, streamer.deloadingRange.z },
        loadedScenes = streamer.loadedScenes?.Count ?? 0
    };

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
        ? new
        {
            active = true, fpsCap = FpsCap, backgroundFpsCap = BackgroundFpsCap, muted = Muted, lowGraphics = LowGraphics, userInput = UserInput, targetFrameRate = Application.targetFrameRate,
            textureLimit = QualitySettings.masterTextureLimit,
            textureBudgetMb = QualitySettings.streamingMipmapsMemoryBudget,
            textureMemoryMb = Texture.currentTextureMemory / (1024 * 1024),
            nearStreamer = streamingInit == null ? null : StreamerSnapshot(nearStreamer(streamingInit)),
            farStreamer = streamingInit == null ? null : StreamerSnapshot(farStreamer(streamingInit))
        }
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
