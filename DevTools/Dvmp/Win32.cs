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

    /// <summary>
    /// The processes with a UDP socket bound to <paramref name="port"/>, IPv4 or IPv6 (GNS binds a
    /// dual-stack IPv6 socket under Wine). Read from the system's tables rather than by trying to
    /// bind the port: the game's socket allows address reuse, so a test bind succeeds even while a
    /// game is listening.
    /// </summary>
    public static HashSet<int> UdpPortOwners(int port)
    {
        HashSet<int> owners = new();
        // MIB_UDPROW_OWNER_PID: address (4), port (network byte order), pid.
        AddUdpPortOwners(owners, port, AfInet, rowSize: 12, portOffset: 4);
        // MIB_UDP6ROW_OWNER_PID: address (16), scope id, port (network byte order), pid.
        AddUdpPortOwners(owners, port, AfInet6, rowSize: 28, portOffset: 20);
        return owners;
    }

    private static void AddUdpPortOwners(HashSet<int> owners, int port, int family, int rowSize, int portOffset)
    {
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UdpTableOwnerPid, 0);
        if (size <= 0) return;
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(table, ref size, false, family, UdpTableOwnerPid, 0) != 0) return;
            int rows = Marshal.ReadInt32(table);
            for (int i = 0; i < rows; i++)
            {
                IntPtr row = table + 4 + i * rowSize;
                int rowPort = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, portOffset));
                if (rowPort == port) owners.Add(Marshal.ReadInt32(row, portOffset + 4));
            }
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int UdpTableOwnerPid = 1;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
