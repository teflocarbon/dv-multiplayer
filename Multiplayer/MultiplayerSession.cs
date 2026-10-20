using DV.Scenarios.Common;
using LiteNetLib;
using MPAPI;
using Multiplayer.Client.Api;
using Multiplayer.Client.Players;
using Multiplayer.Client.Session;
using Multiplayer.Client.UI;
using Multiplayer.Server.Api;
using Multiplayer.Server.Dedicated;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Players;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Session;
using System;
using System.Linq;
using System.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Multiplayer;

/// <summary>
///     The composition root of a session (docs/design/code-layout.md, "The lifecycle"): starts and
///     stops the server and the client, registers them with MPAPI, attaches them to
///     <see cref="NetworkLifecycle"/> (the shared tick and role queries) and to their own
///     accessors (<see cref="NetworkServer.Instance"/>, <see cref="NetworkClient.Instance"/>), and
///     wires up the dedicated host and the player list and network stats UI. On the same object as
///     the lifecycle.
/// </summary>
public class MultiplayerSession : MonoBehaviour
{
    public static MultiplayerSession Instance { get; private set; }

    /// <summary>How the next hosted game is offered (set by the host menu or the dedicated host); null for a default one.</summary>
    public LobbyServerData serverData;
    public bool IsPublicGame { get; set; } = false;
    public bool IsSinglePlayer { get; set; } = true;

    private PlayerListGUI playerList;
    private NetworkStatsGui Stats;
    private LocalPlayerSession localSession;
    private const string LocalSessionArg = "-mp-local-session";

    private void Awake()
    {
        Instance = this;
        playerList = gameObject.AddComponent<PlayerListGUI>();
        Stats = gameObject.AddComponent<NetworkStatsGui>();
        WorldStreamingInit.LoadingFinished += () => { playerList.RegisterListeners(); };
        Settings.OnSettingsUpdated += OnSettingsUpdated;
        SceneManager.sceneLoaded += (scene, _) =>
        {
            if (scene.buildIndex != (int)DVScenes.MainMenu)
                return;

            playerList.UnRegisterListeners();
            DedicatedHost.OnMainMenu();
        };
        if (DedicatedHost.Requested)
        {
            gameObject.AddComponent<DedicatedHostMode>();
            // The mod can load after the menu scene already has.
            if (SceneManager.GetActiveScene().buildIndex == (int)DVScenes.MainMenu)
                DedicatedHost.OnMainMenu();
        }
    }

