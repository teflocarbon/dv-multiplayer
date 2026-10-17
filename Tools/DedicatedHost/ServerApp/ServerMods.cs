using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DvmpServer;

/// <summary>A mod folder with Unity Mod Manager's info.json.</summary>
public class ModInfo
{
    public string Id = "";
    public string DisplayName = "";
    public string Version = "";
    /// <summary>info.json's MultiplayerCompatibility, else the Multiplayer mod's built-in entry, else "Undefined".</summary>
    public string Compatibility = "Undefined";
    /// <summary>Mod IDs it requires, without any "-version" suffix.</summary>
    public string[] Requirements = new string[0];
    public string Folder = "";

    public static ModInfo Read(string folder)
    {
        // Unity Mod Manager reads info.json from the top of the mod's folder only.
        string path = Directory.GetFiles(folder, "info.json", SearchOption.TopDirectoryOnly).FirstOrDefault();
        Json json = path == null ? null : Json.Parse(path);
        string id = json?.GetString("Id");
        if (string.IsNullOrWhiteSpace(id))
            return null;
        string compatibility = json.GetString("MultiplayerCompatibility");
        if (string.IsNullOrEmpty(compatibility) && !BuiltInCompatibility.TryGetValue(id, out compatibility))
            compatibility = "Undefined";
        return new ModInfo
        {
            Id = id,
            DisplayName = json.GetString("DisplayName") ?? id,
            Version = json.GetString("Version") ?? "",
            Compatibility = compatibility,
            Requirements = json.GetStrings("Requirements").Select(RequirementId).Where(r => r.Length > 0).ToArray(),
            Folder = folder,
        };
    }

    // "DVLangHelper-1.2.1" requires DVLangHelper at 1.2.1 or later.
    private static string RequirementId(string requirement)
    {
        Match m = Regex.Match(requirement.Trim(), @"^(.+?)-\d+(\.\d+)*$");
        return m.Success ? m.Groups[1].Value : requirement.Trim();
    }

    /// <summary>What the compatibility means for a server, in a few words.</summary>
    public string CompatibilityMeaning => Compatibility switch
    {
        "All" => "players need it too",
        "Host" => "server only",
        "Client" => "not needed on the server",
        "Incompatible" => "doesn't work with Multiplayer",
        _ => "not declared: players need it too",
    };

    // Multiplayer's own entries for mods that don't say (ModCompatibilityManager.Awake).
    private static readonly Dictionary<string, string> BuiltInCompatibility = new()
    {
        ["Multiplayer"] = "All",
        ["RuntimeUnityEditor"] = "Client",
        ["BookletOrganizer"] = "Host",
        ["RemoteDispatch"] = "Client",
        ["CommsRadioAPI"] = "Client",
        ["DVCustomCargo"] = "All",
        ["DVDiscordPresenceMod"] = "Client",
        ["DVLangHelper"] = "Client",
        ["LightingOverhaul"] = "Client",
        ["dv-improved-job-overview"] = "Client",
        ["dv_f_spammer"] = "Client",
        ["MessageBox"] = "Client",
        ["BetterModUI"] = "Client",
        ["MultiplayerDevTools"] = "Client",
    };
}

/// <summary>
/// The server's own Mods folder, the same way run-dedicated-host.ps1's Initialize-ServerDir keeps it:
/// a mod is on the server when its folder is in the server's Mods. Each start refreshes every one of
/// them from the game's copy (matched by mod ID) except its Settings.xml. A mod turned off here moves
/// to the server's "Mods (off)" folder, which keeps its settings for when it's turned back on.
/// </summary>
public static class ServerMods
{
    public const string MultiplayerId = "Multiplayer";

    public static string ModsDir(string serverDir) => Path.Combine(serverDir, "Mods");
    public static string OffDir(string serverDir) => Path.Combine(serverDir, "Mods (off)");

    public static bool Exists(string serverDir) => Directory.Exists(ModsDir(serverDir));

