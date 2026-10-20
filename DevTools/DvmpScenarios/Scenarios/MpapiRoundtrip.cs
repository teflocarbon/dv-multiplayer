using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MPAPI;
using MPAPI.Interfaces.Packets;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// MPAPI mods' own packets, as a mod registers and sends them, on both games: the host's
    /// broadcast reaches the client's handler and (unless excluded) the host's own client handler;
    /// the client's and the host's own packets to the server reach its handler with the right
    /// sender. The host's client API is its in-process session's
    /// (stage 3 of docs/design/host-local-session.md). On a
    /// dedicated host there's no client API on the host: only the client's side is checked.
    /// </summary>
    [Scenario("mpapi-roundtrip", "A mod's packets between the games through MPAPI: broadcast to the client and the host's own client API, and to the server from both, with the right sender")]
    [Budget(90)]
    public sealed class MpapiRoundtrip : Scenario<MpapiRoundtrip.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            await pair.Both.Run(() => InGame.Setup());
            byte clientId = await pair.Client.Run(() => InGame.ClientApiPlayerId());

            await pair.Host.Run(() => InGame.Broadcast(1));
            await pair.Client.Run(() => InGame.SendToServer(2));
            if (!pair.Dedicated)
                await pair.Host.Run(() => InGame.SendToServer(3));
            await Task.Delay(2000);

            Received host = await pair.Host.Run(() => InGame.Received());
            Received client = await pair.Client.Run(() => InGame.Received());
            Report.Note($"host: server got [{string.Join(",", host.ToServer)}], client API got [{string.Join(",", host.ToClient)}]; " +
                        $"client API got [{string.Join(",", client.ToClient)}]");

            Expect.That(client.ToClient.Contains(1), "the client's handler got the host's broadcast");
            Expect.That(host.ToServer.Contains($"2 from {clientId}"), "the server's handler got the client's packet, from the client");
            if (pair.Dedicated)
                return;
            Expect.That(host.ToClient.Contains(1), "the host's own client handler got its broadcast");
            Expect.That(host.ToServer.Contains($"3 from {host.LocalPlayerId}"), "the server's handler got the host's own packet, from the host's player");
        }

        /// <summary>A mod's packet, server to clients.</summary>
        public sealed class ScenarioBroadcastPacket : IPacket
        {
            public int N { get; set; }
        }

        /// <summary>A mod's packet, a client to the server.</summary>
        public sealed class ScenarioRequestPacket : IPacket
        {
            public int N { get; set; }
        }

        public sealed class Received
        {
            public byte LocalPlayerId;
            public int[] ToClient;
            public string[] ToServer;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static readonly List<int> toClient = new();
            private static readonly List<string> toServer = new();

            /// <summary>Registers the handlers, as a mod does (registering again replaces them).</summary>
            [Step]
            public static bool Setup()
            {
                toClient.Clear();
                toServer.Clear();
                MultiplayerAPI.Server?.RegisterPacket<ScenarioRequestPacket>((packet, sender) => toServer.Add($"{packet.N} from {sender?.PlayerId}"));
                MultiplayerAPI.Client?.RegisterPacket<ScenarioBroadcastPacket>(packet => toClient.Add(packet.N));
                return true;
            }

            [Step]
            public static byte ClientApiPlayerId() => MultiplayerAPI.Client?.PlayerId ?? 0;

            [Step]
            public static bool Broadcast(int n)
            {
                MultiplayerAPI.Server.SendPacketToAll(new ScenarioBroadcastPacket { N = n });
                return true;
            }

            [Step]
            public static bool SendToServer(int n)
            {
                MultiplayerAPI.Client.SendPacketToServer(new ScenarioRequestPacket { N = n });
                return true;
            }

            [Step]
            public static Received Received() => new()
            {
                LocalPlayerId = MultiplayerAPI.Client?.PlayerId ?? 0,
                ToClient = toClient.ToArray(),
                ToServer = toServer.ToArray(),
            };
        }
    }
}
