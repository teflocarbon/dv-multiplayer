using Humanizer;
using Multiplayer.Components.UI.ServerBrowser;
using Multiplayer.Utils;
using System;
using UnityEngine;
using UnityModManagerNet;
using static Multiplayer.Components.Networking.UI.PlayerListGUI;
using Console = DV.Console;

namespace Multiplayer;

[Serializable]
[DrawFields(DrawFieldMask.OnlyDrawAttr)]
public class Settings : UnityModManager.ModSettings, IDrawable
{
    public const int CURRENT_VERSION = 4;
    public const byte MAX_USERNAME_LENGTH = 24;

    public static Action<Settings> OnSettingsUpdated;

    public int SettingsVer = CURRENT_VERSION;

    [Header("Player")]
    [Draw("Use Steam Name", Tooltip = "Use your Steam name as your username in-game.")]
    public bool UseSteamName = true;
    public string LastSteamName = string.Empty;
    public ulong SteamId = 0;
    [Draw("Username", Tooltip = "Your username in-game.", VisibleOn = "UseSteamName|false")]
    public string Username = "Player";
    public string Guid = System.Guid.NewGuid().ToString();
    public string CharacterId = "MP_UC";

    [Space(10)]
    [Header("Gameplay")]
    [Draw("Allow clients to set their own crew names", Tooltip = "Whether to allow clients to set their own crew names.")]
    public bool AllowClientCrewNames = false;
    [Draw("Fast Travel Advances Time", Tooltip = "When players use fast travel, time will advance.")]
    public bool FastTravelAdvancesTime = true;
    [Draw("Lost Item Owner Distance", Tooltip = "Distance from the persistent owner before a world item can become lost.", Min = 50, Max = 2000)]
    public float LostItemOwnerDistance = 200f;
    [Draw("Lost Item Nearby Player Protection", Tooltip = "Do not collect an item while any active player is this close.", Min = 10, Max = 500)]
    public float LostItemNearbyPlayerProtectionDistance = 100f;
    [Draw("Lost Item Collection Grace", Tooltip = "Seconds an item must remain eligible before collection.", Min = 1, Max = 120)]
    public float LostItemCollectionGraceSeconds = 10f;

    [Space(10)]
    [Header("Server")]
    [Draw("Server Name", Tooltip = "Name of your server in the lobby browser.")]
    public string ServerName = "";
    [Draw("Password", Tooltip = "The password required to join your server. Leave blank for no password.")]
    public string Password = "";
    [Draw("Server Visibility")]
    public ServerVisibility Visibility = ServerVisibility.Public;
    public bool PublicGame = true;
    [Draw("Max Players", Tooltip = "The maximum number of players that can join your server, including yourself.")]
    public int MaxPlayers = 4;
    [Draw("Port", Tooltip = "The port that your server will listen on. You generally don't need to change this.")]
    public int Port = 7777;
    [Draw("Details", Tooltip = "Details shown in the server browser.")]
    public string Details = "";

    [Space(10)]
    [Header("Lobby Server")]
    [Draw("Lobby Server address", Tooltip = "Address of lobby server for finding multiplayer games.")]
    public string LobbyServerAddress = "https://dv.mineit.space";
    [Draw("IPv4 Check Address", Tooltip = "Do not modify unless the service is unavailable.")]
    public string Ipv4AddressCheck = "https://api.ipify.org/";
    [Header("Last Server Connected to by IP")]
    [Draw("Last Remote IP", Tooltip = "The IP for the last server connected to by IP.")]
    public string LastRemoteIP = "";
    [Draw("Last Remote Port", Tooltip = "The port for the last server connected to by IP.")]
    public int LastRemotePort = 7777;
    [Draw("Last Remote Password", Tooltip = "The password for the last server connected to by IP.")]
    public string LastRemotePassword = "";

