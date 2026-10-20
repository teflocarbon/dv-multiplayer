using DV.Utils;
using Multiplayer.Shared.Utils;
using System.Collections.Generic;
using System.Collections;
using System;
using UnityEngine.SceneManagement;
using UnityEngine;

namespace Multiplayer.Shared.Session;

/// <summary>
///     The session as every role sees it: the tick clock, and what this game is in the session (the
///     role queries). It names no role: the session root (<c>Multiplayer.MultiplayerSession</c>, in
///     the mod) starts and stops the server and the client and attaches them here as their shared
///     parts (<see cref="NetworkManager"/>, <see cref="ILocalServer"/>,
///     <see cref="ILocalPlayerSession"/>). Role code reaches its own role as
///     <c>NetworkServer.Instance</c> or <c>NetworkClient.Instance</c> (docs/design/code-layout.md).
/// </summary>
// https://revenantx.github.io/LiteNetLib/index.html
public class NetworkLifecycle : SingletonBehaviour<NetworkLifecycle>
{
    public const byte TICK_RATE = 24;
    private const float TICK_INTERVAL = 1.0f / TICK_RATE;
    // Ticks run at most once a frame, so below TICK_RATE fps the tick advances by the intervals
    // that passed (up to this many) to keep tick time in step with real time.
    private const int MAX_TICKS_PER_STEP = TICK_RATE * 2;

    public uint Tick { get; internal set; }
    public Action<uint> OnTick;

    // The roles running in this game, attached by the session root.
    private NetworkManager server;
    private NetworkManager client;

    public bool IsServerRunning => server?.IsRunning ?? false;
    public bool IsClientRunning => client?.IsRunning ?? false;

    /// <summary>
    ///     True while this game applies another player's change: the client handling a received
    ///     packet, or the server applying a change to its own world
    ///     (<see cref="ILocalServer.IsApplyingRemoteChange"/>). Code that would send that change on must not.
    /// </summary>
    public bool IsProcessingPacket => (client?.IsProcessingPacket ?? false) || (LocalServer?.IsApplyingRemoteChange ?? false);

    /// <summary>The server as the host's own player reaches it, in-process; null when this game isn't the server.</summary>
    public ILocalServer LocalServer { get; private set; }

    /// <summary>
    ///     The player at this keyboard (id, name, ping, the other players' avatars, chat); null when
    ///     no client is running. Code that runs for the host's player too reads this, not
    ///     <c>NetworkClient</c>: in host-local-session stage 3 the host has no client.
    /// </summary>
    public ILocalPlayerSession LocalSession { get; private set; }

    public bool IsReturningToMenu { get; private set; } = false;

    /// <summary>
    ///     Whether the caller is the host.
    ///     Note that this does NOT check authority, and should only be used for client-only logic.
    ///     Whether a <c>ServerPlayer</c> is the host's is the server's extension
    ///     <c>IsHost(player)</c> (Server/Session/NetworkLifecycleServerExtensions).
    /// </summary>
    public bool IsHost()
    {
        return IsServerRunning;
    }

    /// <summary>
    ///     Whether this game was started as a dedicated host (<c>-mp-dedicated</c>), set by the
    ///     session root from <c>DedicatedHost.Requested</c> before anything starts.
    /// </summary>
    public static bool DedicatedHostRequested { get; internal set; }

    /// <summary>
    ///     Whether this game is a dedicated host (<c>DedicatedHost</c>): hosting with nobody
    ///     playing it, so the local player is a parked body and the host's own client is a service
    ///     identity, not a player.
    /// </summary>
    public bool IsDedicatedHost => DedicatedHostRequested && IsServerRunning;

    /// <summary>
    ///     Whether the host's own player is the only player, so vanilla code that acts on the local
    ///     player is right. Never on a dedicated host, whose local player is a parked body.
    /// </summary>
    public bool IsHostPlayingAlone => IsServerRunning && !IsDedicatedHost && LocalServer.PlayerCount == 1;

    private readonly Queue<Action> mainMenuLoadedQueue = new();
    private readonly ExecutionTimer tickWatchdog = new(0.25f);

    protected override void Awake()
    {
        base.Awake();
        SceneSwitcher.SceneRequested += OnSceneSwitchRequested;
        SceneManager.sceneLoaded += (scene, _) =>
        {
            if (scene.buildIndex == (int)DVScenes.MainMenu)
                TriggerMainMenuEventLater();
        };
        StartCoroutine(PollEvents());
    }

    /// <summary>The session root attaches the server it started (null when it stops it).</summary>
    internal void AttachServer(NetworkManager manager, ILocalServer local)
    {
        server = manager;
        LocalServer = local;
    }

    /// <summary>The session root attaches the client it started (null when it stops it).</summary>
    internal void AttachClient(NetworkManager manager, ILocalPlayerSession session)
    {
        client = manager;
        LocalSession = session;
    }

    private void OnSceneSwitchRequested(DVScenes scene)
    {
        IsReturningToMenu = scene == DVScenes.MainMenu;
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

            if (client != null)
                TickManager(client);

            if (server != null)
                TickManager(server);

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
}
