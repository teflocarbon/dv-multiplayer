using System;
using System.Collections.Generic;

namespace Multiplayer.Server.Items.Projection;

/// <summary>
/// The one handle space (docs/design/items/wire.md, "Naming an item"): a <c>uint</c> for each item or
/// container a client sees only as a row (a Lost and Found entry, an owned-items row, a container
/// and its slots), mapped to its GUID here on the server only. A GUID never goes on the wire. An id
/// keeps its handle for as long as the server runs, so a row's handle stays good while its list is open.
/// </summary>
public static class ItemHandles
{
    private static readonly Dictionary<Guid, uint> handles = new();
    private static readonly Dictionary<uint, Guid> ids = new();
    private static uint lastHandle;

    /// <summary>The handle for an item's or a container's id; 0 for no id.</summary>
    public static uint Of(Guid id)
    {
        if (id == Guid.Empty)
            return 0;
        if (!handles.TryGetValue(id, out uint handle))
        {
            do handle = ++lastHandle;
            while (handle == 0);
            handles[id] = handle;
            ids[handle] = id;
        }
        return handle;
    }

    /// <summary>The id a handle from this server's lists names.</summary>
    public static bool TryResolve(uint handle, out Guid id) => ids.TryGetValue(handle, out id);
}
