using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Multiplayer.Shared.Transport.Gns;

/// <summary>
///     P/Invoke over the flat C API of Valve's open-source GameNetworkingSockets (v1.6.1, built by
///     DevTools/Gns/build-gns.ps1), the library Steam's own sockets come from. Debug builds only:
///     Multiplayer.csproj compiles this folder out of every other configuration.
///
///     Only what GnsTransport needs is bound. Structs GNS hands back are read at fixed offsets;
///     DevTools/Gns/layout-check.cpp asserts each offset and constant here against the headers
///     the DLL was built from.
/// </summary>
internal static class GnsNative
{
    public const string Library = "GameNetworkingSockets";

    // ESteamNetworkingConnectionState
    public const int StateNone = 0;
    public const int StateConnecting = 1;
    public const int StateFindingRoute = 2;
    public const int StateConnected = 3;
    public const int StateClosedByPeer = 4;
    public const int StateProblemDetectedLocally = 5;
    public const int StateFinWait = -1;
    public const int StateLinger = -2;
    public const int StateDead = -3;

    // ESteamNetworkingConfigValue
    public const int ConfigFakePacketLossSend = 2;
    public const int ConfigFakePacketLossRecv = 3;
    public const int ConfigFakePacketLagSend = 4;
    public const int ConfigFakePacketLagRecv = 5;
    public const int ConfigSendBufferSize = 9;
    public const int ConfigSendRateMin = 10;
    public const int ConfigSendRateMax = 11;
    public const int ConfigTimeoutConnected = 25;

    // ESteamNetworkingSocketsDebugOutputType
    public const int DebugOutputWarning = 4;

    public const int ResultOK = 1;

    // SteamNetworkingMessage_t
    public const int MessageData = 0;
    public const int MessageSize = 8;
    public const int MessageConnection = 12;
    public const int MessageChannel = 192;

    // SteamNetConnectionStatusChangedCallback_t, whose SteamNetConnectionInfo_t starts at 8
    public const int StatusConnection = 0;
    public const int StatusListenSocket = 8 + 144;
    public const int StatusState = 8 + 176;
    public const int StatusEndReason = 8 + 180;
    public const int StatusEndDebug = 8 + 184;
    public const int StatusOldState = 704;

    // SteamNetConnectionRealTimeStatus_t
    public const int RealTimeStatusSize = 120;
    public const int RealTimeStatusPing = 4;

    /// <summary>SteamNetworkingIPAddr: an IPv6 address (IPv4 mapped as ::ffff:a.b.c.d) and a port.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IPAddr
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Ipv6;
        public ushort Port;

        public static IPAddr Any(ushort port) => new() { Ipv6 = new byte[16], Port = port };

        public static IPAddr From(System.Net.IPAddress address, ushort port)
        {
            byte[] bytes = new byte[16];
            byte[] raw = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                bytes[10] = 0xFF;
                bytes[11] = 0xFF;
                Array.Copy(raw, 0, bytes, 12, 4);
            }
            else
            {
                Array.Copy(raw, bytes, 16);
            }
            return new IPAddr { Ipv6 = bytes, Port = port };
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StatusChangedCallback(IntPtr status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DebugOutputCallback(int type, IntPtr message);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GameNetworkingSockets_Init(IntPtr identity, StringBuilder errMsg);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void GameNetworkingSockets_Kill();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamNetworkingSockets_LibV12();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamNetworkingUtils_LibV4();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint SteamAPI_ISteamNetworkingSockets_CreateListenSocketIP(IntPtr self, ref IPAddr address, int optionCount, IntPtr options);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint SteamAPI_ISteamNetworkingSockets_ConnectByIPAddress(IntPtr self, ref IPAddr address, int optionCount, IntPtr options);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SteamAPI_ISteamNetworkingSockets_AcceptConnection(IntPtr self, uint connection);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingSockets_CloseConnection(IntPtr self, uint connection, int reason, string debug, [MarshalAs(UnmanagedType.I1)] bool linger);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingSockets_CloseListenSocket(IntPtr self, uint socket);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SteamAPI_ISteamNetworkingSockets_SendMessageToConnection(IntPtr self, uint connection, byte[] data, uint size, int flags, IntPtr outMessageNumber);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection(IntPtr self, uint connection, [In, Out] IntPtr[] messages, int max);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnPollGroup(IntPtr self, uint pollGroup, [In, Out] IntPtr[] messages, int max);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint SteamAPI_ISteamNetworkingSockets_CreatePollGroup(IntPtr self);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingSockets_DestroyPollGroup(IntPtr self, uint pollGroup);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingSockets_SetConnectionPollGroup(IntPtr self, uint connection, uint pollGroup);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SteamAPI_ISteamNetworkingSockets_GetConnectionRealTimeStatus(IntPtr self, uint connection, IntPtr status, int laneCount, IntPtr lanes);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SteamAPI_ISteamNetworkingSockets_RunCallbacks(IntPtr self);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(IntPtr self, int config, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueFloat(IntPtr self, int config, float value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_SteamNetConnectionStatusChanged(IntPtr self, StatusChangedCallback callback);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction(IntPtr self, int detailLevel, DebugOutputCallback callback);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);

    /// <summary>ISteamNetworkingSockets, once <see cref="Init"/> has run.</summary>
    public static IntPtr Sockets { get; private set; }

    /// <summary>ISteamNetworkingUtils, once <see cref="Init"/> has run.</summary>
    public static IntPtr Utils { get; private set; }

    // Kept alive for as long as GNS may call them.
    private static StatusChangedCallback statusChanged;
    private static DebugOutputCallback debugOutput;

    /// <summary>
    ///     Loads the DLL from beside Multiplayer.dll and starts GNS, once per process: it keeps
    ///     running for the game's lifetime, like Steam's sockets. The callbacks are process-wide too.
    /// </summary>
    public static void Init(StatusChangedCallback onStatusChanged, DebugOutputCallback onDebugOutput)
    {
        if (Sockets != IntPtr.Zero)
            return;

        string dir = Path.GetDirectoryName(typeof(GnsNative).Assembly.Location);
        string path = Path.Combine(dir, Library + ".dll");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"{path} is missing: run DevTools/Gns/build-gns.ps1, then rebuild Multiplayer (Debug)", path);
        if (LoadLibraryW(path) == IntPtr.Zero)
            throw new DllNotFoundException($"Couldn't load {path}: Win32 error {Marshal.GetLastWin32Error()}");

        StringBuilder error = new(1024);
        if (!GameNetworkingSockets_Init(IntPtr.Zero, error))
            throw new InvalidOperationException($"GameNetworkingSockets_Init failed: {error}");

        Sockets = SteamNetworkingSockets_LibV12();
        Utils = SteamNetworkingUtils_LibV4();

        statusChanged = onStatusChanged;
        debugOutput = onDebugOutput;
        SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_SteamNetConnectionStatusChanged(Utils, statusChanged);
        SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction(Utils, DebugOutputWarning, debugOutput);

        UnityEngine.Application.quitting += Kill;
    }

    /// <summary>
    ///     Stops GNS as the game quits, before Mono shuts down. GNS's service thread enters managed
    ///     code when it logs, and a game whose service thread was still running hung on quit.
    /// </summary>
    private static void Kill()
    {
        if (Sockets == IntPtr.Zero)
            return;
        SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction(Utils, 0, null);
        GameNetworkingSockets_Kill();
        Sockets = IntPtr.Zero;
        Utils = IntPtr.Zero;
    }
}
