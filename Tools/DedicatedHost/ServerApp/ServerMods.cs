using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
/// The server's own mods, kept the way run-dedicated-host.ps1's Initialize-ServerDir and
/// Initialize-ServerUmm keep them. The server has its own copy of Unity Mod Manager
/// (Managed\UnityModManager), which the game loads instead of its own (Watchdog.StartGame). Its
/// Config.xml points at the server's Mods, and its Params.xml says which of them are enabled. A mod
/// is on the server when its folder is in the server's Mods and Params.xml doesn't disable it. Each
/// start refreshes every one of them from the game's copy (matched by mod ID) except its Settings.xml.
/// </summary>
public static class ServerMods
{
    public const string MultiplayerId = "Multiplayer";

    public static string ModsDir(string serverDir) => Path.Combine(serverDir, "Mods");

    /// <summary>UMM checks that it sits in a Managed\UnityModManager folder, so its copy does too.</summary>
    public static string UmmDir(string serverDir) => Path.Combine(serverDir, "Managed", "UnityModManager");

    /// <summary>What the game's Doorstop loads in place of the game's own UMM (--doorstop-target-assembly).</summary>
    public static string UmmAssembly(string serverDir) => Path.Combine(UmmDir(serverDir), "UnityModManager.dll");

    private static string ParamsPath(string serverDir) => Path.Combine(UmmDir(serverDir), "Params.xml");

    public static bool Exists(string serverDir) => Directory.Exists(ModsDir(serverDir));

    /// <summary>
    /// The server's copy of UMM: the game's, without its Config.xml, Params.xml and log. Config.xml
    /// gets the server's Mods as its ModsDirectory; Params.xml starts as a copy of the game's and is
    /// the server's own from then on.
    /// </summary>
    public static void InitializeUmm(string gameDir, string serverDir)
    {
        string gameUmm = Path.Combine(gameDir, "DerailValley_Data", "Managed", "UnityModManager");
        string serverUmm = UmmDir(serverDir);
        Directory.CreateDirectory(serverUmm);
        RunRobocopy(gameUmm, serverUmm, "/E /XF Config.xml Params.xml Log.txt");
        XDocument config = XDocument.Load(Path.Combine(gameUmm, "Config.xml"));
        XElement modsDirectory = config.Root.Element("ModsDirectory");
        if (modsDirectory == null)
            config.Root.Add(modsDirectory = new XElement("ModsDirectory"));
        modsDirectory.Value = ModsDir(serverDir);
        config.Save(Path.Combine(serverUmm, "Config.xml"));
        string gameParams = Path.Combine(gameUmm, "Params.xml");
        if (!File.Exists(ParamsPath(serverDir)) && File.Exists(gameParams))
            File.Copy(gameParams, ParamsPath(serverDir));
    }

