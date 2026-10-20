using System;
using UnityEngine;
using UnityModManagerNet;

namespace Dvmp.Agent;

/// <summary>Unity Mod Manager entry point for the dev agent.</summary>
public static class AgentMain
{
    public const string Version = "0.1.0";

    internal static UnityModManager.ModEntry Mod;
    internal static AgentServer Server;

    public static bool Load(UnityModManager.ModEntry modEntry)
    {
        Mod = modEntry;
        try
        {
            // A minimised or unfocused test instance must keep ticking or every request stalls.
            Application.runInBackground = true;
            LogBuffer.Install();
            // Before DV creates its storage (when its first scene loads), and logged, so dvmp_logs shows it.
            try { UserDataRedirect.InstallIfRequested(); }
            catch (Exception e) { modEntry.Logger.Error("dvmp: user data: " + e); }
            try { ModLoggerHook.Install(); }
            catch (Exception e) { modEntry.Logger.Warning("dvmp: could not mirror mod logs: " + e.Message); }

            GameObject host = new("[DvmpDevAgent]");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<MainThread>();
            host.AddComponent<RemoteIndicator>();
            HarnessMode.InstallIfRequested(host);

            Server = new AgentServer();
            Server.Start();
            Discovery.Write(Server.Port, Server.Token);
            Application.quitting += Shutdown;
            modEntry.OnGUI = RemoteUi.Draw;

            modEntry.Logger.Log($"DVMP dev agent {Version} listening on 127.0.0.1:{Server.Port}");
            return true;
        }
        catch (Exception e)
        {
            modEntry.Logger.LogException(e);
            return false;
        }
    }

    private static void Shutdown()
    {
        try { RemoteAccess.Stop("the game closed"); } catch { }
        try { Server?.Stop(); } catch { }
        Discovery.Delete();
    }
}
