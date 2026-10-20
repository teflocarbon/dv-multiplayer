using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Transport;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The host's own player (stage 3 of docs/design/host-local-session.md): a player host's player
    /// is an in-process session of its server, with player id 1 and a LocalPeer, no client of its
    /// own, its loading screen down and its chat window built; a dedicated host has no player at all.
    /// Either way each game has an avatar for the other's player and none for its own.
    /// </summary>
    [Scenario("local-session", "The host's player is an in-process session (or none on a dedicated host): ids, loading screen, chat window and avatars on both games")]
    [Budget(120)]
    public sealed class LocalSession : Scenario<LocalSession.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            HostState host = await pair.Host.Run(() => InGame.Host());
            ClientState client = await pair.Client.Run(() => InGame.Client());
            Report.Note($"host: local player {host.LocalPlayerId} ({host.LocalPeer}), session {host.SessionPlayerId}, " +
                        $"avatars [{string.Join(",", host.Avatars)}]; client: player {client.PlayerId}, avatars [{string.Join(",", client.Avatars)}]");

            Expect.That(!host.Loading, "the host's game is out of loading mode");
            Expect.That(!client.Loading, "the client's game is out of loading mode");
            Expect.That(host.Avatars.Contains(client.PlayerId), "the host has an avatar for the client's player");
            Expect.That(host.LocalPlayerId == 0 || !host.Avatars.Contains(host.LocalPlayerId), "the host has no avatar of its own player");

            if (pair.Dedicated)
            {
                Expect.That(!client.Avatars.Contains(host.LocalPlayerId) || host.LocalPlayerId == 0, "the client has no avatar for a dedicated host");
                Expect.That(!host.ChatWindow, "a dedicated host builds no chat window");
                Expect.Equal<byte>(0, host.LocalPlayerId, "a dedicated host has no player of its own");
                Expect.That(!host.HasSession, "a dedicated host has no local player session");
                Expect.That(!host.ClientRunning, "a dedicated host runs no client");
                return;
            }

            Expect.That(host.LocalPlayerId != 0, "the server knows the host's own player");
            Expect.Equal(host.LocalPlayerId, host.SessionPlayerId, "the host's session and its server agree on its player id");
            Expect.That(client.Avatars.Contains(host.LocalPlayerId), "the client has an avatar for the host's player");
            Expect.That(host.ChatWindow, "the host's chat window is built");
            Expect.Equal<byte>(1, host.LocalPlayerId, "the host's player is player 1");
            Expect.That(host.LocalPeer == nameof(LocalPeer), "the host's player's peer is in-process");
            Expect.That(!host.ClientRunning, "the host runs no client of its own");
        }

        public sealed class HostState
        {
            public byte LocalPlayerId, SessionPlayerId;
            public string LocalPeer;
            public bool HasSession, ClientRunning, Loading, ChatWindow;
            public byte[] Avatars;
        }

        public sealed class ClientState
        {
            public byte PlayerId;
            public bool Loading;
            public byte[] Avatars;
        }

        // ---- runs inside the games
        public static class InGame
        {
            [Step]
            public static HostState Host()
            {
                NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
                ServerPlayer local = NetworkServer.Instance.LocalPlayer;
                return new HostState
                {
                    LocalPlayerId = local?.PlayerId ?? 0,
                    LocalPeer = local?.Peer?.GetType().Name ?? "none",
                    SessionPlayerId = lifecycle.LocalSession?.PlayerId ?? 0,
                    HasSession = lifecycle.LocalSession != null,
                    ClientRunning = lifecycle.IsClientRunning,
                    Loading = LoadingScreenManager.IsLoading,
                    ChatWindow = Object.FindObjectOfType<Multiplayer.Client.UI.ChatGUI>() != null,
                    Avatars = lifecycle.Avatars?.Players.Select(p => p.PlayerId).ToArray() ?? new byte[0],
                };
            }

            [Step]
            public static ClientState Client()
            {
                NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
                return new ClientState
                {
                    PlayerId = lifecycle.LocalSession?.PlayerId ?? 0,
                    Loading = LoadingScreenManager.IsLoading,
                    Avatars = lifecycle.Avatars?.Players.Select(p => p.PlayerId).ToArray() ?? new byte[0],
                };
            }
        }
    }
}
