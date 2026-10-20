using DV.Utils;
using JetBrains.Annotations;
using Multiplayer.Shared.Protocol.Data.RPCs;
using System;
using System.Collections.Generic;

namespace Multiplayer.Client.Session;

public class RpcManager : SingletonBehaviour<RpcManager>
{
    private uint nextTicketId = 1;
    private readonly Dictionary<uint, RpcTicket> activeTickets = [];

    protected override void Awake()
    {
        base.Awake();
        RpcResponseTypes.Discover();
    }

    public RpcTicket CreateTicket(float timeOut)
    {
        uint ticketId = nextTicketId++;
        var ticket = new RpcTicket(ticketId, timeOut);
        activeTickets[ticketId] = ticket;
        return ticket;
    }

    public void ResolveTicket(uint ticketId, IRpcResponse response)
    {
        Multiplayer.LogDebug(() => $"Resolving ticket {ticketId} with response {response.GetType().Name}");
        if (activeTickets.TryGetValue(ticketId, out var ticket))
        {
            Multiplayer.LogDebug(() => $"Found active ticket {ticketId}, resolving...");
            ticket.Resolve(response);
            activeTickets.Remove(ticketId);
        }
        else
        {
            Multiplayer.LogWarning($"Attempted to resolve non-existent or expired ticket with ID {ticketId}");
        }
    }

    protected void Update()
    {
        if (activeTickets.Count == 0)
            return;

        List<uint> expiredTickets = [];

        foreach (var kvp in activeTickets)
        {
            var ticket = kvp.Value;
            ticket.CheckExpiry();

            if (ticket.IsExpired)
                expiredTickets.Add(kvp.Key);
        }

        foreach (var ticketId in expiredTickets)
        {
            Multiplayer.LogDebug(() => $"Removing expired ticket {ticketId}");
            activeTickets.Remove(ticketId);
        }
    }

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(RpcManager)}]";
    }
}