    [Space(10)]
    [Header("Preferences")]
    [Draw("Show Name Tags", Tooltip = "Whether to show player names above their heads.")]
    public bool ShowNameTags = true;
    [Draw("Show Ping In Name Tag", Tooltip = "Whether to show player pings above their heads.", VisibleOn = "ShowNameTags|true")]
    public bool ShowPingInNameTags;
    [Draw("Show Player List in Alt Mouse Mode", Tooltip = "Whether to show the player list in mouse mode.")]
    public bool ShowPlayerListInAltMouseMode = true;
    [Draw("Player List Position", Tooltip = "Location to show the player list when it's visible.", VisibleOn = "ShowPlayerListInAltMouseMode|true")]
    public PlayerListPosition PlayerListPosition = PlayerListPosition.TopCenter;
    [Draw("Chat Key Bind", Tooltip = "Key to show chat window.")]
    public KeyCode ChatKey = KeyCode.Return;
    [Draw("Hide Chat Messages", Tooltip = "Hide incoming chat messages.")]
    public bool HideChatMessages = false;

    [Space(10)]
    [Header("Advanced Settings")]
    [Draw("Show Advanced Settings", Tooltip = "You probably don't need to change these.")]
    public bool ShowAdvancedSettings;
    [Draw("Show Stats", Tooltip = "Whether to show network statistics.", VisibleOn = "ShowAdvancedSettings|true")]
    public bool ShowStats;
    [Draw("Stats List Size", Tooltip = "How many packets to list in the network statistics GUI.", VisibleOn = "ShowStats|true")]
    public int StatsListSize = 3;
    [Draw("Debug Logging", Tooltip = "Whether to log extra information. This is useful for debugging, but should otherwise be kept off.", VisibleOn = "ShowAdvancedSettings|true")]
    public bool DebugLogging;
    [Draw("Enable Debug Loopback Client", Tooltip = "Listens only on localhost for the standalone development client. This does not expose a LAN or internet server.", VisibleOn = "ShowAdvancedSettings|true")]
    public bool EnableDebugLoopbackClient;
    [Draw("Debug Loopback Port", Tooltip = "Localhost-only port used by the standalone development client.", VisibleOn = "ShowAdvancedSettings|true")]
    public int DebugLoopbackPort = 7778;
    [Draw("Enable Log File", Tooltip = "Whether to create a separate file for logs. This is useful for debugging, but should otherwise be kept off.", VisibleOn = "ShowAdvancedSettings|true")]
    public bool EnableLogFile;
    [Space(10)]
    [Header("Multiplayer Debug System")]
    [Draw("Enable Debug System", Tooltip = "Enable structured in-process multiplayer diagnostics.", VisibleOn = "ShowAdvancedSettings|true")]
#if DEBUG
    public bool EnableDebugSystem = true;
#else
    public bool EnableDebugSystem = false;
#endif
    [Draw("Enable Debug File Logging", VisibleOn = "EnableDebugSystem|true")]
    public bool EnableDebugFileLogging = true;
    [Draw("Enable Debug Firehose", Tooltip = "Serve the local debug UI on a random loopback port.", VisibleOn = "EnableDebugSystem|true")]
    public bool EnableDebugFirehose = true;
#if DEBUG
    [Draw("Enable Runtime Test Harness", Tooltip = "Allow authenticated debug clients to run game-native integration tests.", VisibleOn = "EnableDebugSystem|true")]
    public bool EnableRuntimeTestHarness = true;
#endif
    // Kept for settings-file compatibility. Raw capture is intentionally armed only from
    // the in-game packet inspector for the current session because persisting it can make
    // startup unusable on packet-heavy worlds.
    public bool EnableRawPacketCapture;
    [Draw("Enable World Labels", VisibleOn = "EnableDebugSystem|true")]
    public bool EnableDebugWorldLabels;
    [Draw("World Label Radius", Min = 5, Max = 500, VisibleOn = "EnableDebugWorldLabels|true")]
    public float DebugWorldLabelRadius = 30f;
    [Draw("World Label Scale", Min = 0.5, Max = 3, VisibleOn = "EnableDebugWorldLabels|true")]
    public float DebugWorldLabelScale = 1f;
    [Draw("World Label Update Rate", Min = 1, Max = 10, VisibleOn = "EnableDebugWorldLabels|true")]
    public float DebugWorldLabelUpdateHz = 4f;
    [Draw("Maximum World Labels", Min = 1, Max = 128, VisibleOn = "EnableDebugWorldLabels|true")]
    public int DebugWorldLabelMaxCount = 64;
    [Draw("Debug Overlay UI Scale", Min = 0.75, Max = 1.5, VisibleOn = "EnableDebugSystem|true")]
    public float DebugOverlayUiScale = 1f;
    [Draw("Show Labels Through Walls", VisibleOn = "EnableDebugWorldLabels|true")]
    public bool DebugLabelsThroughWalls;
    [Draw("High Frequency Sampling", Min = 1, Max = 120, VisibleOn = "EnableDebugSystem|true")]
    public int DebugHighFrequencySampling = 10;
    [Draw("Max Entity Timeline Events", Min = 50, Max = 2000, VisibleOn = "EnableDebugSystem|true")]
    public int DebugMaxEntityTimelineEvents = 250;
    [Draw("Max In-Memory Events", Min = 100, Max = 100000, VisibleOn = "EnableDebugSystem|true")]
    public int DebugMaxInMemoryEvents = 10000;
    [Draw("Enable NAT Punch", VisibleOn = "ShowAdvancedSettings|true")]
    public bool EnableNatPunch = true;
    [Draw("Reuse NetPacketReaders", VisibleOn = "ShowAdvancedSettings|true")]
    public bool ReuseNetPacketReaders = true;
    [Draw("Use Native Sockets", VisibleOn = "ShowAdvancedSettings|true")]
    public bool UseNativeSockets = true;
    [Draw("Log Full IPs", Tooltip = "Whether to log the full IP address of clients. This is useful for debugging, but should otherwise be kept off.", VisibleOn = "ShowAdvancedSettings|true")]
    public bool LogIps;
    [Draw("Simulate Packet Loss", VisibleOn = "ShowAdvancedSettings|true")]
    public bool SimulatePacketLoss;
    [Draw("Packet Loss Chance", VisibleOn = "SimulatePacketLoss|true")]
    public int SimulationPacketLossChance = 10;
    [Draw("Simulate Latency", VisibleOn = "ShowAdvancedSettings|true")]
    public bool SimulateLatency;
    [Draw("Minimum Latency (ms)", VisibleOn = "SimulateLatency|true")]
    public int SimulationMinLatency = 30;
    [Draw("Maximum Latency (ms)", VisibleOn = "SimulateLatency|true")]
    public int SimulationMaxLatency = 100;
    public bool ForceJson = false;
#if DEBUG
    [Draw("Export Save Data On Load", VisibleOn = "ShowAdvancedSettings|true")]
    public bool ExportSaveOnLoad = false;
#endif

