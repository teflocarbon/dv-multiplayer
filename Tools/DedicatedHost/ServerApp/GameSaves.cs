using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DvmpServer;

/// <summary>One of DV's sessions (a career or free roam), which -mp-save picks by name.</summary>
public class GameSession
{
    public string User = "";
    public string Mode = "";
    public string Name = "";
    public string Folder = "";
    public int Saves;
    public DateTime? LastSave;
    /// <summary>The DV user the game loads (global.json's LastUser): only their sessions can be hosted.</summary>
    public bool CurrentUser;
    /// <summary>Another hostable session has the same name, so -mp-save may pick either.</summary>
    public bool DuplicateName;
}

/// <summary>DV's saves, from %USERPROFILE%\AppData\LocalLow\Altfuture\Derail Valley\Users.</summary>
public static class GameSaves
{
    public static readonly string UsersDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Altfuture", "Derail Valley", "Users");

    public static List<GameSession> List()
    {
        var sessions = new List<GameSession>();
        if (!Directory.Exists(UsersDir))
            return sessions;
        string lastUser = Json.Parse(Path.Combine(UsersDir, "global.json"))?.GetString("LastUser");
        foreach (string userDir in Directory.GetDirectories(UsersDir))
        {
            string user = Json.Parse(Path.Combine(userDir, "userData.json"))?.GetString("Name") ?? Path.GetFileName(userDir);
            foreach (string mode in new[] { "Career", "FreeRoam" })
            {
                string modeDir = Path.Combine(userDir, mode);
                if (!Directory.Exists(modeDir))
                    continue;
                foreach (string sessionDir in Directory.GetDirectories(modeDir))
                {
                    string data = Path.Combine(sessionDir, "sessionData.json");
                    if (!File.Exists(data))
                        continue;
                    FileInfo[] saves = Directory.Exists(Path.Combine(sessionDir, "Saves"))
                        ? new DirectoryInfo(Path.Combine(sessionDir, "Saves")).GetFiles("*.save")
                        : new FileInfo[0];
                    sessions.Add(new GameSession
                    {
                        User = user,
                        Mode = mode,
                        Name = Json.Parse(data)?.GetString("Name") ?? Path.GetFileName(sessionDir),
                        Folder = sessionDir,
                        Saves = saves.Length,
                        LastSave = saves.Length == 0 ? null : saves.Max(f => f.LastWriteTime),
                        CurrentUser = lastUser == null || string.Equals(user, lastUser, StringComparison.Ordinal),
                    });
                }
            }
        }
        foreach (var group in sessions.Where(s => s.CurrentUser && s.Saves > 0).GroupBy(s => s.Name).Where(g => g.Count() > 1))
            foreach (GameSession session in group)
                session.DuplicateName = true;
        return sessions
            .OrderByDescending(s => s.CurrentUser)
            .ThenByDescending(s => s.LastSave ?? DateTime.MinValue)
            .ToList();
    }
}