    private void OnSettingsUpdated(Settings settings)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (!lifecycle.IsSessionRunning)
            return;
        if (settings.ShowStats)
            Stats.Show(NetworkClient.Instance?.Statistics, NetworkServer.Instance?.Statistics);
        else
            Stats.Hide();
    }

    public bool StartServer(IDifficulty difficulty)
    {
        int port = Multiplayer.Settings.Port;

        if (NetworkServer.Instance != null)
            throw new InvalidOperationException("NetworkManager already exists!");

        // A dedicated host is never a single-player game, however hosting was started.
        if (DedicatedHost.Requested)
            IsSinglePlayer = false;

        if (!IsSinglePlayer)
        {
            if (serverData != null)
            {
                port = serverData.port;
            }
        }

        Multiplayer.Log($"Starting server on port {port}");
        NetworkServer server = new(difficulty, Multiplayer.Settings, IsSinglePlayer, serverData);

        if (!server.Start(port))
            return false;

        NetworkServer.Instance = server;
        NetworkLifecycle.Instance.AttachServer(server, server);
        AttachAvatars();
        DebugRuntime.SetRole("host", Multiplayer.Settings.GetUserName());

        // Register server API
        var serverAPI = new ServerAPIProvider(server);
        MultiplayerAPI.RegisterServer(serverAPI);

        if (!NetworkLifecycle.LocalSessionRequested)
            StartClient(IPAddress.Loopback.ToString(), port, Multiplayer.Settings.Password, IsSinglePlayer, null);
        else if (!DedicatedHost.Requested)
            StartLocalPlayerSession(server, IsSinglePlayer);
        // else a dedicated host with no player at all (docs/design/host-local-session.md, stage 3).

        //reset for next game
        IsSinglePlayer = true;
        serverData = null;

        return true;
    }

    public void StartClient(string address, int port, string password, bool isSinglePlayer, Action<DisconnectReason, string> onDisconnect)
    {
        if (NetworkClient.Instance != null)
            throw new InvalidOperationException("NetworkManager already exists!");
        NetworkClient client = new(Multiplayer.Settings, isSinglePlayer);
        client.Start(address, port, password, isSinglePlayer, onDisconnect);

        NetworkClient.Instance = client;
        NetworkLifecycle.Instance.AttachClient(client, client);
        AttachAvatars();
        if (NetworkServer.Instance == null)
            DebugRuntime.SetRole("client", client.Username);

        // Register client API
        var clientAPI = new ClientAPIProvider(client);
        MultiplayerAPI.RegisterClient(clientAPI);

        OnSettingsUpdated(Multiplayer.Settings); // Show stats if enabled
    }

    // The host's own player as a session of its server, instead of a loopback client
    // (docs/design/host-local-session.md, stage 3; -mp-local-session until that's the only way).
    private void StartLocalPlayerSession(NetworkServer server, bool isSinglePlayer)
    {
        LocalPlayerSession session = new(isSinglePlayer);
        localSession = session;
        NetworkLifecycle.Instance.AttachClient(null, session);
        ServerPlayer player = server.AddLocalPlayer(session.Username, Multiplayer.Settings.CharacterId, VRManager.IsVREnabled(), session.Peer);
        session.OnAdmitted(player.PlayerId, player.Username);
        DebugRuntime.SetRole("host", player.Username, player.PlayerId);
        OnSettingsUpdated(Multiplayer.Settings); // Show stats if enabled
    }

    public void Stop()
    {
        Stats?.Hide();

        NetworkServer server = NetworkServer.Instance;
        if (server != null)
        {
            server.Stop();
            MultiplayerAPI.ClearServer();
            NetworkServer.Instance = null;
            NetworkLifecycle.Instance.AttachServer(null, null);
        }

        NetworkClient client = NetworkClient.Instance;
        if (client != null)
        {
            client.Stop();
            MultiplayerAPI.ClearClient();
            NetworkClient.Instance = null;
            NetworkLifecycle.Instance.AttachClient(null, null);
        }

        if (localSession != null)
        {
            localSession.Stop();
            localSession = null;
            NetworkLifecycle.Instance.AttachClient(null, null);
        }

        NetworkLifecycle.Instance.AttachAvatars(null);
    }

    // One avatar registry per session, whichever role starts first (a host starts both).
    private static void AttachAvatars()
    {
        if (NetworkLifecycle.Instance.Avatars == null)
            NetworkLifecycle.Instance.AttachAvatars(new PlayerAvatars(new AvatarPresentation()));
    }

    protected void OnApplicationQuit()
    {
        Stop();
    }

    /// <summary>Creates the session's object: the lifecycle (tick, role queries) and this root.</summary>
    public static void Create()
    {
        if (FindObjectOfType<NetworkLifecycle>() != null)
            throw new InvalidOperationException($"{nameof(NetworkLifecycle)} already exists!");
        NetworkLifecycle.DedicatedHostRequested = DedicatedHost.Requested;
        NetworkLifecycle.LocalSessionRequested = Environment.GetCommandLineArgs().Contains(LocalSessionArg);
        GameObject gameObject = new($"[{nameof(NetworkLifecycle)}]");
        gameObject.AddComponent<NetworkLifecycle>();
        gameObject.AddComponent<MultiplayerSession>();
        DontDestroyOnLoad(gameObject);
    }
}
