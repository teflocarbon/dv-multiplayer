using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DvmpServer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One app per Windows user: two would fight over the same server folder and heartbeat.
        using var single = new Mutex(true, @"Local\DvMultiplayerServerApp", out bool first);
        if (!first)
        {
            MessageBox.Show("DV Multiplayer Server is already open.", "DV Multiplayer Server", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        Application.Run(new MainForm());
    }

    // Into the data folder's server-app-errors.log, and on screen.
    private static void Report(Exception e)
    {
        string path = Path.Combine(AppConfig.DataDir, "server-app-errors.log");
        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}{Environment.NewLine}");
        }
        catch (Exception) { }
        MessageBox.Show($"Something went wrong: {e?.Message}\n\nThe details are in {path}.", "DV Multiplayer Server",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
