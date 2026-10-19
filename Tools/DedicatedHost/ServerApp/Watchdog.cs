using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DvmpServer;

public enum LineKind { Info, Debug, Warning, Error, Console, App }

/// <summary>What the mod's heartbeat file says (DedicatedHost.WriteHeartbeat).</summary>
public class Heartbeat
{
    public DateTime Written;
    public double? Fps;
    public string[] Players = new string[0];
    public bool Paused;
    public bool Stopping;
    public DateTime? LastSave;

    public static Heartbeat Read(string path)
    {
        Json json = Json.Parse(path);
        if (json == null || json.GetString("time") == null)
            return null;
        var beat = new Heartbeat
        {
            Written = File.GetLastWriteTime(path),
            Fps = json.GetNumber("fps"),
            Players = json.GetStrings("players"),
            Paused = json.GetBool("paused"),
            Stopping = json.GetBool("stopping"),
        };
        if (DateTime.TryParse(json.GetString("lastSave"), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime saved))
            beat.LastSave = saved.ToLocalTime();
        return beat;
    }
}

/// <summary>
/// Starts the game as a dedicated host and keeps it running, as run-dedicated-host.ps1 does:
///   - the game exits without having been told to stop (a crash): restart it, after 15 s, or after
///     5 minutes once it has exited three times within 10 minutes;
///   - the heartbeat goes stale (a hang): kill the game and restart it;
///   - /stop in chat, or Stop here: the game saves and quits (the .stop file), and so does this;
///   - the daily restart time: ask the game to save and quit, then start it again.
/// Runs on its own thread; its events come from that thread.
/// </summary>
public class Watchdog
{
    public class Options
    {
        public string GameDir;
        public string ServerDir;
        public string DataDir;
        public string Save;
        public int Port;
        public string Visibility;
        public int AutosaveMinutes;
        public int Fps;
        public int StaleSeconds;
        public int StartupSeconds;
        /// <summary>Time of day, or null.</summary>
        public TimeSpan? DailyRestart;
        public bool ShowGameWindow;
    }

    public event Action<string, LineKind> Line;
    public event Action<Heartbeat> Beat;
    /// <summary>The world is loaded and players can join ("[Server] Server loaded" in the log).</summary>
    public event Action Ready;
    /// <summary>A new game process has started (first start, or a restart).</summary>
    public event Action GameStarted;
    public event Action Stopped;

    public bool Running => thread != null && thread.IsAlive;

    private readonly Options options;
    private readonly string heartbeat;
    private readonly string stopRequest;
    private readonly string commandFile;
    private readonly string watchdogLog;
    private readonly ManualResetEvent stopEvent = new(false);
    private volatile bool restartRequested;
    private Thread thread;
    private Process game;

    // The game's log, read as it's written. Only whole lines: the rest waits for the next read.
    private StreamReader logReader;
    private string logPath;
    private string logRest = "";

    public Watchdog(Options options)
    {
        this.options = options;
        heartbeat = Path.Combine(options.DataDir, "heartbeat.json");
        stopRequest = heartbeat + ".stop";
        // Commands typed here, one per line; the mod reads and runs them (DedicatedHost.RunConsoleCommands).
        commandFile = heartbeat + ".cmd";
        watchdogLog = Path.Combine(options.DataDir, "watchdog.log");
    }

    public static string HeartbeatPath => Path.Combine(AppConfig.DataDir, "heartbeat.json");

