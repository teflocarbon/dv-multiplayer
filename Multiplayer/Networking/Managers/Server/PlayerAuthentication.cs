using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Packets.Serverbound;
using Steamworks;

namespace Multiplayer.Networking.Managers.Server;

/// <summary>
/// Which player is logging in (docs/design/server-player-split.md, "Identity"). Each player sends
/// the Steam account they claim and a session ticket; the server checks the ticket with Steam and
/// admits them under that account's player ID (<see cref="PlayerIdentity"/>).
///
/// - The host's own client, in the same process, proves itself with <see cref="HostToken"/>
///   instead: it is the host's player, or the service identity on a dedicated host.
/// - With -mp-allow-unverified (the test harness) everyone else is admitted under the GUID they
///   claim, as before Steam identities.
/// - Steam answers a ticket in two steps: BeginAuthSession checks it at once, and
///   OnValidateAuthTicketResponse confirms it (or not) a moment later. A negative answer then
///   kicks the player.
/// </summary>
internal sealed class PlayerAuthentication : IDisposable
{
    /// <summary>A secret of this process, which only the host's own client can send.</summary>
    public static readonly byte[] HostToken = Guid.NewGuid().ToByteArray();

    public readonly struct Identity
    {
        public readonly Guid PlayerId;
        // The verified account, or 0 when there was no ticket to check.
        public readonly ulong SteamId;

        public Identity(Guid playerId, ulong steamId)
        {
            PlayerId = playerId;
            SteamId = steamId;
        }
    }

    // Open auth sessions by Steam account, and the player each belongs to once admitted.
    private readonly Dictionary<ulong, ServerPlayer> sessions = [];
    // Sessions Steam has confirmed (OnValidateAuthTicketResponse OK).
    private readonly HashSet<ulong> validated = [];
    private readonly NetworkServer server;
    private bool subscribed;

    public PlayerAuthentication(NetworkServer server)
    {
        this.server = server;
    }

    /// <summary>
    /// Works out who a login is. False when the player can't be admitted, with the reason logged.
    /// A verified login opens an auth session, which <see cref="Admit"/> or <see cref="Abandon"/>
    /// must then follow.
    /// </summary>
    public bool TryResolve(ServerboundClientLoginPacket packet, Guid claimedGuid, out Identity identity)
    {
        identity = default;

        if (packet.HostToken != null && packet.HostToken.SequenceEqual(HostToken))
        {
            identity = new Identity(NetworkLifecycle.Instance.IsDedicatedHost
                ? Multiplayer.Settings.GetServiceGuid()
                : PlayerIdentity.Local, 0);
            return true;
        }

        if (PlayerIdentity.AllowUnverified)
        {
            if (claimedGuid == Guid.Empty)
            {
                Multiplayer.LogWarning($"Login from {packet.Username} without a GUID, refused");
                return false;
            }
            identity = new Identity(claimedGuid, 0);
            return true;
        }

        if (packet.SteamId == 0 || packet.AuthTicket == null || packet.AuthTicket.Length == 0)
        {
            Multiplayer.LogWarning($"Login from {packet.Username} without a Steam ticket, refused");
            return false;
        }

        if (!SteamReady())
        {
            Multiplayer.LogWarning($"Can't verify {packet.Username}'s Steam ticket: Steam isn't running on this host");
            return false;
        }

        ulong steamId = packet.SteamId;
        // A player back after a crash, before their old connection timed out: its session goes
        // first, or Steam refuses the new one as a duplicate.
        if (sessions.Remove(steamId))
            EndSession(steamId);

        BeginAuthResult result;
        try
        {
            result = SteamUser.BeginAuthSession(packet.AuthTicket, steamId);
        }
        catch (Exception e)
        {
            Multiplayer.LogError($"Steam ticket check for {packet.Username} threw: {e}");
            return false;
        }

        if (result != BeginAuthResult.OK)
        {
            Multiplayer.LogWarning($"Steam refused {packet.Username}'s ticket for {steamId}: {result}");
            return false;
        }

        sessions[steamId] = null;
        validated.Remove(steamId);
        identity = new Identity(PlayerIdentity.FromSteamId(steamId), steamId);
        Multiplayer.Log($"{packet.Username} is Steam account {steamId}");
        return true;
    }

