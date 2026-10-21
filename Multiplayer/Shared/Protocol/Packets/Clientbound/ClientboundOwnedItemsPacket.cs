namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

/// <summary>
/// A player's owned items, a row per live item (owned-items.md, "The owned-items list"): a handle
/// for this session, what it is, where it is as the server words it, and whether its owner can
/// retrieve it from there.
/// </summary>
public class ClientboundOwnedItemsPacket
{
    public uint RequestId { get; set; }
    public uint[] Handles { get; set; }
    public string[] PrefabNames { get; set; }
    public string[] DisplayNames { get; set; }
    public string[] Wheres { get; set; }
    public bool[] Retrievable { get; set; }
}
