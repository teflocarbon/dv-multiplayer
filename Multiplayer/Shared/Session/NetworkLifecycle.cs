using DV.Scenarios.Common;
using DV.Utils;
using LiteNetLib;
using MPAPI;
using Multiplayer.Client.Api;
using Multiplayer.Client.Session;
using Multiplayer.Client.UI;
using Multiplayer.Server.Api;
using Multiplayer.Server.Dedicated;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Utils;
using System.Collections.Generic;
using System.Collections;
using System.Net;
using System;
using UnityEngine.SceneManagement;
using UnityEngine;

namespace Multiplayer.Shared.Session;

// https://revenantx.github.io/LiteNetLib/index.html
public class NetworkLifecycle : SingletonBehaviour<NetworkLifecycle>
{
    public const byte TICK_RATE = 24;
    private const float TICK_INTERVAL = 1.0f / TICK_RATE;
    // Ticks run at most once a frame, so below TICK_RATE fps the tick advances by the intervals
    // that passed (up to this many) to keep tick time in step with real time.
    private const int MAX_TICKS_PER_STEP = TICK_RATE * 2;

    public LobbyServerData serverData;
    public bool IsPublicGame { get; set; } = false;
    public bool IsSinglePlayer { get; set; } = true;


    public NetworkServer Server { get; private set; }
    public NetworkClient Client { get; private set; }

    public uint Tick { get; internal set; }
    public Action<uint> OnTick;

    public bool IsServerRunning => Server?.IsRunning ?? false;
    public bool IsClientRunning => Client?.IsRunning ?? false;

    /// <summary>
    ///     True while this game applies another player's change: the client handling a received
    ///     packet, or the server applying a change to its own world (<see cref="NetworkServer.IsApplyingRemoteChange"/>).
    ///     Code that would send that change on must not.
    /// </summary>
    public bool IsProcessingPacket => (Client?.IsProcessingPacket ?? false) || (Server?.IsApplyingRemoteChange ?? false);

    /// <summary>The server as the host's own player reaches it, in-process; null when this game isn't the server.</summary>
    public ILocalServer LocalServer => Server;

    /// <summary>
    ///     The player at this keyboard (id, name, ping, the other players' avatars, chat); null when
    ///     no client is running. Code that runs for the host's player too reads this, not
    ///     <see cref="Client"/>: in host-local-session stage 3 the host has no client.
    /// </summary>
    public ILocalPlayerSession LocalSession => Client;

    public bool IsReturningToMenu { get; private set; } = false;

    private PlayerListGUI playerList;
    private NetworkStatsGui Stats;
    private readonly ExecutionTimer tickWatchdog = new(0.25f);

    /// <summary>
    ///     Whether the provided ITransportPeer is the host.
    ///     Note that this does NOT check authority, and should only be used for client-only logic.
    /// </summary>
    public bool IsHost(ServerPlayer player)
    {
        return Server?.IsRunning == true && Client?.IsRunning == true && Client.PlayerId == player.PlayerId;
    }

    /// <summary>
    ///     Whether the caller is the host.
    ///     Note that this does NOT check authority, and should only be used for client-only logic.
    /// </summary>
    public bool IsHost()
    {
        return Server?.IsRunning == true;
    }

    /// <summary>
    ///     Whether this game is a dedicated host (<see cref="DedicatedHost"/>): hosting with nobody
    ///     playing it, so the local player is a parked body and the host's own client is a service
    ///     identity, not a player.
    /// </summary>
    public bool IsDedicatedHost => DedicatedHost.Requested && IsServerRunning;

    /// <summary>
    ///     Whether the host's own player is the only player, so vanilla code that acts on the local
    ///     player is right. Never on a dedicated host, whose local player is a parked body.
    /// </summary>
    public bool IsHostPlayingAlone => IsServerRunning && !IsDedicatedHost && Server.PlayerCount == 1;

    private readonly Queue<Action> mainMenuLoadedQueue = new();

    protected override void Awake()
    {
        base.Awake();
        playerList = gameObject.AddComponent<PlayerListGUI>();
        Stats = gameObject.AddComponent<NetworkStatsGui>();
        //RegisterPackets();
        WorldStreamingInit.LoadingFinished += () => { playerList.RegisterListeners(); };
        Settings.OnSettingsUpdated += OnSettingsUpdated;
        SceneSwitcher.SceneRequested += OnSceneSwitchRequested;
        SceneManager.sceneLoaded += (scene, _) =>
        {
            if (scene.buildIndex != (int)DVScenes.MainMenu)
                return;

            playerList.UnRegisterListeners();
            TriggerMainMenuEventLater();
            DedicatedHost.OnMainMenu();
        };
        if (DedicatedHost.Requested)
        {
            gameObject.AddComponent<DedicatedHostMode>();
            // The mod can load after the menu scene already has.
            if (SceneManager.GetActiveScene().buildIndex == (int)DVScenes.MainMenu)
                DedicatedHost.OnMainMenu();
        }
        StartCoroutine(PollEvents());
    }

    private void OnSceneSwitchRequested(DVScenes scene)
    {
        IsReturningToMenu = scene == DVScenes.MainMenu;
    }

    private void OnSettingsUpdated(Settings settings)
    {
        if (!IsClientRunning && !IsServerRunning)
            return;
        if (settings.ShowStats)
            Stats.Show(Client.Statistics, Server?.Statistics);
        else
            Stats.Hide();
    }

