using Multiplayer.Server.Career;
using Multiplayer.Server.Dedicated;
using Multiplayer.Server.Players;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Session;
using DV.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Multiplayer.Server.Chat;

public delegate void ChatCommandCallbackInternal(string message, ServerPlayer sender);
public delegate bool ChatFilterDelegateInternal(ref string message, ServerPlayer sender);

public class ChatManager
{
    public const string COMMAND_SERVER = "server";
    public const string COMMAND_SERVER_SHORT = "s";
    public const string COMMAND_WHISPER = "whisper";
    public const string COMMAND_WHISPER_SHORT = "w";
    public const string COMMAND_HELP = "help";
    public const string COMMAND_HELP_SHORT = "?";
    public const string COMMAND_LOG = "log";
    public const string COMMAND_LOG_SHORT = "l";
    public const string COMMAND_KICK = "kick";
    public const string COMMAND_SET_CREW_NAME = "crew";
    public const string COMMAND_SET_CREW_NAME_SHORT = "sc";
    public const string COMMAND_SAVE = "save";
    public const string COMMAND_STOP = "stop";
    public const string COMMAND_PAY_FEES = "payfees";
    public const string COMMAND_GIVE = "give";
    public const string COMMAND_RESTORE = "restore";
    public const string COMMAND_WEATHER = "weather";
    public const string COMMAND_TIME = "time";
    public const string COMMAND_REPAIR = "repair";
    public const string COMMAND_SEASON = "season";


    public const string MESSAGE_COLOUR_SERVER = "9CDCFE";
    public const string MESSAGE_COLOUR_HELP = "00FF00";

    private readonly Dictionary<string, ChatCommandCallbackInternal> _registeredCommands = [];
    private readonly Dictionary<string, Func<string>> _registeredHelpMessages = [];
    private readonly List<ChatFilterDelegateInternal> _chatFilters = [];

    public ChatManager()
    {
        RegisterBuiltInCommands();
    }

