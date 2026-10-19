using System;
using UnityEngine;
using UnityModManagerNet;

namespace Dvmp.Agent;

/// <summary>The remote-debugging controls in the mod's Unity Mod Manager settings (Ctrl+F10).</summary>
internal static class RemoteUi
{
    private static GUIStyle warning;

    public static void Draw(UnityModManager.ModEntry entry)
    {
        warning ??= new GUIStyle(GUI.skin.label) { wordWrap = true, normal = { textColor = new Color(1f, 0.75f, 0.3f) } };

        GUILayout.Label("<b>Remote debugging over Tailscale</b>");
        GUILayout.Label("Lets a paired person run any code they like in this game, from their PC. " +
                        "Only pair someone you would hand your PC to. It is always off when the game starts.", warning);
        GUILayout.Space(6);

        switch (RemoteAccess.Mode)
        {
            case RemoteMode.Off:
                if (RemoteAccess.Message != null) GUILayout.Label(RemoteAccess.Message, warning);
                GUILayout.BeginHorizontal();
                GUI.enabled = RemoteAccess.Pairings.Count > 0;
                if (GUILayout.Button("Allow a paired debugger for this session", GUILayout.Width(300))) RemoteAccess.AllowSession();
                GUI.enabled = true;
                if (GUILayout.Button("Pair a new debugger", GUILayout.Width(180))) RemoteAccess.StartPairing();
                GUILayout.EndHorizontal();
                break;

            case RemoteMode.Pairing:
                GUILayout.Label($"Waiting to pair. Give them this address and pairing code " +
                                $"(it works once, for {Math.Max(0, RemoteAccess.PairingTimeLeft.TotalMinutes):0} more minutes):");
                CodeRow($"{RemoteAccess.Address}  {RemoteAccess.Code}");
                if (GUILayout.Button("Cancel", GUILayout.Width(120))) RemoteAccess.Stop("cancelled");
                break;

            case RemoteMode.Session:
                GUILayout.Label(RemoteAccess.ConnectedName != null
                    ? $"<b>ON</b>: {RemoteAccess.ConnectedName} is connected" + LastRequest()
                    : "<b>ON</b>: waiting for a paired debugger. Give them this session code (a new one every time):");
                CodeRow(RemoteAccess.Code);
                GUILayout.Label($"Turns off when the game closes, after {RemoteAccess.IdleLimit.TotalMinutes:0} minutes unused, or after 5 failed attempts.");
                if (GUILayout.Button("Stop remote debugging", GUILayout.Width(200))) RemoteAccess.Stop("stopped by you");
                break;
        }

        var pairings = RemoteAccess.Pairings;
        if (pairings.Count > 0)
        {
            GUILayout.Space(6);
            GUILayout.Label("Paired debuggers:");
            foreach (Pairing p in pairings)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{p.Name} from {p.Peer}, paired {p.CreatedUtc.ToLocalTime():d MMM HH:mm}" +
                                (p.LastUsedUtc is DateTime used ? $", last used {used.ToLocalTime():d MMM HH:mm}" : string.Empty),
                    GUILayout.ExpandWidth(false));
                if (GUILayout.Button("Remove", GUILayout.Width(80))) RemoteAccess.RemovePairing(p.Id);
                GUILayout.EndHorizontal();
            }
        }

        var log = RemoteAccess.Activity;
        if (log.Count > 0)
        {
            GUILayout.Space(6);
            GUILayout.Label("Recent activity (everything is also in remote-audit.log in this mod's folder):");
            foreach (string line in log) GUILayout.Label("  " + line);
        }
    }

    private static void CodeRow(string text)
    {
        GUILayout.BeginHorizontal();
        GUILayout.TextField(text, GUILayout.Width(360));
        if (GUILayout.Button("Copy", GUILayout.Width(70))) GUIUtility.systemCopyBuffer = text;
        GUILayout.EndHorizontal();
    }

    private static string LastRequest() =>
        RemoteAccess.LastRequestUtc is DateTime t ? $", last request {(DateTime.UtcNow - t).TotalSeconds:0}s ago" : string.Empty;
}

/// <summary>
/// A banner in the game window while remote debugging is on, and the clock that expires it.
/// The banner is drawn on the desktop window only; a VR headset doesn't show it.
/// </summary>
internal sealed class RemoteIndicator : MonoBehaviour
{
    private GUIStyle style;

    private void Update() => RemoteAccess.Tick();

    private void OnGUI()
    {
        if (RemoteAccess.Mode == RemoteMode.Off) return;
        style ??= new GUIStyle(GUI.skin.box)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white, background = Solid(new Color(0.6f, 0.05f, 0.05f, 0.85f)) }
        };
        string text = RemoteAccess.Mode == RemoteMode.Pairing
            ? "Remote debugging: waiting to pair (Ctrl+F10 to cancel)"
            : RemoteAccess.ConnectedName != null
                ? $"Remote debugging ON: {RemoteAccess.ConnectedName} can run code in this game (Ctrl+F10 to stop)"
                : "Remote debugging ON: waiting for a paired debugger (Ctrl+F10 to stop)";
        GUI.Box(new Rect(10, 10, 560, 28), "  " + text, style);
    }

    private static Texture2D Solid(Color color)
    {
        Texture2D t = new(1, 1);
        t.SetPixel(0, 0, color);
        t.Apply();
        return t;
    }
}