    /// <summary>
    /// A server another watchdog (or an earlier run of this app) left running: a fresh heartbeat
    /// that isn't stopping, with the game running.
    /// </summary>
    public static bool OtherServerRunning()
    {
        try
        {
            if (!File.Exists(HeartbeatPath) || (DateTime.Now - File.GetLastWriteTime(HeartbeatPath)).TotalSeconds > 30)
                return false;
            Heartbeat beat = Heartbeat.Read(HeartbeatPath);
            return beat != null && !beat.Stopping && Process.GetProcessesByName("DerailValley").Length > 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Asks a server this app didn't start to save and quit.</summary>
    public static void RequestStopOther() => File.WriteAllText(HeartbeatPath + ".stop", "stop");

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Watchdog" };
        thread.Start();
    }

    /// <summary>Save and quit the game, and stop watching.</summary>
    public void Stop() => stopEvent.Set();

    /// <summary>Save and quit the game, then start it again.</summary>
    public void Restart() => restartRequested = true;

    /// <summary>A chat command (or chat line) for the server to run as its owner.</summary>
    public void SendCommand(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return;
        File.AppendAllText(commandFile, line + Environment.NewLine);
        AppendWatchdogLog("> " + line);
    }

    public void Say(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        AppendWatchdogLog(message);
        Line?.Invoke(line, LineKind.App);
    }

    private void AppendWatchdogLog(string message)
    {
        try { File.AppendAllText(watchdogLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
        catch (Exception) { }
    }

    private bool StopWanted => stopEvent.WaitOne(0);

    private void Run()
    {
        var crashes = new List<DateTime>();
        DateTime lastDaily = DateTime.Today;
        try
        {
            Directory.CreateDirectory(Path.Combine(options.DataDir, "logs"));
            while (!StopWanted)
            {
                ServerMods.Refresh(options.GameDir, options.ServerDir, Say);
                StartGame();
                DateTime started = DateTime.Now;
                // StartGame deleted the old heartbeat, so the first one written is this game's.
                DateTime lastBeat = started;
                bool restart = false;
                bool userStop = false;
                while (!game.HasExited)
                {
                    // The log every quarter second, the heartbeat every 5.
                    for (int tick = 0; tick < 20 && !game.HasExited && !StopWanted && !restartRequested; tick++)
                    {
                        Thread.Sleep(250);
                        ShowNewLogLines();
                    }
                    if (game.HasExited)
                        break;
                    if (StopWanted)
                    {
                        userStop = true;
                        Say("stopping the server (it saves first)");
                        StopGame();
                        break;
                    }
                    if (restartRequested)
                    {
                        restartRequested = false;
                        restart = true;
                        Say("restarting the server (it saves first)");
                        StopGame();
                        break;
                    }
                    Heartbeat beat = Heartbeat.Read(heartbeat);
                    if (beat != null)
                        Beat?.Invoke(beat);
                    // Aged from the last write seen, not the file now: the mod swaps the file in with
                    // File.Replace, and mid-swap it can look missing, which is not a hang.
                    DateTime written = File.GetLastWriteTime(heartbeat);
                    if (written.Year > 2000 && written > lastBeat)
                        lastBeat = written;
                    double age = (DateTime.Now - lastBeat).TotalSeconds;
                    bool grace = (DateTime.Now - started).TotalSeconds < options.StartupSeconds && lastBeat <= started;
                    if (!grace && age > options.StaleSeconds)
                    {
                        Say($"the heartbeat is {age:N0} s old; the game looks hung, killing it");
                        Kill();
                        break;
                    }
                    if (options.DailyRestart is TimeSpan due && DateTime.Now >= DateTime.Today + due && lastDaily < DateTime.Today)
                    {
                        lastDaily = DateTime.Today;
                        Say("daily restart");
                        restart = true;
                        StopGame();
                        break;
                    }
                }
                game.WaitForExit();
                ShowNewLogLines();
                if (userStop)
                    break;
                Heartbeat last = Heartbeat.Read(heartbeat);
                if (!restart && last != null && last.Stopping)
                {
                    Say("the game was stopped (/stop or a stop request); not restarting");
                    break;
                }
                if (!restart)
                {
                    int code = -1;
                    try { code = game.ExitCode; } catch (Exception) { }
                    Say($"the game exited unexpectedly (code {code})");
                    crashes.Add(DateTime.Now);
                    int recent = crashes.Count(c => c > DateTime.Now.AddMinutes(-10));
                    if (recent >= 3)
                    {
                        Say("three exits within 10 minutes; waiting 5 minutes before trying again");
                        if (stopEvent.WaitOne(TimeSpan.FromMinutes(5)))
                            break;
                    }
                    else if (stopEvent.WaitOne(TimeSpan.FromSeconds(15)))
                        break;
                }
            }
        }
        catch (Exception e)
        {
            Say("error: " + e.Message);
        }
        finally
        {
            // Never leave the game running unwatched.
            if (game != null && !game.HasExited)
            {
                Say("stopping the game");
                StopGame();
            }
            logReader?.Dispose();
            logReader = null;
            Say("the server is stopped");
            Stopped?.Invoke();
        }
    }

    private void StartGame()
    {
        foreach (string file in new[] { heartbeat, stopRequest, commandFile })
            try { File.Delete(file); } catch (Exception) { }
        string log = Path.Combine(options.DataDir, "logs", $"{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var args = new List<string>
        {
            "-nonvr", "-mp-dedicated", "-mp-save", Quote(options.Save), "-mp-heartbeat", Quote(heartbeat),
            "-mp-autosave", options.AutosaveMinutes.ToString(), "-mp-fps", options.Fps.ToString(), "-logFile", Quote(log),
        };
        if (options.Port > 0)
            args.AddRange(new[] { "-mp-port", options.Port.ToString() });
        if (!string.IsNullOrEmpty(options.Visibility))
            args.AddRange(new[] { "-mp-visibility", options.Visibility.ToLowerInvariant() });
        // Unity Mod Manager looks for Mods in the working directory, but the game resets that to its
        // own folder before UMM starts, so the server picks its mods by giving Doorstop (which starts
        // UMM) the server's own UMM instead (ServerMods.InitializeUmm).
        args.AddRange(new[] { "--doorstop-target-assembly", Quote(ServerMods.UmmAssembly(options.ServerDir)) });
        game = Process.Start(new ProcessStartInfo(Path.Combine(options.GameDir, "DerailValley.exe"), string.Join(" ", args))
        {
            WorkingDirectory = options.GameDir,
            UseShellExecute = true,
            WindowStyle = options.ShowGameWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Minimized,
        });
        logReader?.Dispose();
        logReader = null;
        logRest = "";
        logPath = log;
        Say($"started the game (pid {game.Id}), log {log}");
        GameStarted?.Invoke();
    }

    private static string Quote(string arg) => "\"" + arg.Replace("\"", "\\\"") + "\"";

    // Asks the game to save and quit (the mod polls for the stop file), then waits for it.
    private void StopGame(int waitSeconds = 120)
    {
        if (game.HasExited)
            return;
        try { File.WriteAllText(stopRequest, "stop"); } catch (Exception) { }
        DateTime until = DateTime.Now.AddSeconds(waitSeconds);
        while (!game.HasExited && DateTime.Now < until)
        {
            game.WaitForExit(250);
            ShowNewLogLines();
        }
        if (!game.HasExited)
        {
            Say($"the game didn't quit within {waitSeconds} s; killing it");
            Kill();
        }
    }

    private void Kill()
    {
        try { game.Kill(); game.WaitForExit(10000); } catch (Exception) { }
    }

    private void ShowNewLogLines()
    {
        if (logPath == null)
            return;
        if (logReader == null)
        {
            if (!File.Exists(logPath))
                return;
            try
            {
                var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                logReader = new StreamReader(stream, Encoding.UTF8);
            }
            catch (Exception) { return; }
        }
        string chunk = logReader.ReadToEnd();
        if (chunk.Length == 0)
            return;
        string[] lines = (logRest + chunk).Split('\n');
        logRest = lines[lines.Length - 1];
        for (int i = 0; i < lines.Length - 1; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (!line.StartsWith("[Multiplayer] "))
                continue;
            string text = line.Substring(14);
            LineKind kind = line.Contains("] [Debug] ") || text.StartsWith("[Debug] ") ? LineKind.Debug
                : text.Contains("[Error]") ? LineKind.Error
                : text.Contains("[Warning]") ? LineKind.Warning
                : text.Contains("[Console]") ? LineKind.Console
                : LineKind.Info;
            Line?.Invoke(text, kind);
            if (text.Contains("Server loaded"))
                Ready?.Invoke();
        }
    }
}