    private void RegisterBuiltInCommands()
    {
        RegisterChatCommand
        (
            COMMAND_SERVER,
            COMMAND_SERVER_SHORT,
            () => $"{Locale.CHAT_HELP_SERVER_MSG}" +
                    $"\r\n\t\t/{COMMAND_SERVER} <{Locale.CHAT_HELP_MSG}>" +
                    $"\r\n\t\t/{COMMAND_SERVER_SHORT} <{Locale.CHAT_HELP_MSG}>",
            (message, sender) => ServerMessage(message, sender, null)
        );

        RegisterChatCommand
        (
            COMMAND_WHISPER,
            COMMAND_WHISPER_SHORT,
            () => $"{Locale.CHAT_HELP_WHISPER_MSG}" +
                    $"\r\n\t\t/{COMMAND_WHISPER} <{Locale.CHAT_HELP_PLAYER_NAME}> <{Locale.CHAT_HELP_MSG}>" +
                    $"\r\n\t\t/{COMMAND_WHISPER_SHORT} <{Locale.CHAT_HELP_PLAYER_NAME}> <{Locale.CHAT_HELP_MSG}>",
            WhisperMessage
        );

        RegisterChatCommand
        (
            COMMAND_HELP,
            COMMAND_HELP_SHORT,
            () => $"{Locale.CHAT_HELP_HELP}" +
                    $"\r\n\t\t/{COMMAND_HELP}" +
                    $"\r\n\t\t/{COMMAND_HELP_SHORT}",
            HelpMessage
        );

        RegisterChatCommand
        (
            COMMAND_KICK,
            null,
            () => $"Kick a player from the server (host only)" +
                    $"\r\n\t\t/{COMMAND_KICK}",
            KickMessage
        );

        RegisterChatCommand
        (
            COMMAND_SET_CREW_NAME,
            COMMAND_SET_CREW_NAME_SHORT,
            () => $"{Locale.CHAT_HELP_SET_CREW_NAME_HOST_MSG}" +
                    $"\r\n\t\t/{COMMAND_SET_CREW_NAME} <{Locale.CHAT_HELP_PLAYER_NAME}> <{Locale.CHAT_HELP_CREW_NAME}>" +
                    $"\r\n\t\t/{COMMAND_SET_CREW_NAME_SHORT} <{Locale.CHAT_HELP_PLAYER_NAME}> <{Locale.CHAT_HELP_CREW_NAME}>" +
                    $"\r\n{Locale.CHAT_HELP_SET_CREW_NAME_CLIENT_MSG}" +
                    $"\r\n\t\t/{COMMAND_SET_CREW_NAME} <{Locale.CHAT_HELP_CREW_NAME}>" +
                    $"\r\n\t\t/{COMMAND_SET_CREW_NAME_SHORT} <{Locale.CHAT_HELP_CREW_NAME}>",
                SetCrewNameMessage
        );

        RegisterChatCommand
        (
            COMMAND_SAVE,
            null,
            () => "Save the game now (admins)" +
                    $"\r\n\t\t/{COMMAND_SAVE}",
            SaveMessage
        );

        RegisterChatCommand
        (
            COMMAND_STOP,
            null,
            () => "Save and shut a dedicated server down (admins)" +
                    $"\r\n\t\t/{COMMAND_STOP}",
            StopMessage
        );

        RegisterChatCommand
        (
            COMMAND_PAY_FEES,
            null,
            () => "Pay the fees from the shared wallet, cheapest first, until insurance clears the rest (admins)" +
                    $"\r\n\t\t/{COMMAND_PAY_FEES}",
            PayFeesMessage
        );

        RegisterChatCommand
        (
            COMMAND_GIVE,
            null,
            () => "Give a player an item, into their inventory, theirs to keep (admins). Item names are DV's, e.g. EOTLantern, Boombox" +
                    $"\r\n\t\t/{COMMAND_GIVE} <{Locale.CHAT_HELP_PLAYER_NAME}> <item> [count]",
            GiveMessage
        );

        RegisterChatCommand
        (
            COMMAND_RESTORE,
            null,
            () => "Give a player back any starting items they no longer own (admins)" +
                    $"\r\n\t\t/{COMMAND_RESTORE} <{Locale.CHAT_HELP_PLAYER_NAME}>",
            RestoreMessage
        );

        RegisterChatCommand
        (
            COMMAND_WEATHER,
            null,
            () => "Set the weather for everyone (admins): clear, rain or storm for some in-game hours (default 2), auto to go back to natural weather, new for a different natural pattern" +
                    $"\r\n\t\t/{COMMAND_WEATHER} clear|rain|storm [hours]" +
                    $"\r\n\t\t/{COMMAND_WEATHER} auto|new",
            WeatherMessage
        );

        RegisterChatCommand
        (
            COMMAND_TIME,
            null,
            () => "Skip time forward for everyone (admins): to the next time of day, or by some hours. It only goes forward, as sleeping does" +
                    $"\r\n\t\t/{COMMAND_TIME} HH:mm" +
                    $"\r\n\t\t/{COMMAND_TIME} +hours",
            TimeMessage
        );

        RegisterChatCommand
        (
            COMMAND_REPAIR,
            null,
            () => "Fully repair the car a player is in, as a service station does (admins); all for their whole train. Without a name, your own" +
                    $"\r\n\t\t/{COMMAND_REPAIR} [{Locale.CHAT_HELP_PLAYER_NAME}] [all]",
            RepairMessage
        );

        RegisterChatCommand
        (
            COMMAND_SEASON,
            null,
            () => "With the Dynamic Seasons mod: set the season for everyone, move on to the next, or start or end a blizzard in winter (admins). Without an argument, the current season" +
                    $"\r\n\t\t/{COMMAND_SEASON} spring|summer|autumn|winter|next" +
                    $"\r\n\t\t/{COMMAND_SEASON} blizzard|calm",
            SeasonMessage
        );

#if DEBUG
        RegisterChatCommand
        (
            COMMAND_LOG,
            COMMAND_LOG_SHORT,
            null,
            (args, sender) =>
                Multiplayer.specLog = !Multiplayer.specLog);
#endif
    }

    public bool RegisterChatCommand(string commandLong, string commandShort, Func<string> helpMessage, ChatCommandCallbackInternal callback)
    {
        if (string.IsNullOrEmpty(commandLong) || callback == null)
            return false;

        if (_registeredCommands.ContainsKey(commandLong.ToLower()) ||
            (!string.IsNullOrEmpty(commandShort) && _registeredCommands.ContainsKey(commandShort.ToLower())))
            return false;

        _registeredCommands[commandLong.ToLower()] = callback;

        if (!string.IsNullOrEmpty(commandShort) && !_registeredCommands.ContainsKey(commandShort.ToLower()))
        {
            _registeredCommands[commandShort.ToLower()] = callback;
        }

        if (helpMessage != null)
        {
            _registeredHelpMessages[commandLong.ToLower()] = helpMessage;
        }

        return true;
    }

