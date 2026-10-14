using System.Text.Json;

namespace Dvmp;

internal static class Paths
{
    private const string DefaultGameDir = @"C:\Program Files (x86)\Steam\steamapps\common\Derail Valley";

    public static string GameDir => Environment.GetEnvironmentVariable("DVMP_DV_DIR") is { Length: > 0 } dir ? dir : DefaultGameDir;
    public static string GameExe => Path.Combine(GameDir, "DerailValley.exe");
    public static string Managed => Path.Combine(GameDir, "DerailValley_Data", "Managed");
    public static string ModsDir => Path.Combine(GameDir, "Mods");
    public static string DevToolsModDir => Path.Combine(ModsDir, "MultiplayerDevTools");

    /// <summary>Probes/ next to the dvmp project when running from a repo build: probes and their
    /// libraries are then read from source on every call, so edits need no rebuild of dvmp.</summary>
    public static string? ProbeSourceDir
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string probes = Path.Combine(dir.FullName, "Probes");
                if (File.Exists(Path.Combine(dir.FullName, "Dvmp.csproj")) && Directory.Exists(probes))
                    return probes;
            }
            return null;
        }
    }

    /// <summary>The repo's Debug build output of the mod, when dvmp runs from a repo build.</summary>
    public static string? ModBuildDir
    {
        get
        {
            if (ProbeSourceDir is not { } probes) return null;
            string dir = Path.GetFullPath(Path.Combine(probes, "..", "..", "..", "Multiplayer", "bin", "Debug", "net48"));
            return File.Exists(Path.Combine(dir, "Multiplayer.dll")) ? dir : null;
        }
    }

    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dvmp");
    public static string AgentsDir => Path.Combine(DataRoot, "agents");
    public static string CacheDir => Path.Combine(DataRoot, "cache");

    /// <summary>
    /// Unity Mod Manager folders are user-named (e.g. "Multiplayer 0.1.16.13"), so the
    /// installed Multiplayer mod is found by the Id in its info.json.
    /// </summary>
    public static string? MultiplayerModDir()
    {
        if (!Directory.Exists(ModsDir)) return null;
        foreach (string dir in Directory.GetDirectories(ModsDir))
        {
            string info = Path.Combine(dir, "info.json");
            if (!File.Exists(info)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(info));
                if (doc.RootElement.TryGetProperty("Id", out JsonElement id) && id.GetString() == "Multiplayer")
                    return dir;
            }
            catch (JsonException) { }
        }
        return null;
    }
}
