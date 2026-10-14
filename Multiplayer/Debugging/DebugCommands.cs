using CommandTerminal;
using Multiplayer.Debugging.Protocol;
using System;
using System.IO;
using System.Linq;

namespace Multiplayer.Debugging;

public static class DebugCommands
{
    [RegisterCommand("debug", Help = "Multiplayer debug: status|sessions|labels|select|dump|copy|settings|verbose|mark|capture|auto|raw|trace|clear", MinArgCount = 0, MaxArgCount = 32)]
    public static void Execute(CommandArg[] args)
    {
        string op = args.Length == 0 ? "status" : args[0].String.ToLowerInvariant();
        switch (op)
        {
            case "status":
                Log(DebugRuntime.Enabled ? DebugJson.Serialize(DebugRuntime.Session) : "Debug system disabled.");
                break;
            case "sessions":
                string directory = Path.Combine(Multiplayer.ModEntry.Path, "Multiplayer.Debug", "sessions");
                Log(Directory.Exists(directory) ? string.Join("\n", Directory.GetFiles(directory, "*.json")) : "No session directory.");
                break;
            case "labels": HandleLabels(args.Skip(1).ToArray()); break;
            case "select":
                if (args.Length >= 2 && args[1].String.Equals("look", StringComparison.OrdinalIgnoreCase)) Log(DebugOverlayController.SelectLookedAt() ? "Selected looked-at entity." : "No network entity found in view.");
                else if (args.Length >= 3) DebugOverlayController.Select(NormalizeType(args[1].String), args[2].String);
                else Log("Usage: debug select look|item|player|car <id>");
                break;
            case "dump":
                Log(DebugOverlayController.SelectedJson());
                break;
            case "copy":
                string copyTarget = args.Length > 1 ? args[1].String.ToLowerInvariant() : "entity";
                if (copyTarget == "mode")
                {
                    DebugOverlayController.ToggleCopyMode();
                    Log($"Copy mode: {(DebugOverlayController.SummaryCopyMode ? "summary" : "full")}");
                    break;
                }
                bool copied = copyTarget switch
                {
                    "timeline" or "events" => DebugOverlayController.CopyTimeline(),
                    "last" or "event" => DebugOverlayController.CopyLastEvent(),
                    "flow" or "replication" => DebugOverlayController.CopyReplicationFlow(),
                    "matrix" or "interest" => DebugOverlayController.CopyInterestMatrix(),
                    "tree" or "components" or "hierarchy" => DebugOverlayController.CopyComponentHierarchy(),
                    "bundle" or "diagnostics" => DebugOverlayController.CopyDiagnosticBundle(),
                    _ => DebugOverlayController.CopySelected()
                };
                Log(copied ? $"Copied {copyTarget} to clipboard." : $"Nothing available for {copyTarget}.");
                break;
            case "settings": DebugOverlayController.ToggleSettings(); break;
            case "verbose":
                DebugOverlayController.ToggleVerbose();
                Log("Verbose overlay toggled.");
                break;
            case "mark": DebugRuntime.Mark(string.Join(" ", args.Skip(1).Select(arg => arg.String))); break;
            case "capture": HandleCapture(args.Skip(1).ToArray()); break;
            case "auto":
                if (args.Length >= 2 && TryToggle(args[1].String, out bool automatic)) DebugDiagnostics.AutomaticCapturesEnabled = automatic;
                Log($"Automatic diagnostic captures: {DebugDiagnostics.AutomaticCapturesEnabled}");
                break;
            case "raw":
                if (args.Length >= 2 && TryToggle(args[1].String, out bool raw))
                {
                    DebugRuntime.RuntimeSettings.RawPacketCapture = raw;
                    DebugRuntime.RuntimeSettings.TraceMode = raw ? DebugTraceMode.Raw : DebugTraceMode.Summary;
                    Log($"Raw capture: {raw}");
                }
                break;
            case "trace":
                if (args.Length >= 3 && args[1].String.Equals("item", StringComparison.OrdinalIgnoreCase)) EntityDebugRegistry.Trace("Item", args[2].String, true);
                else if (args.Length >= 3 && args[1].String.Equals("packet", StringComparison.OrdinalIgnoreCase)) DebugTrace.TracePacket(args[2].String, true);
                else Log("Usage: debug trace item <id>|packet <packet-type>");
                break;
            case "clear": DebugRuntime.Clear(); break;
            default: Log("Unknown debug command."); break;
        }
    }

    private static void HandleLabels(CommandArg[] args)
    {
        if (args.Length == 0) { Log($"Labels: {DebugWorldLabelManager.Enabled}"); return; }
        if (args[0].String.Equals("on", StringComparison.OrdinalIgnoreCase)) DebugWorldLabelManager.SetEnabled(true);
        else if (args[0].String.Equals("off", StringComparison.OrdinalIgnoreCase)) DebugWorldLabelManager.SetEnabled(false);
        else if (args[0].String.Equals("radius", StringComparison.OrdinalIgnoreCase) && args.Length > 1 && float.TryParse(args[1].String, out float radius)) Multiplayer.Settings.DebugWorldLabelRadius = Math.Max(5, Math.Min(500, radius));
        else if (args[0].String.Equals("errors-only", StringComparison.OrdinalIgnoreCase)) DebugWorldLabelManager.SetErrorsOnly(true);
        else if (args[0].String.Equals("netid", StringComparison.OrdinalIgnoreCase) && args.Length > 1) DebugWorldLabelManager.SetNetId(args[1].String);
        else if (args.Length > 1 && TryToggle(args[1].String, out bool typeEnabled)) DebugWorldLabelManager.SetType(args[0].String, typeEnabled);
        else Log("Usage: debug labels on|off|items|players|trains on|off|radius <metres>|errors-only|netid <id>");
    }

    private static void HandleCapture(CommandArg[] args)
    {
        if (args.Length > 0 && args[0].String.Equals("start", StringComparison.OrdinalIgnoreCase)) Log(DebugRuntime.StartCapture(args.Length > 1 ? args[1].String : "capture"));
        else if (args.Length > 0 && args[0].String.Equals("stop", StringComparison.OrdinalIgnoreCase)) Log(DebugRuntime.StopCapture());
        else Log("Usage: debug capture start <name>|stop");
    }

    private static string NormalizeType(string value) => value.ToLowerInvariant() switch { "item" => "Item", "player" => "Player", "car" => "TrainCar", _ => value };
    private static bool TryToggle(string value, out bool enabled)
    {
        if (value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase)) { enabled = true; return true; }
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)) { enabled = false; return true; }
        enabled = false; return false;
    }
    private static void Log(string value) => Terminal.Log("{0}", new object[] { value ?? string.Empty });
}
