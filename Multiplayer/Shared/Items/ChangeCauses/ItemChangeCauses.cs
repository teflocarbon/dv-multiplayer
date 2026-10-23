using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Items.ChangeCauses;

/// <summary>
/// The parts of "why did this item change" (<see cref="ItemMoveReason"/>,
/// <see cref="ItemRemovalReason"/>) that every game has: the details a cause carries and the callers
/// that led to it. The host's own log of moves and removals is the server's <c>ItemChangeLog</c>; a
/// client's log of the causes host updates bring is <c>ClientItemChangeLog</c>.
/// </summary>
public static class ItemChangeCauses
{
    private const int CallerFrames = 6;

    /// <summary>Joins what a reason leaves out: the change itself, and anything the caller adds.</summary>
    public static string Detail(params string[] parts)
    {
        List<string> kept = new();
        foreach (string part in parts)
            if (!string.IsNullOrEmpty(part))
                kept.Add(part);
        return string.Join("; ", kept);
    }

    /// <summary>
    /// The methods that led here, outside this class and the given types: what a change no caller
    /// explained can still say about where it came from.
    /// </summary>
    public static string Callers(params Type[] skip)
    {
        var names = new List<string>();
        foreach (StackFrame frame in new StackTrace(1, false).GetFrames() ?? Array.Empty<StackFrame>())
        {
            MethodBase method = frame.GetMethod();
            Type type = method?.DeclaringType;
            if (method == null || type == typeof(ItemChangeCauses) || Array.IndexOf(skip, type) >= 0)
                continue;
            names.Add(type != null ? $"{type.Name}.{method.Name}" : method.Name);
            if (names.Count == CallerFrames)
                break;
        }
        string callers = names.Count > 0 ? string.Join(" < ", names) : "an unknown caller";
        return NetworkLifecycle.Instance?.IsProcessingPacket == true ? "in a packet handler, " + callers : callers;
    }
}