    /// <summary>Mod IDs the server's Params.xml disables (UMM enables any mod it has no entry for).</summary>
    public static HashSet<string> Disabled(string serverDir)
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(ParamsPath(serverDir)))
                foreach (XElement mod in XDocument.Load(ParamsPath(serverDir)).Root?.Element("ModParams")?.Elements("Mod") ?? Enumerable.Empty<XElement>())
                    if (string.Equals((string)mod.Attribute("Enabled"), "false", StringComparison.OrdinalIgnoreCase))
                        disabled.Add((string)mod.Attribute("Id") ?? "");
        }
        catch (Exception) { }
        return disabled;
    }

    // Each mod's entry in the server's Params.xml, as UMM writes them.
    private static void SetEnabled(string serverDir, IDictionary<string, bool> enabled)
    {
        string path = ParamsPath(serverDir);
        XDocument doc = File.Exists(path) ? XDocument.Load(path) : null;
        if (doc?.Root == null)
            doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Param"));
        XElement modParams = doc.Root.Element("ModParams");
        if (modParams == null)
            doc.Root.Add(modParams = new XElement("ModParams"));
        foreach (var pair in enabled)
        {
            XElement mod = modParams.Elements("Mod").FirstOrDefault(m => (string)m.Attribute("Id") == pair.Key);
            if (mod == null)
                modParams.Add(mod = new XElement("Mod", new XAttribute("Id", pair.Key), new XAttribute("Enabled", "true"),
                    new XElement("Hotkey", new XElement("keyCode", "None"), new XElement("modifiers", "0"))));
            mod.SetAttributeValue("Enabled", pair.Value ? "true" : "false");
        }
        doc.Save(path);
    }

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
    /// Turns on exactly the mods in <paramref name="enabled"/> on the server, in its own Params.xml:
    /// ones it doesn't have yet are copied from the game's, with their settings. Turned-off ones stay
    /// in its Mods, disabled, with their settings. Multiplayer is always on.
    /// </summary>
    public static void Apply(string gameDir, string serverDir, ISet<string> enabled, Action<string> say)
    {
        string mods = ModsDir(serverDir);
        Directory.CreateDirectory(mods);
        InitializeUmm(gameDir, serverDir);
        Dictionary<string, ModInfo> game = Scan(Path.Combine(gameDir, "Mods"));
        Dictionary<string, ModInfo> server = Scan(mods);
        HashSet<string> wasDisabled = Disabled(serverDir);
        var on = new HashSet<string>(enabled) { MultiplayerId };

        foreach (string id in on)
        {
            if (server.ContainsKey(id))
            {
                if (wasDisabled.Contains(id))
                    say($"turned on {id} on the server");
                continue;
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
        var flags = new Dictionary<string, bool>();
        foreach (string id in Scan(mods).Keys)
        {
            flags[id] = on.Contains(id);
            if (!flags[id] && !wasDisabled.Contains(id))
                say($"turned off {id} on the server (its folder and settings stay)");
        }
        SetEnabled(serverDir, flags);
    }

    /// <summary>
    /// Before each start: refreshes every server mod from the game's copy by ID, except its
    /// Settings.xml, so versions match what players have, and the server's UMM from the game's
    /// (Initialize-ServerDir).
    /// </summary>
    public static void Refresh(string gameDir, string serverDir, Action<string> say)
    {
        InitializeUmm(gameDir, serverDir);
        Dictionary<string, ModInfo> game = Scan(Path.Combine(gameDir, "Mods"));
        Dictionary<string, ModInfo> server = Scan(ModsDir(serverDir));
        HashSet<string> disabled = Disabled(serverDir);
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
            throw new InvalidOperationException($"the server's Mods folder ({ModsDir(serverDir)}) has no Multiplayer mod: Save the setup again");
        if (disabled.Contains(MultiplayerId))
        {
            SetEnabled(serverDir, new Dictionary<string, bool> { [MultiplayerId] = true });
            disabled.Remove(MultiplayerId);
        }
        say("server mods: " + string.Join(", ", server.Keys.Where(id => !disabled.Contains(id)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) +
            (disabled.Count > 0 ? "; turned off: " + string.Join(", ", disabled.Where(server.ContainsKey).OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) : ""));
    }

    /// <summary>Mods the enabled ones require that aren't enabled themselves.</summary>
    public static List<string> MissingRequirements(IEnumerable<ModInfo> enabled)
    {
        var ids = new HashSet<string>(enabled.Select(m => m.Id));
        return enabled.SelectMany(m => m.Requirements.Where(r => !ids.Contains(r)).Select(r => $"{m.Id} needs {r}")).ToList();
    }

    /// <summary>
    /// Files of one mod folder into another, except its settings; only what changed. Returns how
    /// many files it copied.
    /// </summary>
    private static int Sync(string from, string to)
    {
        string output = RunRobocopy(from, to, "/E /XF Settings.xml");
        Match files = Regex.Match(output, @"^\s*Files\s*:\s*\d+\s+(\d+)", RegexOptions.Multiline);
        return files.Success ? int.Parse(files.Groups[1].Value) : 0;
    }

    // robocopy, as some mods' files sit deeper than Windows' 260-character path limit allows.
    private static string RunRobocopy(string from, string to, string options)
    {
        var start = new ProcessStartInfo("robocopy.exe",
            $"\"{from.TrimEnd('\\')}\" \"{to.TrimEnd('\\')}\" {options} /R:1 /W:1 /NJH /NFL /NDL /NP")
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
        return output;
    }
}