    public void RegisterChatFilter(ChatFilterDelegateInternal callback)
    {
        if (callback != null)
        {
            _chatFilters.Add(callback);
        }
    }

    public void ProcessMessage(string message, ServerPlayer sender)
    {

        if (string.IsNullOrEmpty(message))
            return;

        Multiplayer.LogDebug(() => $"ProcessMessage(\'{message}\')");

        //Check if we have a command
        if (message.StartsWith("/"))
        {
            string[] messageParams = message.Substring(1).Split(' ');
            string command = messageParams[0].ToLower();

            Multiplayer.LogDebug(() => $"ProcessMessage(\'{message}\') starts with, substr: {message.Substring(0)}), command: {command}");

            //check registered commands
            if (!string.IsNullOrEmpty(command) && _registeredCommands.TryGetValue(command, out var commandCallback))
            {
                //remove the command, leading slash and trailing space
                var cleanedMessage = message.Substring(command.Length + 1).Trim();

                Multiplayer.LogDebug(() => $"ProcessMessage(\'{message}\') cleaned message: {cleanedMessage}");

                commandCallback(cleanedMessage, sender);

                return;
            }
        }

        //not a server command, process as normal message
        ProcessChatMessage(message, sender);
    }

    private void ProcessChatMessage(string message, ServerPlayer sender)
    {
        if (sender == null)
            return;

        //clean up the message to stop format injection
        message = Regex.Replace(message, "</noparse>", string.Empty, RegexOptions.IgnoreCase);

        //call each filter until either a filter returns false or all filters have been called
        foreach (var filter in _chatFilters)
        {
            if (!filter(ref message, sender))
                return;
        }

        message = $"<alpha=#50>{sender.DisplayName}:</color> <noparse>{message}</noparse>";
        NetworkServer.Instance.SendChat(message, sender);
    }

    /// <summary>
    /// Whether a player may use the admin commands: the host's own player; on a dedicated host, its
    /// owner (CareerOwner.Id: the Steam account running it, or -mp-owner); and anyone in
    /// Settings.Admins. Player IDs are verified Steam accounts (PlayerAuthentication), except on a
    /// host with -mp-allow-unverified.
    /// </summary>
    public static bool IsAdmin(ServerPlayer player)
    {
        if (player == null)
            return false;
        // The server's console sends only what was typed in it.
        if (IsConsole(player))
            return runningConsoleCommand;
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        return lifecycle.IsHost(player) ||
               (lifecycle.IsDedicatedHost && player.Guid == CareerOwner.Id) ||
               Multiplayer.Settings.IsListedAdmin(player.Guid);
    }

    private static void Reply(ServerPlayer player, string message) =>
        Tell($"<color=#{MESSAGE_COLOUR_SERVER}>{message}</color>", player);

    // A whisper, or for the console its log, where the watchdog's window shows it.
    private static void Tell(string message, ServerPlayer player)
    {
        if (runningConsoleCommand && IsConsole(player))
        {
            string plain = Regex.Replace(message ?? string.Empty, "<[^>]+>", string.Empty)
                .Replace("\r\n\t\t", "  ").Replace("\r\n\t", "\n  ").Replace("\r\n", "\n");
            Multiplayer.Log("[Console] " + plain);
            return;
        }
        NetworkServer.Instance.SendWhisper(message, player);
    }

    private static bool runningConsoleCommand;

    // Sends a dedicated host's console commands (it has no player of its own).
    private ServerPlayer consoleSender;

    private static bool IsConsole(ServerPlayer player) => player != null && player.IsConsole;