    /// <summary>Mods in a folder by ID (the first folder of an ID wins, as Unity Mod Manager's does).</summary>
    public static Dictionary<string, ModInfo> Scan(string modsDir)
    {
        var mods = new Dictionary<string, ModInfo>(StringComparer.Ordinal);
        if (!Directory.Exists(modsDir))
            return mods;
        foreach (string dir in Directory.GetDirectories(modsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            ModInfo mod;
            try { mod = ModInfo.Read(dir); }
            catch (Exception) { continue; }
            if (mod != null && !mods.ContainsKey(mod.Id))
                mods[mod.Id] = mod;
        }
        return mods;
    }

    /// <summary>The server's copy of Multiplayer's Settings.xml, or null without one.</summary>
    public static string MultiplayerSettingsPath(string serverDir)
    {
        ModInfo mp = Scan(ModsDir(serverDir)).TryGetValue(MultiplayerId, out ModInfo m) ? m : null;
        return mp == null ? null : Path.Combine(mp.Folder, "Settings.xml");
    }

    /// <summary>
    /// Makes the server's Mods hold exactly the mods in <paramref name="enabled"/>: turned-off ones
    /// move to "Mods (off)"; turned-on ones come back from there, or are copied from the game's with
    /// their settings. Multiplayer is always on.
    /// </summary>
    public static void Apply(string gameDir, string serverDir, ISet<string> enabled, Action<string> say)
    {
        string mods = ModsDir(serverDir);
        string off = OffDir(serverDir);
        Directory.CreateDirectory(mods);
        WriteSteamAppId(serverDir);
        Dictionary<string, ModInfo> game = Scan(Path.Combine(gameDir, "Mods"));
        Dictionary<string, ModInfo> server = Scan(mods);
        Dictionary<string, ModInfo> parked = Scan(off);

        foreach (ModInfo mod in server.Values.Where(m => m.Id != MultiplayerId && !enabled.Contains(m.Id)))
        {
            Directory.CreateDirectory(off);
            string to = Path.Combine(off, Path.GetFileName(mod.Folder));
            if (Directory.Exists(to))
                DeleteDirectory(to);
            Directory.Move(mod.Folder, to);
            say($"turned off {mod.Id} on the server (kept in {off})");
        }
        foreach (string id in enabled.Concat(new[] { MultiplayerId }).Distinct())
        {
            if (server.ContainsKey(id))
                continue;
            if (parked.TryGetValue(id, out ModInfo back))
            {
                string to = Path.Combine(mods, Path.GetFileName(back.Folder));
                if (!Directory.Exists(to))
                {
                    Directory.Move(back.Folder, to);
                    say($"turned on {id} on the server");
                    continue;
                }
            }
            if (game.TryGetValue(id, out ModInfo source))
            {
                string to = Path.Combine(mods, Path.GetFileName(source.Folder));
                Sync(source.Folder, to);
                string settings = Path.Combine(source.Folder, "Settings.xml");
                if (File.Exists(settings))
                    File.Copy(settings, Path.Combine(to, "Settings.xml"), true);
                say($"copied {id} from your game to the server, with its settings");
            }
        }
    }

    /// <summary>
    /// Before each start: refreshes every server mod from the game's copy by ID, except its
    /// Settings.xml, so versions match what players have (Initialize-ServerDir).
    /// </summary>
    public static void Refresh(string gameDir, string serverDir, Action<string> say)
    {
        WriteSteamAppId(serverDir);
        Dictionary<string, ModInfo> game = Scan(Path.Combine(gameDir, "Mods"));
        Dictionary<string, ModInfo> server = Scan(ModsDir(serverDir));
        foreach (ModInfo mod in server.Values)
        {
            if (!game.TryGetValue(mod.Id, out ModInfo source))
            {
                say($"the server has mod {mod.Id}, which your game doesn't; keeping it as it is");
                continue;
            }
            int copied = Sync(source.Folder, mod.Folder);
            if (copied > 0)
                say($"updated the server's {mod.Id} from your game ({copied} file(s))");
        }
        if (!server.ContainsKey(MultiplayerId))
            throw new InvalidOperationException($"the server's Mods folder ({ModsDir(serverDir)}) has no Multiplayer mod: turn it on in Setup");
        say("server mods: " + string.Join(", ", server.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>Mods the enabled ones require that aren't enabled themselves.</summary>
    public static List<string> MissingRequirements(IEnumerable<ModInfo> enabled)
    {
        var ids = new HashSet<string>(enabled.Select(m => m.Id));
        return enabled.SelectMany(m => m.Requirements.Where(r => !ids.Contains(r)).Select(r => $"{m.Id} needs {r}")).ToList();
    }

    // Steam's app ID, for a game started outside Steam's own folder.
    private static void WriteSteamAppId(string serverDir)
    {
        Directory.CreateDirectory(serverDir);
        File.WriteAllText(Path.Combine(serverDir, "steam_appid.txt"), "588030");
    }

    /// <summary>
    /// Files of one mod folder into another, except its settings; only what changed. robocopy, as
    /// some mods' files sit deeper than Windows' 260-character path limit allows. Returns how many
    /// files it copied.
    /// </summary>
    private static int Sync(string from, string to)
    {
        var start = new ProcessStartInfo("robocopy.exe",
            $"\"{from.TrimEnd('\\')}\" \"{to.TrimEnd('\\')}\" /E /XF Settings.xml /R:1 /W:1 /NJH /NFL /NDL /NP")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using Process robocopy = Process.Start(start);
        string output = robocopy.StandardOutput.ReadToEnd();
        robocopy.WaitForExit();
        if (robocopy.ExitCode >= 8)
            throw new IOException($"copying {from} to {to} failed (robocopy {robocopy.ExitCode}): {output.Trim()}");
        Match files = Regex.Match(output, @"^\s*Files\s*:\s*\d+\s+(\d+)", RegexOptions.Multiline);
        return files.Success ? int.Parse(files.Groups[1].Value) : 0;
    }

    // robocopy again: Directory.Delete fails on paths past 260 characters.
    private static void DeleteDirectory(string dir)
    {
        string empty = Path.Combine(Path.GetTempPath(), "dvmp-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var start = new ProcessStartInfo("robocopy.exe", $"\"{empty}\" \"{dir.TrimEnd('\\')}\" /MIR /R:1 /W:1 /NJH /NJS /NFL /NDL /NP")
            { UseShellExecute = false, CreateNoWindow = true };
            using (Process p = Process.Start(start))
                p.WaitForExit();
            Directory.Delete(dir, true);
        }
        finally { Directory.Delete(empty, true); }
    }
}
