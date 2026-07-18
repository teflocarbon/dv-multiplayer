#if DEBUG
using DV.Interaction.Inputs;
using DV.UI;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests;

internal enum RuntimeTestInteractionMode
{
    Pickup,
    Screenspace
}

/// <summary>
/// Owns human input while a dashboard-managed game is under automation. DV's
/// keyboard/mouse request system is used instead of Windows hooks, so ordinary
/// game input is blocked without preventing direct gameplay-method calls.
/// </summary>
internal static class RuntimeTestControlState
{
    private static readonly object KeyboardAndMouseRequest = new();
    private static readonly object CursorRequest = new();
    private static readonly object ScreenspaceRequest = new();
    private static bool userControl;
    private static bool appliedUserControl;
    private static bool initialized;
    private static float audioVolumeBeforeHarness;
    private static float nextApply;
    private static string activeTest = string.Empty;
    private static string activeRun = string.Empty;
    private static RuntimeTestInteractionMode interactionMode = RuntimeTestInteractionMode.Pickup;

    internal static bool Managed => DebugRuntime.PreventCursorCapture;
    internal static bool UserControl => Managed && userControl;
    internal static bool AutomationOwnsMouse => Managed && !userControl;
    internal static bool AutomationOwnsKeyboard => Managed && !userControl;
    internal static bool TestsPaused => UserControl;
    internal static RuntimeTestInteractionMode InteractionMode => interactionMode;
    internal static string ActiveTest => activeTest;
    internal static bool AudioMuted => Managed && initialized;

    internal static void Initialize()
    {
        userControl = false;
        appliedUserControl = true;
        initialized = true;
        interactionMode = RuntimeTestInteractionMode.Pickup;
        audioVolumeBeforeHarness = AudioListener.volume;
        AudioListener.volume = 0f;
        nextApply = 0f;
        Apply(true);
    }

    internal static void Shutdown()
    {
        if (!initialized) return;
        try { InputManager.SetKeyboardAndMouseEnabled(KeyboardAndMouseRequest, true); }
        catch { }
        if (CursorManager.Instance != null)
            CursorManager.Instance.RemoveRequest(CursorRequest);
        if (ScreenspaceMouse.Instance != null)
            ScreenspaceMouse.Instance.RemoveRequest(ScreenspaceRequest);
        AudioListener.volume = audioVolumeBeforeHarness;
        initialized = false;
        userControl = false;
        activeTest = string.Empty;
        activeRun = string.Empty;
    }

    internal static void Tick()
    {
        if (!Managed) return;
        // A managed test instance must stay silent even if game code changes the
        // listener volume while loading a scene or applying preferences.
        AudioListener.volume = 0f;
        if (Time.unscaledTime >= nextApply) Apply(false);
        if (AutomationOwnsMouse)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    internal static void ToggleUserControl() => SetUserControl(!userControl);

    internal static void SetUserControl(bool enabled)
    {
        if (!Managed || userControl == enabled) return;
        userControl = enabled;
        Apply(true);
        DebugRuntime.Publish("runtime-test", enabled ? "runtime-test.user-control-acquired" :
                "runtime-test.automation-control-acquired", DebugRuntimeSide.Shared,
            data: Snapshot());
    }

    internal static void SetInteractionMode(RuntimeTestInteractionMode mode)
    {
        interactionMode = mode;
        if (!UserControl) Apply(true);
    }

    internal static void SetActiveTest(string test, string run)
    {
        activeTest = test ?? string.Empty;
        activeRun = run ?? string.Empty;
    }

    internal static void ClearActiveTest()
    {
        activeTest = string.Empty;
        activeRun = string.Empty;
    }

    internal static Dictionary<string, object> Snapshot()
    {
        Vector3? absolute = AbsolutePlayerPosition();
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["managed"] = Managed,
            ["controlOwner"] = UserControl ? "User" : "Automation",
            ["mouseOwner"] = UserControl ? "User" : "Automation",
            ["keyboardOwner"] = UserControl ? "User" : "Automation",
            ["audioMuted"] = AudioMuted,
            ["testsPaused"] = TestsPaused,
            ["interactionMode"] = CurrentInteractionModeName(),
            ["screenspaceMouseOn"] = ScreenspaceMouse.Instance?.on == true,
            ["activeTest"] = activeTest,
            ["activeRun"] = activeRun,
            ["playerAbsolutePosition"] = absolute.HasValue ?
                DebugValueSnapshotter.Snapshot(absolute.Value) : null
        };
    }

    internal static string CopyAbsolutePosition()
    {
        Vector3? position = AbsolutePlayerPosition();
        if (!position.HasValue) return string.Empty;
        Vector3 value = position.Value;
        string text = string.Format(CultureInfo.InvariantCulture,
            "{0:0.###}, {1:0.###}, {2:0.###}", value.x, value.y, value.z);
        GUIUtility.systemCopyBuffer = text;
        return text;
    }