    /// <summary>
    /// A line typed in a dedicated host's console (DedicatedHost.RunConsoleCommands). It runs as the
    /// server's console sender (<see cref="ServerPlayer.CreateConsole"/>), which is an admin for it,
    /// and the replies go to the log. A line without a slash is said in chat as the server.
    /// </summary>
    public void RunConsoleCommand(string line)
    {
        NetworkServer server = NetworkServer.Instance;
        ServerPlayer console = server != null ? consoleSender ??= ServerPlayer.CreateConsole(DedicatedHost.ServiceName) : null;
        if (console == null)
        {
            Multiplayer.LogWarning($"[Console] the server isn't ready for commands yet: {line}");
            return;
        }
        if (!line.StartsWith("/"))
            line = $"/{COMMAND_SERVER} {line}";
        string command = line.Substring(1).Split(' ')[0].ToLower();
        if (!_registeredCommands.ContainsKey(command))
        {
            Multiplayer.Log($"[Console] unknown command /{command}; /help lists them");
            return;
        }
        runningConsoleCommand = true;
        try
        {
            ProcessMessage(line, console);
        }
        catch (Exception e)
        {
            Multiplayer.LogError($"[Console] {line} failed: {e}");
        }
        finally
        {
            runningConsoleCommand = false;
        }
    }