    public void Draw(UnityModManager.ModEntry modEntry)
    {
        Settings self = this;

#if DEBUG
        if (GUILayout.Button("Dump Save Json"))
            ExportSaveData.DumpSaveData();
#endif

        UnityModManager.UI.DrawFields(ref self, modEntry, DrawFieldMask.OnlyDrawAttr, OnChange);

        if (ShowAdvancedSettings && GUILayout.Button("Enable Developer Commands"))
            Console.RegisterDevCommands();
    }

    public override void Save(UnityModManager.ModEntry modEntry)
    {
        LastSteamName = LastSteamName.Trim().Truncate(MAX_USERNAME_LENGTH);
        Username = Username.Trim().Truncate(MAX_USERNAME_LENGTH);

        Port = Mathf.Clamp(Port, 1024, 49151);
        DebugLoopbackPort = Mathf.Clamp(DebugLoopbackPort, 1024, 49151);
        DebugWorldLabelRadius = Mathf.Clamp(DebugWorldLabelRadius, 5f, 500f);
        DebugWorldLabelScale = Mathf.Clamp(DebugWorldLabelScale, 0.5f, 3f);
        DebugWorldLabelUpdateHz = Mathf.Clamp(DebugWorldLabelUpdateHz, 1f, 10f);
        DebugWorldLabelMaxCount = Mathf.Clamp(DebugWorldLabelMaxCount, 1, 128);
        DebugOverlayUiScale = Mathf.Clamp(DebugOverlayUiScale, 0.75f, 1.5f);
        LostItemOwnerDistance = Mathf.Clamp(LostItemOwnerDistance, 50f, 2000f);
        LostItemNearbyPlayerProtectionDistance = Mathf.Clamp(LostItemNearbyPlayerProtectionDistance, 10f, 500f);
        LostItemCollectionGraceSeconds = Mathf.Clamp(LostItemCollectionGraceSeconds, 1f, 120f);
        DebugHighFrequencySampling = Mathf.Clamp(DebugHighFrequencySampling, 1, 120);
        DebugMaxEntityTimelineEvents = Mathf.Clamp(DebugMaxEntityTimelineEvents, 50, 2000);
        DebugMaxInMemoryEvents = Mathf.Clamp(DebugMaxInMemoryEvents, 100, 100000);
        MaxPlayers = Mathf.Clamp(MaxPlayers, 1, byte.MaxValue);
        Password = Password?.Trim();

        ChatKey = ChatKey == KeyCode.None ? KeyCode.Return : ChatKey;

        if (!UnloadWatcher.isQuitting)
            OnSettingsUpdated?.Invoke(this);
        Save(this, modEntry);
    }

