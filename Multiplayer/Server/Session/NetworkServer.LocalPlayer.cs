using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using Multiplayer.Core.Session;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Transport;

namespace Multiplayer.Server.Session;

// The host's own player, served in-process instead of through its loopback client
// (docs/design/host-local-session.md, stage 2).
public partial class NetworkServer : ILocalServer
{
    private readonly LocalRequestQueue<ServerPlayer> localRequests = new();
    private int applyingRemoteChange;

    /// <summary>
    ///     The host's own player, a session in this process (<see cref="AddLocalPlayer"/>); null on a
    ///     dedicated host, which has no player, and before the server has started.
    /// </summary>
    public ServerPlayer LocalPlayer { get; private set; }

    /// <summary>
    ///     Admits the host's own player as a session in this process, with no connection
    ///     (docs/design/host-local-session.md, stage 3): the same admission and record binding as a
    ///     login, then it skips to the world state, since its game has the world (held until the save
    ///     has loaded). What the server sends it arrives through
    ///     <paramref name="peer"/>. Called as the server starts, so it gets the first player id.
    /// </summary>
    public ServerPlayer AddLocalPlayer(string username, string characterId, bool isVr, ITransportPeer peer)
    {
        if (LocalPlayer != null)
            throw new InvalidOperationException("The host's own player is already logged in");

        ServerPlayer player = AdmitPlayer(peer, username, username, PlayerIdentity.Local,
            Multiplayer.Settings.GetGuid(), 0, characterId, isVr, isLocalPlayer: true);
        Log($"The host's own player {player.Username} is player {player.PlayerId}, in-process");
        // Never fired for the host's loopback client, which skipped ReadyForGameData.
        PlayerConnected?.Invoke(player);
        OnServerboundLoadStateUpdatePacket(new ServerboundLoadStateUpdatePacket { LoadState = PlayerLoadingState.ReadyForWorldState }, peer);
        return player;
    }

    /// <summary>
    ///     True while the server applies another player's change to its own world (a junction, a
    ///     turntable, a time skip). <see cref="NetworkLifecycle.IsProcessingPacket"/> includes it,
    ///     so code that would send the change on doesn't, as when the host's client applied it.
    /// </summary>
    public bool IsApplyingRemoteChange => applyingRemoteChange > 0;

    /// <summary>Scope for applying another player's change: <c>using (ApplyingRemoteChange()) { ... }</c>.</summary>
    public IDisposable ApplyingRemoteChange()
    {
        applyingRemoteChange++;
        return new ScopeExit(() => applyingRemoteChange--);
    }

    public bool Submit<TRequest>(TRequest request) where TRequest : class => localRequests.Submit(request);

    private readonly Queue<byte[]> localPlayerPackets = new();
    private readonly NetDataReader localPlayerReader = new();

    public void ReceiveFromLocalPlayer(byte[] data) => localPlayerPackets.Enqueue(data);

    /// <summary>Registers the in-process handler for a request type: the same work as its packet handler.</summary>
    private void HandleLocal<TRequest>(Action<TRequest, ServerPlayer> handler) where TRequest : class =>
        localRequests.Handle(handler);

    // Each area registers its in-process handlers in its own file, by implementing its method.
    partial void RegisterLocalTrains();
    partial void RegisterLocalItems();
    partial void RegisterLocalWorld();
    partial void RegisterLocalStations();
    partial void RegisterLocalJobs();
    partial void RegisterLocalCareer();
    partial void RegisterLocalSession();

    private void RegisterLocalHandlers()
    {
        RegisterLocalTrains();
        RegisterLocalItems();
        RegisterLocalWorld();
        RegisterLocalStations();
        RegisterLocalJobs();
        RegisterLocalCareer();
        RegisterLocalSession();
    }

    public override void PollEvents()
    {
        base.PollEvents();
        UpdateLocalPlayerFromTracker();
        if (localRequests.Count > 0)
            localRequests.Drain(LocalPlayer, (type, e) =>
            {
                if (e == null)
                    LogWarning($"Dropped the host's {type.Name}: the host's player isn't logged in");
                else
                    LogError($"Handling the host's {type.Name} threw: {e}");
            });

        // MPAPI mods' packets from the host's player, read as from its peer like any player's.
        while (localPlayerPackets.Count > 0)
        {
            byte[] data = localPlayerPackets.Dequeue();
            if (LocalPlayer?.Peer == null)
                continue;
            localPlayerReader.SetSource(data);
            OnNetworkReceive(LocalPlayer.Peer, localPlayerReader, 0, DeliveryMethod.ReliableOrdered);
        }
    }

    private sealed class ScopeExit : IDisposable
    {
        private Action onExit;
        public ScopeExit(Action onExit) => this.onExit = onExit;
        public void Dispose() { onExit?.Invoke(); onExit = null; }
    }
}