    // A player by name, ignoring case, or by the start of their name if only one matches.
    private static ServerPlayer FindPlayer(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        var players = NetworkServer.Instance.ServerPlayers.ToList();
        ServerPlayer exact = players.FirstOrDefault(p => string.Equals(p.Username, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;
        var starting = players.Where(p => p.Username.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return starting.Count == 1 ? starting[0] : null;
    }

    private void GiveMessage(string message, ServerPlayer sender)
    {
        if (!IsAdmin(sender))
            return;
        string[] parts = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            Reply(sender, $"/{COMMAND_GIVE} <player> <item> [count]");
            return;
        }
        ServerPlayer player = FindPlayer(parts[0]);
        if (player == null)
        {
            Reply(sender, $"No player called {parts[0]}.");
            return;
        }
        if (!AdminItemGrants.CanReceive(player, out string notNow))
        {
            Reply(sender, notNow);
            return;
        }
        InventoryItemSpec spec = AdminItemGrants.Find(parts[1], out string[] near);
        if (spec == null)
        {
            Reply(sender, near.Length > 0 ? $"No item called {parts[1]}. Did you mean: {string.Join(", ", near)}?" : $"No item called {parts[1]}.");
            return;
        }
        int count = parts.Length > 2 && int.TryParse(parts[2], out int n) ? Math.Max(1, Math.Min(n, AdminItemGrants.MaxCount)) : 1;
        if (spec.IsEssential)
        {
            // DV allows one of each essential per player.
            if (AdminItemGrants.OwnedPrefabs(player).Contains(spec.ItemPrefabName))
            {
                Reply(sender, $"{player.Username} already has a {spec.ItemPrefabName} somewhere, and it's one per player. /{COMMAND_RESTORE} gives back only what's missing.");
                return;
            }
            count = 1;
        }
        CoroutineManager.Instance.StartCoroutine(AdminItemGrants.Spawn(player, Enumerable.Repeat(spec, count).ToList()));
        Multiplayer.Log($"{sender.Username} gave {player.Username} {count} x {spec.ItemPrefabName}");
        Reply(sender, $"Giving {player.Username} {count} x {spec.ItemPrefabName}.");
        if (sender != player)
            Reply(player, $"You've been given {count} x {spec.ItemPrefabName}, in your inventory (or your Lost and Found if it's full).");
    }

    // DV's "starting weather": the override a new career starts with, which holds a weather for a
    // while and then fades back to the natural one. It's in the weather state the server sends
    // everyone (ClientboundWeatherPacket), so every game shows the same.
    private void WeatherMessage(string message, ServerPlayer sender)
    {
        if (!IsAdmin(sender))
            return;
        string[] parts = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string kind = parts.FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;
        var weather = DV.WeatherSystem.WeatherDriver.Instance;
        if (weather == null)
        {
            Reply(sender, "The weather isn't running yet.");
            return;
        }
        float hours = parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float h) ? Math.Max(0.25f, Math.Min(h, 72f)) : 2f;
        (float rain, float thunder, float wetness)? set = kind switch
        {
            "clear" or "sun" or "sunny" => (0f, 0f, 0f),
            "rain" => (0.7f, 0f, 0.7f),
            "storm" or "thunder" => (1f, 0.8f, 1f),
            _ => null
        };
        string said;
        if (set is { } w)
        {
            weather.SetStartingWeather(weather.manager.DateTime + TimeSpan.FromHours(hours), TimeSpan.FromMinutes(20),
                UnityEngine.Vector2.zero, w.rain, w.thunder, w.wetness);
            said = $"{sender.Username} set the weather to {kind} for {hours:0.##} in-game hour(s).";
        }
        else if (kind == "auto")
        {
            weather.ResetStartingWeather();
            said = $"{sender.Username} set the weather back to natural.";
        }
        else if (kind == "new")
        {
            weather.ResetStartingWeather();
            var seed = typeof(DV.WeatherSystem.WeatherDriver).GetField("weatherSeed",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (seed == null)
            {
                Reply(sender, "This version of the game doesn't let the weather pattern change.");
                return;
            }
            seed.SetValue(weather, UnityEngine.Random.Range(0f, 1000f));
            said = $"{sender.Username} rolled a new weather pattern.";
        }
        else
        {
            Reply(sender, $"/{COMMAND_WEATHER} clear|rain|storm [hours], or auto, or new");
            return;
        }
        NetworkServer.Instance.SendWeatherState();
        Multiplayer.Log(said);
        ServerMessage(said, null);
    }

    // A time skip, as fast travel and sleeping make one: every game, the server's own included,
    // runs DV's TimeAdvance for it (ClientboundTimeAdvancePacket), so jobs and everything else that
    // goes by the clock move on properly. Never backwards: DV reads a clock going back as a whole day
    // passing (a lit flashlight's battery emptied at once).
    private void TimeMessage(string message, ServerPlayer sender)
    {
        if (!IsAdmin(sender))
            return;
        var weather = DV.WeatherSystem.WeatherDriver.Instance;
        if (weather?.manager == null)
        {
            Reply(sender, "The clock isn't running yet.");
            return;
        }
        DateTime now = weather.manager.DateTime;
        string arg = message.Trim();
        double seconds;
        if (arg.StartsWith("+") && double.TryParse(arg.Substring(1), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double hours))
        {
            seconds = hours * 3600;
        }
        else if (TimeSpan.TryParse(arg, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan timeOfDay) &&
                 timeOfDay >= TimeSpan.Zero && timeOfDay < TimeSpan.FromDays(1))
        {
            DateTime target = now.Date + timeOfDay;
            if (target <= now)
                target = target.AddDays(1);
            seconds = (target - now).TotalSeconds;
        }
        else
        {
            Reply(sender, $"It's {now:HH:mm}. /{COMMAND_TIME} HH:mm skips to the next such time, /{COMMAND_TIME} +hours skips ahead.");
            return;
        }
        if (seconds < 60 || seconds > 24 * 3600)
        {
            Reply(sender, "Time skips forward by at least a minute and at most a day.");
            return;
        }
        // Advances the server's world, then every other game.
        NetworkServer.Instance.SendTimeAdvance((float)seconds);
        string said = $"{sender.Username} skipped the time to {now.AddSeconds(seconds):HH:mm} ({seconds / 3600:0.#} h on).";
        Multiplayer.Log(said);
        ServerMessage(said, null);
    }

    // DV's own full repair (DamageController.RepairAll: body, engine, traction motors, compressor
    // and the rest), on the host; the car's health goes to every game as any other change does.
    private void RepairMessage(string message, ServerPlayer sender)
    {
        if (!IsAdmin(sender))
            return;
        List<string> parts = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        bool wholeTrain = parts.RemoveAll(part => string.Equals(part, "all", StringComparison.OrdinalIgnoreCase)) > 0;
        ServerPlayer player = parts.Count > 0 ? FindPlayer(parts[0]) : IsConsole(sender) ? null : sender;
        if (player == null)
        {
            Reply(sender, parts.Count > 0 ? $"No player called {parts[0]}." : $"/{COMMAND_REPAIR} <player> [all]");
            return;
        }
        if (player.CarId == 0 || !global::Multiplayer.Shared.Trains.NetworkedTrainCar.TryGet(player.CarId, out TrainCar car) || car == null)
        {
            Reply(sender, $"{player.Username} isn't in or on a car.");
            return;
        }
        List<TrainCar> cars = wholeTrain && car.trainset != null ? car.trainset.cars.ToList() : new List<TrainCar> { car };
        int repaired = 0;
        foreach (TrainCar each in cars)
        {
            DV.Damage.DamageController damage = each.GetComponentInChildren<DV.Damage.DamageController>(true);
            if (damage != null)
            {
                damage.RepairAll();
                repaired++;
            }
            else if (each.CarDamage != null)
            {
                each.CarDamage.RepairCar(each.CarDamage.maxHealth);
                repaired++;
            }
        }
        string what = cars.Count == 1 ? car.ID : $"{repaired} car(s) of {car.ID}'s train";
        string said = $"{sender.Username} repaired {what} for {player.Username}.";
        Multiplayer.Log(said);
        ServerMessage(said, null);
    }

    // Dynamic Seasons' own runtime, on the server (DVSeasonsIntegration); it publishes the change to
    // every player itself.
    private void SeasonMessage(string message, ServerPlayer sender)
    {
        if (!IsAdmin(sender))
            return;
        if (!global::Multiplayer.Server.Integrations.DVSeasonsIntegration.Installed)
        {
            Reply(sender, "The Dynamic Seasons mod isn't installed on the server.");
            return;
        }
        string arg = message.Trim().ToLowerInvariant();
        string error;
        string said;
        switch (arg)
        {
            case "":
                string current = global::Multiplayer.Server.Integrations.DVSeasonsIntegration.Current() ?? "an unknown season";
                Reply(sender, $"It's {current}. /{COMMAND_SEASON} spring|summer|autumn|winter|next|blizzard|calm");
                return;
            case "next":
                if (!global::Multiplayer.Server.Integrations.DVSeasonsIntegration.TryCall("AdvanceToNextSeason", out error))
                {
                    Reply(sender, error);
                    return;
                }
                said = $"{sender.Username} moved the season on.";
                break;
            case "blizzard":
                if (!global::Multiplayer.Server.Integrations.DVSeasonsIntegration.TryCall("ScheduleBlizzard", out error))
                {
                    Reply(sender, error);
                    return;
                }
                said = $"{sender.Username} called up a blizzard (it only comes in winter).";
                break;
            case "calm":
                if (!global::Multiplayer.Server.Integrations.DVSeasonsIntegration.TryCall("EndBlizzard", out error))
                {
                    Reply(sender, error);
                    return;
                }
                said = $"{sender.Username} ended the blizzard.";
                break;
            default:
                if (!global::Multiplayer.Server.Integrations.DVSeasonsIntegration.TrySetSeason(arg, out error))
                {
                    Reply(sender, error);
                    return;
                }
                said = $"{sender.Username} set the season to {arg}.";
                break;
        }
        Multiplayer.Log(said);
        ServerMessage(said, null);
    }

    private void RestoreMessage(string message, ServerPlayer sender)
    {
        if (!IsAdmin(sender))
            return;
        string name = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        ServerPlayer player = FindPlayer(name);
        if (player == null)
        {
            Reply(sender, string.IsNullOrEmpty(name) ? $"/{COMMAND_RESTORE} <player>" : $"No player called {name}.");
            return;
        }
        if (!AdminItemGrants.CanReceive(player, out string notNow))
        {
            Reply(sender, notNow);
            return;
        }
        HashSet<string> owned = AdminItemGrants.OwnedPrefabs(player);
        List<InventoryItemSpec> missing = AdminItemGrants.StartingItemPrefabs()
            .Where(prefab => !owned.Contains(prefab))
            .Select(prefab => AdminItemGrants.Find(prefab, out _))
            .Where(spec => spec != null)
            .ToList();
        if (missing.Count == 0)
        {
            Reply(sender, $"{player.Username} still owns all their starting items (they may be elsewhere: recall or Lost and Found).");
            return;
        }
        string list = string.Join(", ", missing.Select(spec => spec.ItemPrefabName));
        CoroutineManager.Instance.StartCoroutine(AdminItemGrants.Spawn(player, missing));
        Multiplayer.Log($"{sender.Username} restored {player.Username}'s starting items: {list}");
        Reply(sender, $"Giving {player.Username} back: {list}.");
        if (sender != player)
            Reply(player, $"You've been given back: {list}, in your inventory (or your Lost and Found if it's full).");
    }

    public void ServerMessage(string message, ServerPlayer sender, ServerPlayer exclude = null)
    {
        if (sender != null && !IsAdmin(sender))
            return;

        message = $"<color=#{MESSAGE_COLOUR_SERVER}>{message}</color>";
        NetworkServer.Instance.SendChat(message, exclude);
    }

    private void WhisperMessage(string message, ServerPlayer sender)
    {
        Multiplayer.LogDebug(() => $"Whispering: \"{message}\", sender: {sender?.Username}, senderID: {sender?.PlayerId}");

        if (sender == null)
            return;

        if (string.IsNullOrEmpty(message))
            return;

        string[] parts = message.Split([' '], 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
            return;

        string recipientName = parts[0];
        string whisperMessage = parts[1];


        Multiplayer.LogDebug(() => $"Whispering parse 1: \"{message}\", sender: {sender?.Username}, senderID: {sender?.PlayerId}, peerName: {recipientName}");

        //look up the peer ID
        var recipient = ServerPlayerFromUsername(recipientName);
        if (recipient == null)
        {
            Multiplayer.LogDebug(() => $"Whispering failed: \"{message}\", sender: {sender?.Username}, senderID: {sender?.PlayerId}, peerName: {recipientName}");

            whisperMessage = $"<color=#{MESSAGE_COLOUR_SERVER}>{Locale.Get(Locale.CHAT_WHISPER_NOT_FOUND_KEY, [recipientName])}</color>";
            Tell(whisperMessage, sender);
            return;
        }

        Multiplayer.LogDebug(() => $"Whispering parse 2: \"{message}\", sender: {sender?.Username}, senderID: {sender?.PlayerId}, peerName: {recipientName}, peerID: {recipient?.PlayerId}");

        //clean up the message to stop format injection
        whisperMessage = Regex.Replace(whisperMessage, "</noparse>", string.Empty, RegexOptions.IgnoreCase);

        //call each chat filter until either a filter returns false or all filters have been called
        foreach (var filter in _chatFilters)
        {
            if (!filter(ref message, sender))
                return;
        }

        whisperMessage = "<i><alpha=#50>" + sender.DisplayName + ":</color> <noparse>" + whisperMessage + "</noparse></i>";

        Tell(whisperMessage, recipient);
    }

    public void KickMessage(string message, ServerPlayer sender)
    {
        ServerPlayer playerToKick;
        string playerName;
        string whisper;

        if (!IsAdmin(sender))
            return;

        playerName = message.Split(' ')[0];
        if (string.IsNullOrEmpty(playerName))
            return;

        playerToKick = ServerPlayerFromUsername(playerName);

        if (playerToKick == null || NetworkLifecycle.Instance.IsHost(playerToKick))
        {
            whisper = $"<color=#{MESSAGE_COLOUR_SERVER}>{Locale.Get(Locale.CHAT_KICK_UNABLE_KEY, [playerName])}</color>";
        }
        else
        {
            whisper = $"<color=#{MESSAGE_COLOUR_SERVER}>{Locale.Get(Locale.CHAT_KICK_KICKED_KEY, [playerName])}</color>";

            NetworkServer.Instance.KickPlayer(playerToKick);
        }

        Tell(whisper, sender);
    }

    private void SetCrewNameMessage(string message, ServerPlayer sender)
    {
        ServerPlayer playerToSet;
        string playerName;
        string crewName;

        if (sender == null)
            return;

        string[] parts = message.Split([' '], 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 1)
            return;

        if (!IsAdmin(sender))
        {
            if (!Multiplayer.Settings.AllowClientCrewNames)
            {
                var whisper = $"<color=#{MESSAGE_COLOUR_SERVER}>{Locale.CHAT_SET_CREW_DISALLOWED}</color>";
                Tell(whisper, sender);
                return;
            }

            playerToSet = sender;
            crewName = message;
        }
        else
        {
            playerName = parts[0];
            playerToSet = ServerPlayerFromUsername(playerName);

            if (playerToSet == null)
            {
                string whisper = $"<color=#{MESSAGE_COLOUR_SERVER}>{Locale.Get(Locale.CHAT_SET_CREW_PLAYER_NOT_FOUND_KEY, [playerName])}</color>";
                Tell(whisper, sender);
                return;
            }

            crewName = string.Join(" ", parts.Skip(1));
        }
      
        playerToSet.CrewName = crewName;

        if (crewName != string.Empty)
        {
            string announce = $"<color=#{MESSAGE_COLOUR_SERVER}>{Locale.Get(Locale.CHAT_SET_CREW_JOINED_MSG_KEY, [playerToSet.Username, playerToSet.CrewName])}</color>";

            NetworkServer.Instance.SendChat(announce);
        }
    }

    private void SaveMessage(string _, ServerPlayer sender)
    {
        if (!IsAdmin(sender))
            return;
        bool saved = NetworkLifecycle.Instance.IsDedicatedHost
            ? DedicatedHost.Save($"/{COMMAND_SAVE} by {sender.Username}")
            : SaveGameManager.Instance.SaveAllowed() && SaveGameManager.Instance.Save(DV.Common.SaveType.Auto) != null;
        Reply(sender, saved ? "Saved." : "The game couldn't be saved right now.");
    }

    private void StopMessage(string _, ServerPlayer sender)
    {
        if (!IsAdmin(sender))
            return;
        if (!NetworkLifecycle.Instance.IsDedicatedHost)
        {
            Reply(sender, $"/{COMMAND_STOP} only shuts down a dedicated server.");
            return;
        }
        DedicatedHost.Stop($"/{COMMAND_STOP} by {sender.Username}");
    }

    private void PayFeesMessage(string _, ServerPlayer sender) => PayFees(sender);

    /// <summary>
    /// Pays the fees from the shared wallet as the fees screen would (FeePayment), for an admin:
    /// /payfees, or the fees screen on a client (ServerboundPayFeesRequestPacket).
    /// </summary>
    public void PayFees(ServerPlayer sender)
    {
        if (!IsAdmin(sender))
        {
            if (sender != null && !IsConsole(sender))
                Reply(sender, "Only the host or an admin can pay the fees.");
            return;
        }
        FeePayment.Result result = FeePayment.PayAll();
        if (result.Shortfall > 0)
        {
            Multiplayer.Log($"{sender.Username}'s fee payment refused: the fees come to ${result.Shortfall:N2}, the wallet has ${DV.InventorySystem.Inventory.Instance.PlayerMoney:N2}");
            Reply(sender, $"The fees come to ${result.Shortfall:N2}, more than the ${DV.InventorySystem.Inventory.Instance.PlayerMoney:N2} in the wallet.");
            return;
        }
        if (result.NothingToPay)
        {
            Reply(sender, "There are no fees to pay.");
            return;
        }
        string summary = $"{sender.Username} paid {result.Paid} fee(s), ${result.Cost:N2}" +
                         (result.Cleared > 0 ? $"; insurance cleared the other {result.Cleared}" : "");
        Multiplayer.Log(summary);
        ServerMessage(summary + ".", null);
    }

    private void HelpMessage(string _, ServerPlayer player)
    {

        if (player == null)
            return;

        Multiplayer.LogDebug(() => $"HelpMessage()");

        StringBuilder sb = new($"<color=#{MESSAGE_COLOUR_HELP}>{Locale.CHAT_HELP_AVAILABLE}");

        foreach (var helpMessage in _registeredHelpMessages)
        {
            var help = helpMessage.Value?.Invoke();
            if (help != null)
                sb.AppendLine("\r\n\t" + help);
        }

        sb.AppendLine("</color>");

        /*
         * $"<color=#{MESSAGE_COLOUR_HELP}>Available commands:" +

                        "\r\n\r\n\tSend a message as the server (host only)" +
                        "\r\n\t\t/server <message>" +
                        "\r\n\t\t/s <message>" +

                        "\r\n\r\n\tWhisper to a playerToKick" +
                        "\r\n\t\t/whisper <PlayerName> <message>" +
                        "\r\n\t\t/w <PlayerName> <message>" +

                        "\r\n\r\n\tDisplay this help message" +
                        "\r\n\t\t/help" +
                        "\r\n\t\t/?" +

                        "</color>";
        */
        Tell(sb.ToString(), player);
    }


    private ServerPlayer ServerPlayerFromUsername(string playerName)
    {

        if (string.IsNullOrEmpty(playerName))
            return null;

        return NetworkServer.Instance.ServerPlayers.Where(p => p.Username == playerName).FirstOrDefault();
    }
}
