using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Session;

/// <summary>
/// Requests from the host's own player, handed to the server in-process instead of through its
/// loopback client (docs/design/host-local-session.md, stage 2). The server registers a handler
/// per request type and drains the queue where it handles received packets, so the host's
/// requests keep their order and their old timing (a loopback packet was handled at the next
/// poll too), and nothing re-enters the code that raised them.
/// </summary>
/// <typeparam name="TPlayer">The server's player type: the sender every handler is given.</typeparam>
public sealed class LocalRequestQueue<TPlayer> where TPlayer : class
{
    private readonly Dictionary<Type, Action<object, TPlayer>> handlers = new();
    private readonly Queue<(Type Type, object Request)> pending = new();

    public int Count => pending.Count;

    /// <summary>How requests of type <typeparamref name="TRequest"/> are handled. Replaces any earlier handler.</summary>
    public void Handle<TRequest>(Action<TRequest, TPlayer> handler) where TRequest : class =>
        handlers[typeof(TRequest)] = (request, player) => handler((TRequest)request, player);

    public bool Handles(Type requestType) => handlers.ContainsKey(requestType);

    /// <summary>Queues a request. False, and nothing queued, if no handler is registered for its type.</summary>
    public bool Submit<TRequest>(TRequest request) where TRequest : class
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (!handlers.ContainsKey(typeof(TRequest)))
            return false;
        pending.Enqueue((typeof(TRequest), request));
        return true;
    }

    /// <summary>
    /// Handles every queued request in order, with <paramref name="player"/> as the sender. A
    /// handler that throws is reported and the rest still run. With no player (the host isn't
    /// logged in, or has gone), the requests are dropped and reported.
    /// </summary>
    /// <returns>How many requests were handled.</returns>
    public int Drain(TPlayer player, Action<Type, Exception> onFailure)
    {
        int handled = 0;
        while (pending.Count > 0)
        {
            (Type type, object request) = pending.Dequeue();
            if (player == null)
            {
                onFailure(type, null);
                continue;
            }
            try
            {
                handlers[type](request, player);
                handled++;
            }
            catch (Exception e)
            {
                onFailure(type, e);
            }
        }
        return handled;
    }

    public void Clear()
    {
        pending.Clear();
        handlers.Clear();
    }
}
