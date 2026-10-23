using System;
using LiteNetLib.Utils;

namespace Multiplayer.Shared.Protocol.Data.Items;

/// <summary>Which list a <c>ClientboundItemListPacket</c> is. Byte-sized on the wire, append-only.</summary>
public enum ItemListKind : byte
{
    None,
    /// <summary>The recipient's Lost and Found, oldest first.</summary>
    LostAndFound,
    /// <summary>The recipient's owned items (the career manager's Owned Items page).</summary>
    Owned,
    /// <summary>One cold container's slots.</summary>
    Container
}

[Flags]
public enum ItemRowFlags : byte
{
    None = 0,
    /// <summary>Its owner can retrieve it from where it is (owned items).</summary>
    Retrievable = 1,
    /// <summary>Someone else owns it (red, D3).</summary>
    SomeoneElses = 2,
    /// <summary>It's a container: a list request for its handle opens it.</summary>
    Container = 4
}

/// <summary>
/// One row of a list (docs/design/items/wire.md, "One list"): an item a client sees only as a row,
/// named by a handle from the session's one handle space, never by its GUID.
/// </summary>
public class ItemRow
{
    public uint Handle { get; set; }
    public string PrefabName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Where it is or why it's there, worded by the server (an owned item's place, a lost item's reason).</summary>
    public string Where { get; set; } = string.Empty;
    public ItemRowFlags Flags { get; set; }
    /// <summary>Its slot in a container, or its claim slot in Lost and Found; -1 for none.</summary>
    public int Slot { get; set; } = -1;
    /// <summary>The revision a request about this row expects.</summary>
    public uint Revision { get; set; }

    public static void Serialize(NetDataWriter writer, ItemRow row)
    {
        writer.Put(row.Handle);
        writer.Put(row.PrefabName ?? string.Empty);
        writer.Put(row.DisplayName ?? string.Empty);
        writer.Put(row.Where ?? string.Empty);
        writer.Put((byte)row.Flags);
        writer.Put(row.Slot);
        writer.Put(row.Revision);
    }

    public static ItemRow Deserialize(NetDataReader reader) => new()
    {
        Handle = reader.GetUInt(),
        PrefabName = reader.GetString(),
        DisplayName = reader.GetString(),
        Where = reader.GetString(),
        Flags = (ItemRowFlags)reader.GetByte(),
        Slot = reader.GetInt(),
        Revision = reader.GetUInt()
    };
}
