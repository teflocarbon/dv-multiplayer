using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Client.UI;
using Multiplayer.Server.Chat;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Players;
using Multiplayer.Shared.Session;
using TMPro;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// What the server sends the host's own player reaches it (docs/design/host-local-session.md,
    /// stage 3: through its LocalPeer).
    /// On a player host: the client's chat line and its whisper show in the host's chat; the host's
    /// `/help` is answered to the host alone; the host sets its own crew name with `/crew` and its
    /// player list row shows it, as do the client's avatar of the host and the client's player list
    /// (cleared afterwards). The host's player list has a row for the client, and its avatar of the
    /// client wears a name tag. On a dedicated host, which has no player: the client's `/help` is
    /// answered to the client. `host-session` covers the other direction (the host's chat and
    /// whispers reaching the client).
    /// </summary>
    [Scenario("host-chat", "Chat, whispers, command replies and the crew name reach the host's own player; its player list shows the client")]
    [Budget(120)]
    public sealed class HostChat : Scenario<HostChat.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            string nonce = "hc" + System.DateTime.UtcNow.Ticks % 100000000;
            Names names = await pair.Host.Run(() => InGame.Names());
            Report.Note($"host player '{names.Host}', client player {names.ClientId} '{names.Client}'");

            int clientHelp = await pair.Client.Run(() => InGame.HelpLines());
            await pair.Client.Run(() => InGame.Say("/help"));
            await Until.True(async () => await pair.Client.Run(() => InGame.HelpLines()) > clientHelp, 10,
                "the client's /help is answered in its chat");

            if (pair.Dedicated) return;
            Require.That(names.Host != null, "the server knows the host's own player");

            await pair.Client.Run(() => InGame.Say(nonce + "-said"));
            await Until.True(async () => (await pair.Host.Run(() => InGame.Lines())).Any(l => l.Contains(nonce + "-said")), 10,
                "the client's chat line shows in the host's chat");

            await pair.Client.Run(() => InGame.Say("/w " + names.Host + " " + nonce + "-whisper"));
            await Until.True(async () => (await pair.Host.Run(() => InGame.Lines())).Any(l => l.Contains(nonce + "-whisper")), 10,
                "the client's whisper shows in the host's chat");

            clientHelp = await pair.Client.Run(() => InGame.HelpLines());
            int hostHelp = await pair.Host.Run(() => InGame.HelpLines());
            await pair.Host.Run(() => InGame.Say("/help"));
            await Until.True(async () => await pair.Host.Run(() => InGame.HelpLines()) > hostHelp, 10,
                "the host's /help is answered in its chat");
            await Task.Delay(2000);
            Expect.Equal(clientHelp, await pair.Client.Run(() => InGame.HelpLines()),
                "the host's /help isn't answered to the client (help lines in the client's chat)");

            string crew = nonce.Substring(nonce.Length - 6);
            pair.Cleanup(() => pair.Host.Run(() => InGame.Say("/crew " + names.Host)));
            await pair.Host.Run(() => InGame.Say("/crew " + names.Host + " " + crew));
            await Until.True(async () => (await pair.Host.Run(() => InGame.PlayerList())).Any(r => r.Contains("[" + crew + "] " + names.Host)), 10,
                "the host's player list shows its own crew name");
            await Until.Value(() => pair.Client.Run(() => InGame.AvatarCrew(names.HostId)), c => c == crew, 10,
                "the client's avatar of the host has the host's crew name");
            Expect.That((await pair.Client.Run(() => InGame.PlayerList())).Any(r => r.Contains("[" + crew + "] " + names.Host)),
                "the client's player list shows the host's crew name");

            string[] rows = await pair.Host.Run(() => InGame.PlayerList());
            Report.Note("host's player list: " + string.Join(" | ", rows));
            Expect.That(rows.Any(r => r.Contains(names.Client) && r.EndsWith("ms)")), "the host's player list has a row for the client, with its ping");
            Expect.That(await pair.Host.Run(() => InGame.HasNameTag(names.ClientId)), "the host's avatar of the client wears a name tag");
        }

        public sealed class Names
        {
            public string Host, Client;
            public byte HostId, ClientId;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the host's own player (none on a dedicated host) and the
            /// client's, the one other player, from the server.</summary>
            [Step]
            public static Names Names()
            {
                ServerPlayer local = NetworkServer.Instance.LocalPlayer;
                ServerPlayer client = NetworkServer.Instance.ServerPlayers.First(p => p != local);
                return new Names { Host = local?.Username, HostId = local?.PlayerId ?? 0, Client = client.Username, ClientId = client.PlayerId };
            }

            /// <summary>Either peer: sends a chat line or command as this game's player, as the chat box does.</summary>
            [Step]
            public static void Say(string text) => NetworkLifecycle.Instance.LocalSession.SendChat(text);

            /// <summary>Either peer: the lines in this game's chat window, oldest first; none without a window.</summary>
            [Step]
            public static string[] Lines()
            {
                ChatGUI chat = Object.FindObjectOfType<ChatGUI>();
                return chat == null
                    ? new string[0]
                    : chat.messageList.Where(m => m != null).Select(m => m.GetComponent<TextMeshProUGUI>().text).ToArray();
            }

            /// <summary>Either peer: how many lines in this game's chat window are a <c>/help</c> answer.</summary>
            [Step]
            public static int HelpLines() => Lines().Count(l => l.Contains("<color=#" + ChatManager.MESSAGE_COLOUR_HELP));

            /// <summary>Either peer: the player list's rows, as it draws them.</summary>
            [Step]
            public static string[] PlayerList() => Object.FindObjectOfType<PlayerListGUI>().GetPlayerList().ToArray();

            /// <summary>Either peer: the crew name on this game's avatar of a player; null without one.</summary>
            [Step]
            public static string AvatarCrew(byte playerId) =>
                NetworkLifecycle.Instance.Avatars != null && NetworkLifecycle.Instance.Avatars.TryGetPlayer(playerId, out NetworkedPlayer p) ? p.CrewName : null;

            /// <summary>Either peer: whether this game's avatar of a player has its name tag.</summary>
            [Step]
            public static bool HasNameTag(byte playerId) =>
                NetworkLifecycle.Instance.Avatars != null && NetworkLifecycle.Instance.Avatars.TryGetPlayer(playerId, out NetworkedPlayer p) &&
                p.GetComponent<Multiplayer.Client.Players.NetworkedPlayerNameTag>() != null;
        }
    }
}
