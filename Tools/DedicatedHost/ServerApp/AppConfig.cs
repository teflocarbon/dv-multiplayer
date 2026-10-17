using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace DvmpServer;

/// <summary>
/// The app's own setup: what the watchdog script takes as parameters. The Multiplayer mod's server
/// settings (name, password, admins) live in the server's copy of the mod instead
/// (<see cref="ServerSettings"/>), and which mods it runs is its Mods folder (<see cref="ServerMods"/>).
/// </summary>
public class AppConfig
{
    /// <summary>
    /// Heartbeat, logs and the watchdog log, shared with run-dedicated-host.ps1 (its -DataDir).
    /// DVMP_SERVER_APP_DATA moves it, for trying the app without touching the real server.
    /// </summary>
    public static readonly string DataDir =
        Environment.GetEnvironmentVariable("DVMP_SERVER_APP_DATA") is string dir && dir.Length > 0
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dv-multiplayer", "dedicated-host");

    /// <summary>The server's own folder, with its own Mods (run-dedicated-host.ps1's -ServerDir default).</summary>
    public static readonly string ServerDir = Path.Combine(DataDir, "server");

    private static readonly string ConfigPath = Path.Combine(DataDir, "server-app.xml");

    public string GameDir = "";
    /// <summary>A session name, a save name or UID, or "continue" (-mp-save).</summary>
    public string Save = "";
    public int AutosaveMinutes = 10;
    public int Fps = 30;
    /// <summary>"HH:mm", or empty for none.</summary>
    public string DailyRestart = "";
    /// <summary>Show the game's window instead of starting it minimised.</summary>
    public bool ShowGameWindow;
    /// <summary>Show the mod's debug lines in the log view (they're in the game's log either way).</summary>
    public bool ShowDebugLines;
    public int StaleSeconds = 120;
    public int StartupSeconds = 600;

    public static AppConfig Load()
    {
        AppConfig config = null;
        try
        {
            if (File.Exists(ConfigPath))
                using (var reader = File.OpenRead(ConfigPath))
                    config = (AppConfig)new XmlSerializer(typeof(AppConfig)).Deserialize(reader);
        }
        catch (Exception) { config = null; }
        config ??= new AppConfig();
        if (!IsGameDir(config.GameDir))
            config.GameDir = FindGameDir() ?? config.GameDir ?? "";
        return config;
    }

    public void Write()
    {
        Directory.CreateDirectory(DataDir);
        string temp = ConfigPath + ".tmp";
        using (var writer = File.Create(temp))
            new XmlSerializer(typeof(AppConfig)).Serialize(writer, this);
        if (File.Exists(ConfigPath))
            File.Replace(temp, ConfigPath, null);
        else
            File.Move(temp, ConfigPath);
    }

    public static bool IsGameDir(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "DerailValley.exe"));

    /// <summary>
    /// The game's folder: this exe's when it's in the game's Mods folder (as shipped, in the mod's
    /// folder), else Steam's default library, else any Steam library that has it.
    /// </summary>
    public static string FindGameDir()
    {
        try
        {
            string here = AppDomain.CurrentDomain.BaseDirectory;
            string fromHere = Path.GetDirectoryName(Path.GetDirectoryName(here.TrimEnd('\\', '/')));
            if (IsGameDir(fromHere))
                return fromHere;
        }
        catch (Exception) { }

        string steam = null;
        try { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; }
        catch (Exception) { }
        steam ??= @"C:\Program Files (x86)\Steam";
        string candidate = Path.Combine(steam, "steamapps", "common", "Derail Valley");
        if (IsGameDir(candidate))
            return Path.GetFullPath(candidate);
        try
        {
            string libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraries))
                foreach (Match m in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s*\"([^\"]+)\""))
                {
                    candidate = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", "Derail Valley");
                    if (IsGameDir(candidate))
                        return candidate;
                }
        }
        catch (Exception) { }
        return null;
    }
}