    public void OnChange()
    {
        // yup
    }

    public Guid GetGuid()
    {
        if (System.Guid.TryParse(Guid, out Guid guid))
            return guid;
        guid = System.Guid.NewGuid();
        Guid = guid.ToString();
        return guid;
    }

    public string GetUserName()
    {
        string username = Username;

        if (Multiplayer.Settings.UseSteamName)
        {
            if (SteamworksUtils.GetSteamUser(out string steamUsername, out ulong steamId))
            {
                Multiplayer.Settings.LastSteamName = steamUsername;
                Multiplayer.Settings.SteamId = steamId;
            }

            if (Multiplayer.Settings.LastSteamName != string.Empty)
                username = Multiplayer.Settings.LastSteamName;
        }

        return username;
    }

    public static Settings Load(UnityModManager.ModEntry modEntry)
    {
        Settings data = Settings.Load<Settings>(modEntry);

        MigrateSettings(ref data);

        data.SettingsVer = GetCurrentVersion();

        data.Save(modEntry);

        return data;
    }

    private static int GetCurrentVersion()
    {
        return CURRENT_VERSION;
    }

    // Function to handle migrations based on the current version
    private static void MigrateSettings(ref Settings data)
    {
        switch (data.SettingsVer)
        {
            case 0:
                //We want to disable Punch until it's fully implemented
                data.EnableNatPunch = false;

                //Ensure http setting is upgraded to https if using the default lobby server
                if (data.LobbyServerAddress == "http://dv.mineit.space")
                    data.LobbyServerAddress = new Settings().LobbyServerAddress;

                break;

            case 1:
                if (data.Ipv4AddressCheck == "http://checkip.dyndns.org")
                    data.Ipv4AddressCheck = new Settings().Ipv4AddressCheck;

                data.ShowAdvancedSettings = true;
                data.DebugLogging = true;
                data.ShowPingInNameTags = true;

                break;

            case 2:

                if (data.PublicGame)
                    data.Visibility = ServerVisibility.Public;
                else
                    data.Visibility = ServerVisibility.Friends;

                break;

            default:
                break;
        }

        if (data.SettingsVer < GetCurrentVersion())
        {
            data.SettingsVer++;
            MigrateSettings(ref data);
        }
    }
}