    public void TriggerMainMenuEventLater()
    {
        SingletonBehaviour<CoroutineManager>.Instance.StartCoroutine(TriggerMainMenuEvent());
    }

    private IEnumerator TriggerMainMenuEvent()
    {
        yield return null;
        while (mainMenuLoadedQueue.Count > 0)
            mainMenuLoadedQueue.Dequeue().Invoke();
    }

    public void QueueMainMenuEvent(Action action)
    {
        mainMenuLoadedQueue.Enqueue(action);
    }

    public bool StartServer(IDifficulty difficulty)
    {
        int port = Multiplayer.Settings.Port;

        if (Server != null)
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

        Server = server;
        DebugRuntime.SetRole("host", Multiplayer.Settings.GetUserName());

        // Register server API
        var serverAPI = new ServerAPIProvider(server);
        MultiplayerAPI.RegisterServer(serverAPI);

        StartClient(IPAddress.Loopback.ToString(), port, Multiplayer.Settings.Password, IsSinglePlayer, null);

        //reset for next game
        IsSinglePlayer = true;
        serverData = null;

        return true;
    }

    public void StartClient(string address, int port, string password, bool isSinglePlayer, Action<DisconnectReason, string> onDisconnect)
    {
        if (Client != null)
            throw new InvalidOperationException("NetworkManager already exists!");
        NetworkClient client = new(Multiplayer.Settings, isSinglePlayer);
        client.Start(address, port, password, isSinglePlayer, onDisconnect);

        Client = client;
        if (Server == null)
            DebugRuntime.SetRole("client", client.Username);

        // Register client API
        var clientAPI = new ClientAPIProvider(client);
        MultiplayerAPI.RegisterClient(clientAPI);

        OnSettingsUpdated(Multiplayer.Settings); // Show stats if enabled
    }

    private IEnumerator PollEvents()
    {
        float nextTickAt = Time.realtimeSinceStartup;
        while (!UnloadWatcher.isQuitting)
        {
            // Sample ages and timeouts are counted in ticks. A peer below 24 fps used to tick
            // slower than real time, so on a client a late sample looked fresh (its train trailed
            // the host's by metres while the frame rate was low).
            float now = Time.realtimeSinceStartup;
            int steps = 1 + Mathf.Clamp(Mathf.FloorToInt((now - nextTickAt) / TICK_INTERVAL), 0, MAX_TICKS_PER_STEP - 1);
            Tick += (uint)steps;
            nextTickAt = now - nextTickAt > MAX_TICKS_PER_STEP * TICK_INTERVAL
                ? now + TICK_INTERVAL // a long stall (loading): carry on from now
                : nextTickAt + steps * TICK_INTERVAL;

            tickWatchdog.Start();
            try
            {
                if (!UnloadWatcher.isUnloading && !UnloadWatcher.isQuitting && !IsReturningToMenu)
                {
                    // Invoke subscribers independently. A fault in an optional subsystem
                    // must not suppress player tracking, item authority, or every handler
                    // registered after it for the remainder of this tick.
                    Action<uint> handlers = OnTick;
                    if (handlers != null)
                    {
                        foreach (Action<uint> handler in handlers.GetInvocationList())
                        {
                            try
                            {
                                handler(Tick);
                            }
                            catch (Exception e)
                            {
                                string owner = handler.Method.DeclaringType?.FullName ?? "unknown";
                                Multiplayer.LogError($"Exception in OnTick subscriber {owner}.{handler.Method.Name}: {e}");
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (!UnloadWatcher.isUnloading && !UnloadWatcher.isQuitting && !IsReturningToMenu)
                    Multiplayer.LogError($"Exception while processing OnTick: {e}");
            }
            finally
            {
                tickWatchdog.Stop(time => Multiplayer.LogWarning($"OnTick took {time} ms!"));
            }

            if (Client != null)
                TickManager(Client);

            if (Server != null)
                TickManager(Server);

            float remainingTime = nextTickAt - Time.realtimeSinceStartup;
            yield return remainingTime < 0.001f ? null : new WaitForSecondsRealtime(remainingTime);
        }
    }

    private void TickManager(NetworkManager manager)
    {
        if (manager == null)
            return;

        tickWatchdog.Start();

        try
        {
            manager.PollEvents();
        }
        catch (Exception e)
        {
            manager.LogError($"Exception while polling events: {e}");
        }
        finally
        {
            tickWatchdog.Stop(time => manager.LogWarning($"PollEvents took {time} ms!"));
        }
    }

    public void Stop()
    {
        Stats?.Hide();

        if (Server != null)
        {
            Server?.Stop();
            MultiplayerAPI.ClearServer();
            Server = null;
        }

        if (Client != null)
        {
            Client?.Stop();
            MultiplayerAPI.ClearClient();
            Client = null;
        }
    }

    protected void OnApplicationQuit()
    {
        Stop();
    }

    public static void CreateLifecycle()
    {
        if (FindObjectOfType<NetworkLifecycle>() != null)
            throw new InvalidOperationException($"{nameof(NetworkLifecycle)} already exists!");
        GameObject gameObject = new($"[{nameof(NetworkLifecycle)}]");
        gameObject.AddComponent<NetworkLifecycle>();
        DontDestroyOnLoad(gameObject);
    }

}
