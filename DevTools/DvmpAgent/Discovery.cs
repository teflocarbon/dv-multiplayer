using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace Dvmp.Agent;

/// <summary>
/// Advertises this process to the dvmp tool through a per-user file. The token never leaves
/// this file or the Authorization header; no endpoint returns it.
/// </summary>
internal static class Discovery
{
    private static string path;

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dvmp", "agents");

    public static void Write(int port, string token)
    {
        System.IO.Directory.CreateDirectory(Directory);
        int pid = Process.GetCurrentProcess().Id;
        path = Path.Combine(Directory, $"agent-{pid}.json");
        string json = JsonConvert.SerializeObject(new
        {
            pid,
            port,
            token,
            agentVersion = AgentMain.Version,
            startedUtc = DateTime.UtcNow,
            args = Environment.GetCommandLineArgs()
        }, Formatting.Indented);
        string temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    public static void Delete()
    {
        try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
    }
}
