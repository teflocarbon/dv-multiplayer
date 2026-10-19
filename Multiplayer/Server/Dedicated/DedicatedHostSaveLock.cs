using DV.Common;
using DV.UserManagement;
using DV.UserManagement.Data;
using System;
using System.IO;
using UnityEngine;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Dedicated;

/// <summary>
/// Stops a second game from opening the career a dedicated host is running: two games writing the
/// same career overwrite each other's saves. The dedicated host keeps "dedicated-host.lock" open in
/// the career's folder, with sharing denied, while its world is loaded. Windows lets go of it the
/// moment that process ends, crash or not, so a lock left behind by a crash never blocks anything.
/// Any other game with the mod checks for it before DV's launcher starts a save
/// (LauncherControllerSaveLockPatch).
/// </summary>
public static class DedicatedHostSaveLock
{
    private const string FileName = "dedicated-host.lock";

    private static FileStream held;
    private static string heldPath;

    /// <summary>The folder of a career (DV's GameSession), or null.</summary>
    public static string CareerFolder(IGameSession session) =>
        session is GameSession gameSession && !string.IsNullOrEmpty(gameSession.BasePath)
            ? Path.Combine(Application.persistentDataPath, gameSession.BasePath)
            : null;

    /// <summary>Called by a dedicated host once its world has loaded.</summary>
    public static void Take()
    {
        if (!NetworkLifecycle.Instance.IsDedicatedHost || held != null)
            return;
        string folder = CareerFolder(UserManager.Instance?.CurrentUser?.CurrentSession);
        if (folder == null)
        {
            Multiplayer.LogWarning("Dedicated host: couldn't find the career's folder; it isn't locked against other games");
            return;
        }
        try
        {
            heldPath = Path.Combine(folder, FileName);
            held = new FileStream(heldPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            byte[] note = System.Text.Encoding.UTF8.GetBytes(
                $"A dv-multiplayer dedicated host (process {System.Diagnostics.Process.GetCurrentProcess().Id}) has had this career open since {DateTime.Now:yyyy-MM-dd HH:mm:ss}.\r\n");
            held.Write(note, 0, note.Length);
            held.Flush();
            Application.quitting -= Release;
            Application.quitting += Release;
            Multiplayer.Log($"Dedicated host: locked the career against other games ({heldPath})");
        }
        catch (Exception e)
        {
            held = null;
            Multiplayer.LogWarning($"Dedicated host: couldn't lock the career: {e.Message}");
        }
    }

    /// <summary>Called when the server stops and when the game quits.</summary>
    public static void Release()
    {
        if (held == null)
            return;
        try
        {
            held.Dispose();
            File.Delete(heldPath);
        }
        catch (Exception e)
        {
            Multiplayer.LogWarning($"Dedicated host: couldn't remove the career lock: {e.Message}");
        }
        held = null;
        heldPath = null;
    }

    /// <summary>Whether a dedicated host has this career open right now.</summary>
    public static bool IsLocked(IGameSession session)
    {
        string folder = CareerFolder(session);
        if (folder == null)
            return false;
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            return false;
        try
        {
            // Opens only if nobody holds it: a lock left by a game that has ended.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