    private static void Apply(bool force)
    {
        if (!Managed) return;
        nextApply = Time.unscaledTime + 0.5f;
        if (force || appliedUserControl != userControl)
        {
            try { InputManager.SetKeyboardAndMouseEnabled(KeyboardAndMouseRequest, userControl); }
            catch (Exception exception)
            {
                Multiplayer.LogWarning("Runtime-test input ownership could not be applied: " + exception.Message);
            }
            appliedUserControl = userControl;
        }

        ScreenspaceMouse screenspace = ScreenspaceMouse.Instance;
        if (userControl)
        {
            CursorManager.Instance?.RemoveRequest(CursorRequest);
            screenspace?.RemoveRequest(ScreenspaceRequest);
        }
        else
        {
            // GrabberRaycasterDV deliberately refuses to raycast while both the cursor is
            // unlocked and CursorManager considers it hidden. Register through DV's cursor
            // request system so pickup mode remains raycast-capable without allowing real
            // background input or relying on a Windows cursor hook.
            CursorManager.Instance?.RequestCursor(CursorRequest, true, 10000);
            screenspace?.RequestOverride(ScreenspaceRequest,
                interactionMode == RuntimeTestInteractionMode.Screenspace, 10000);
        }
    }

    private static Vector3? AbsolutePlayerPosition()
    {
        if (PlayerManager.PlayerTransform == null || WorldMover.Instance == null) return null;
        return PlayerManager.PlayerTransform.position - WorldMover.currentMove;
    }

    private static string CurrentInteractionModeName()
    {
        if (UserControl) return ScreenspaceMouse.Instance?.on == true ? "Screenspace" : "Pickup";
        return interactionMode.ToString();
    }
}

internal sealed class RuntimeTestControlOverlay : MonoBehaviour
{
    private GUIStyle panelStyle;
    private GUIStyle titleStyle;
    private GUIStyle detailStyle;
    private string copyStatus = string.Empty;
    private float copyStatusUntil;

    private void Awake() => RuntimeTestControlState.Initialize();

    private void Update()
    {
        if (!RuntimeTestControlState.Managed) return;
        bool chord = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        chord &= Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (chord && Input.GetKeyDown(KeyCode.F5)) RuntimeTestControlState.ToggleUserControl();
        if (chord && Input.GetKeyDown(KeyCode.F2))
        {
            string copied = RuntimeTestControlState.CopyAbsolutePosition();
            copyStatus = copied.Length == 0 ? "POSITION UNAVAILABLE" : "COPIED " + copied;
            copyStatusUntil = Time.unscaledTime + 3f;
        }
        RuntimeTestControlState.Tick();
    }

    private void LateUpdate() => RuntimeTestControlState.Tick();

    private void OnGUI()
    {
        if (!RuntimeTestControlState.Managed) return;
        EnsureStyles();
        Dictionary<string, object> state = RuntimeTestControlState.Snapshot();
        bool user = RuntimeTestControlState.UserControl;
        string test = state["activeTest"]?.ToString();
        string position = FormatPosition(state["playerAbsolutePosition"]);
        string details =
            $"Mouse: {(user ? "USER" : "AUTOMATION")}   Keyboard: {(user ? "USER" : "AUTOMATION")}   " +
            $"Mode: {state["interactionMode"]}   Audio: MUTED\n" +
            $"Test: {(string.IsNullOrEmpty(test) ? "idle" : test)}   Position: {position}\n" +
            "Ctrl+Shift+F5: toggle control   Ctrl+Shift+F2: copy position";
        if (Time.unscaledTime < copyStatusUntil) details += "\n" + copyStatus;

        float height = Time.unscaledTime < copyStatusUntil ? 92f : 76f;
        GUI.Box(new Rect(12f, 88f, 610f, height), GUIContent.none, panelStyle);
        GUI.Label(new Rect(22f, 94f, 590f, 20f),
            user ? "DVMP HARNESS — USER CONTROL / TESTS PAUSED" :
                "DVMP HARNESS — AUTOMATION CONTROL", titleStyle);
        GUI.Label(new Rect(22f, 114f, 590f, height - 24f), details, detailStyle);
    }

    private void EnsureStyles()
    {
        if (panelStyle != null) return;
        panelStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft };
        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 13,
            normal = { textColor = new Color(0.4f, 0.95f, 0.7f) }
        };
        detailStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = Color.white }
        };
    }

    private static string FormatPosition(object snapshot)
    {
        if (snapshot is not IDictionary<string, object> values) return "unavailable";
        return values.TryGetValue("x", out object x) && values.TryGetValue("y", out object y) &&
               values.TryGetValue("z", out object z) ? $"{x}, {y}, {z}" : "unavailable";
    }

    private void OnDestroy() => RuntimeTestControlState.Shutdown();
}
#endif
