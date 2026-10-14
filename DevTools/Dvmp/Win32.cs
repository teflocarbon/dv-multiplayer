using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dvmp;

internal static class Win32
{
    private const int SwShowNoActivate = 4;
    private const int SwShowMinNoActive = 7;

    /// <summary>
    /// Unity stops rendering a minimised window, so a screenshot of one would be stale or black.
    /// Restores it without taking focus for the duration of <paramref name="work"/>, then
    /// minimises it again.
    /// </summary>
    public static async Task<T> WithWindowVisibleAsync<T>(int pid, Func<Task<T>> work)
    {
        IntPtr window = MainWindow(pid);
        bool wasMinimised = window != IntPtr.Zero && IsIconic(window);
        if (!wasMinimised) return await work();

        ShowWindow(window, SwShowNoActivate);
        await Task.Delay(400); // let a few frames render at the background frame cap
        try { return await work(); }
        finally { ShowWindow(window, SwShowMinNoActive); }
    }

    /// <summary>Sets a game window's title so host and client are easy to tell apart in the taskbar.</summary>
    public static void SetTitle(int pid, string title)
    {
        IntPtr window = MainWindow(pid);
        if (window != IntPtr.Zero) SetWindowText(window, title);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hWnd, string text);

    private static IntPtr MainWindow(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.MainWindowHandle;
        }
        catch { return IntPtr.Zero; }
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
