using System;
using System.Linq;
using Steamworks;

namespace Multiplayer.Shared.Session;

/// <summary>
/// Who a player is: their Steam account (docs/design/server-player-split.md, "Identity").
///
/// A player ID is a GUID derived from the SteamID64: a fixed prefix plus the 64-bit ID, which can be
/// turned back into the SteamID. Every place that keys players by GUID (ServerPlayer.Guid, item
/// ownership, Players.&lt;id&gt; in saves, Lost and Found, cold containers) keeps its type and only
/// gets its value from here, and no 64-bit number ever passes through JSON as a number.
///
/// Without Steam (single player with Steam not running) the local player falls back to the
/// install's own GUID (Settings.Guid); a later verified login re-keys it to the Steam ID.
/// </summary>
public static class PlayerIdentity
{
    // "DVMP" "ST" version 1, then the SteamID64 big-endian in the last eight bytes.
    private const int PrefixA = 0x44564D50;
    private const short PrefixB = 0x5354;
    private const short PrefixC = 0x0001;

    public static Guid FromSteamId(ulong steamId)
    {
        byte[] id = BitConverter.GetBytes(steamId);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(id);
        return new Guid(PrefixA, PrefixB, PrefixC, id);
    }

    /// <summary>Whether this player ID is a Steam account's, and which.</summary>
    public static bool TryGetSteamId(Guid playerId, out ulong steamId)
    {
        steamId = 0;
        byte[] bytes = playerId.ToByteArray();
        Guid prefix = new(PrefixA, PrefixB, PrefixC, new byte[8]);
        if (!bytes.Take(8).SequenceEqual(prefix.ToByteArray().Take(8)))
            return false;
        byte[] id = bytes.Skip(8).ToArray();
        if (BitConverter.IsLittleEndian)
            Array.Reverse(id);
        steamId = BitConverter.ToUInt64(id, 0);
        return steamId != 0;
    }

    /// <summary>The Steam account this game runs under, or 0 without Steam.</summary>
    public static ulong LocalSteamId
    {
        get
        {
            try
            {
                return SteamClient.IsValid && SteamClient.IsLoggedOn ? SteamClient.SteamId.Value : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// -mp-allow-unverified, for the test harness: a host that admits players without a Steam ticket
    /// under the GUID they claim, and keys its own player by the install's GUID. Two games on one
    /// Steam account would otherwise be the same player. Never for a public server.
    /// </summary>
    public static bool AllowUnverified { get; } = Environment.GetCommandLineArgs().Contains("-mp-allow-unverified");

    /// <summary>
    /// The local player's ID: this game's Steam account, or the install's GUID without Steam (or
    /// with -mp-allow-unverified). A dedicated host's own client isn't a player and uses
    /// Settings.ServiceGuid instead.
    /// </summary>
    public static Guid Local =>
        !AllowUnverified && LocalSteamId != 0 ? FromSteamId(LocalSteamId) : Multiplayer.Settings.GetGuid();

    /// <summary>
    /// A player ID typed by a person (an admin list, -mp-owner): a SteamID64, or a GUID for an ID
    /// from before Steam identities.
    /// </summary>
    public static bool TryParse(string text, out Guid playerId)
    {
        playerId = Guid.Empty;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;
        if (ulong.TryParse(text, out ulong steamId) && steamId != 0)
        {
            playerId = FromSteamId(steamId);
            return true;
        }
        return Guid.TryParse(text, out playerId) && playerId != Guid.Empty;
    }

    /// <summary>How a player ID reads in logs and messages: the SteamID64 when it is one.</summary>
    public static string Describe(Guid playerId) =>
        TryGetSteamId(playerId, out ulong steamId) ? $"Steam {steamId}" : playerId.ToString();
}
