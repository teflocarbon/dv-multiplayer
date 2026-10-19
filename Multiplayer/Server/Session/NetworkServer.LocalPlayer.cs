using System;
using Multiplayer.Core.Session;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Session;

// The host's own player, served in-process instead of through its loopback client
// (docs/design/host-local-session.md, stage 2).
public partial class NetworkServer : ILocalServer
{
    private readonly LocalRequestQueue<ServerPlayer> localRequests = new();
    private int applyingRemoteChange;

    /// <summary>
    ///     The host's own player: a player host's, or a dedicated host's service identity. Null until
    ///     its loopback login has been accepted.
    /// </summary>
    public ServerPlayer LocalPlayer => SelfId != 0 && TryGetServerPlayer(SelfId, out ServerPlayer player) ? player : null;

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
        if (localRequests.Count > 0)
            localRequests.Drain(LocalPlayer, (type, e) =>
            {
                if (e == null)
                    LogWarning($"Dropped the host's {type.Name}: the host's player isn't logged in");
                else
                    LogError($"Handling the host's {type.Name} threw: {e}");
            });
    }

    private sealed class ScopeExit : IDisposable
    {
        private Action onExit;
        public ScopeExit(Action onExit) => this.onExit = onExit;
        public void Dispose() { onExit?.Invoke(); onExit = null; }
    }
}