    /// <summary>Ties a verified login's session to the player it admitted.</summary>
    public void Admit(Identity identity, ServerPlayer player)
    {
        if (identity.SteamId != 0 && sessions.ContainsKey(identity.SteamId))
            sessions[identity.SteamId] = player;
    }

    /// <summary>Closes a verified login's session when the player is refused after all.</summary>
    public void Abandon(Identity identity)
    {
        if (identity.SteamId != 0 && sessions.TryGetValue(identity.SteamId, out ServerPlayer player) && player == null)
        {
            sessions.Remove(identity.SteamId);
            validated.Remove(identity.SteamId);
            EndSession(identity.SteamId);
        }
    }

    /// <summary>Closes a leaving player's session, unless a newer login of theirs has taken it.</summary>
    public void OnDisconnected(ServerPlayer player)
    {
        ulong steamId = sessions.FirstOrDefault(pair => pair.Value == player).Key;
        if (steamId != 0)
        {
            sessions.Remove(steamId);
            validated.Remove(steamId);
            EndSession(steamId);
        }
    }

    private bool SteamReady()
    {
        try
        {
            if (!SteamClient.IsValid)
                return false;
        }
        catch
        {
            return false;
        }

        if (!subscribed)
        {
            SteamUser.OnValidateAuthTicketResponse += OnValidateAuthTicketResponse;
            subscribed = true;
        }
        return true;
    }

    private void OnValidateAuthTicketResponse(SteamId steamId, SteamId ownerId, AuthResponse response)
    {
        if (!sessions.TryGetValue(steamId.Value, out ServerPlayer player))
            return;

        if (response == AuthResponse.OK)
        {
            validated.Add(steamId.Value);
            if (ownerId.Value != steamId.Value)
                Multiplayer.Log($"Steam account {steamId.Value} plays a copy shared by {ownerId.Value} (Family Sharing)");
            return;
        }

        // Once Steam has confirmed a ticket, a later "cancelled" says nothing about who the player
        // is, and the connection itself is still there. It came for the server owner's own game
        // whenever another player joined or left (two games on the one account), and kicked them.
        // Only a ban or a lost licence still kicks a confirmed player.
        if (validated.Contains(steamId.Value) && response is not (AuthResponse.VACBanned or
                AuthResponse.PublisherIssuedBan or AuthResponse.NoLicenseOrExpired))
        {
            Multiplayer.Log($"Steam reported {response} for confirmed account {steamId.Value}" +
                            $"{(player != null ? $" ({player.Username})" : "")}; keeping them");
            return;
        }

        Multiplayer.LogWarning($"Steam rejected account {steamId.Value}{(player != null ? $" ({player.Username})" : "")}: {response}");
        sessions.Remove(steamId.Value);
        validated.Remove(steamId.Value);
        EndSession(steamId.Value);
        if (player != null)
            server.KickPlayer(player);
    }

    private static void EndSession(ulong steamId)
    {
        try
        {
            if (SteamClient.IsValid)
                SteamUser.EndAuthSession(steamId);
        }
        catch (Exception e)
        {
            Multiplayer.LogWarning($"Ending the Steam auth session of {steamId} threw: {e.Message}");
        }
    }

    public void Dispose()
    {
        foreach (ulong steamId in sessions.Keys.ToArray())
            EndSession(steamId);
        sessions.Clear();
        validated.Clear();
        if (subscribed)
            SteamUser.OnValidateAuthTicketResponse -= OnValidateAuthTicketResponse;
        subscribed = false;
    }
}
