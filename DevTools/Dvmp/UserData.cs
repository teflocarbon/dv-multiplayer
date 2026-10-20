using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dvmp;

/// <summary>
/// A named session's own copy of DV's user data (profiles, preferences, control bindings, careers
/// and saves), one folder per game: sessions\NAME\data\host and \client. A game gets its folder
/// through -dvmp-userdata, which the dev agent applies to DV's storage before DV creates it, so
/// parallel pairs never write each other's saves or the real profile
/// (docs/design/parallel-test-sessions.md). The default session keeps the real profile.
///
/// A folder is seeded from the real profile, which is only read, when it doesn't exist yet or on
/// `up --reseed`. It is kept across `down` and `up` after that: save-reload and inventory-persist
/// restart the games and load what they saved.
/// </summary>
internal static class UserData
{
    /// <summary>Saves copied per career, the newest; DV numbers new ones after them.</summary>
    private const int SavesKept = 5;

    /// <summary>DV's real user data folder (Application.persistentDataPath), only ever read here.</summary>
    public static string RealProfile => Environment.GetEnvironmentVariable("DVMP_DV_PROFILE") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Altfuture", "Derail Valley");

    /// <summary>The current session's folder for a game of this role; null for the default session,
    /// or with DVMP_SHARED_PROFILE=1 (every game on the real profile, as before; save-writing gate
    /// runs then need to run alone).</summary>
    public static string? Dir(string role) =>
        Sessions.IsDefault || Environment.GetEnvironmentVariable("DVMP_SHARED_PROFILE") == "1"
            ? null
            : Path.Combine(Sessions.Dir(Sessions.Current), "data", role);

    /// <summary>The folder for a game about to launch, seeded first if it doesn't exist.</summary>
    public static string? Prepare(string role)
    {
        if (Dir(role) is not { } dir) return null;
        if (!Directory.Exists(dir)) Seed(dir);
        return dir;
    }

    /// <summary>Drops the current session's folders so the next launch seeds them again. Only
    /// with the session's games down.</summary>
    public static void Reset()
    {
        foreach (string role in new[] { "host", "client" })
            if (Dir(role) is { } dir && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    /// <summary>Whether two folder paths name the same folder.</summary>
    public static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // Copies what a harness game needs: global.json, each user's userData.json (selected sessions,
    // encryption signature), control bindings and GameData, the preferences, and the careers and
    // sandboxes the harness plays: those named "DVMP..." and each mode's selected one (the save the
    // launcher shows), with their newest saves. Built in a staging folder and moved into place, so
    // an interrupted seed is never taken for a finished one.
    private static void Seed(string dir)
    {
        string from = RealProfile;
        string staging = dir + ".seeding";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        CopyFile(from, staging, Path.Combine("Users", "global.json"));
        string preferences = Path.Combine(from, "Preferences");
        if (Directory.Exists(preferences))
            foreach (string ini in Directory.GetFiles(preferences, "*.ini"))
                CopyFile(from, staging, Path.GetRelativePath(from, ini));

        string users = Path.Combine(from, "Users");
        if (Directory.Exists(users))
        {
            foreach (string user in Directory.GetDirectories(users))
            {
                string rel = Path.GetRelativePath(from, user);
                CopyFile(from, staging, Path.Combine(rel, "userData.json"));
                CopyFile(from, staging, Path.Combine(rel, "ControlBindings.json"));
                CopyTree(from, staging, Path.Combine(rel, "GameData"));
                Dictionary<string, int> selected = SelectedSessions(Path.Combine(user, "userData.json"));
                foreach (string mode in Directory.GetDirectories(user))
                {
                    if (Path.GetFileName(mode) is "GameData" or "ImportSave") continue;
                    foreach (string session in Directory.GetDirectories(mode))
                        if (Wanted(session, selected))
                            CopySession(from, staging, Path.GetRelativePath(from, session));
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        Directory.Move(staging, dir);
    }

    // userData.json's selectedSessionUID: game mode -> session ID.
    private static Dictionary<string, int> SelectedSessions(string userData)
    {
        Dictionary<string, int> selected = new();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(userData))?["selectedSessionUID"] is JsonObject uids)
                foreach ((string mode, JsonNode? id) in uids)
                    if (id is JsonValue v && v.TryGetValue(out int n)) selected[mode] = n;
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return selected;
    }

    private static bool Wanted(string session, Dictionary<string, int> selected)
    {
        try
        {
            JsonNode? data = JsonNode.Parse(File.ReadAllText(Path.Combine(session, "sessionData.json")));
            if (data?["Name"]?.GetValue<string>() is { } name && name.StartsWith("DVMP", StringComparison.OrdinalIgnoreCase))
                return true;
            return data?["GameMode"]?.GetValue<string>() is { } mode && selected.TryGetValue(mode, out int id)
                   && data["SessionID"]?.GetValue<int>() == id;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    // A career or sandbox: its sessionData.json, its newest saves and DV's lastLoaded backup; not
    // a dedicated host's backups or its lock.
    private static void CopySession(string from, string to, string session)
    {
        CopyFile(from, to, Path.Combine(session, "sessionData.json"));
        string saves = Path.Combine(from, session, "Saves");
        if (!Directory.Exists(saves)) return;
        Directory.CreateDirectory(Path.Combine(to, session, "Saves"));
        // By time: DV reuses save numbers (its autosaves rotate through slots).
        IEnumerable<string> newest = Directory.GetFiles(saves, "*.save").OrderByDescending(File.GetLastWriteTimeUtc).Take(SavesKept);
        foreach (string file in newest.Concat(Directory.GetFiles(saves, "lastLoaded_*.bak")))
            CopyFile(from, to, Path.GetRelativePath(from, file));
    }

    private static void CopyTree(string from, string to, string rel)
    {
        string source = Path.Combine(from, rel);
        if (!Directory.Exists(source)) return;
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            CopyFile(from, to, Path.GetRelativePath(from, file));
    }

    private static void CopyFile(string from, string to, string rel)
    {
        string source = Path.Combine(from, rel);
        if (!File.Exists(source)) return;
        string target = Path.Combine(to, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }
}
