namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

/// <summary>
/// Where a stashed boombox's cassette has got to (docs/design/items/inventory.md, "Stashed items
/// that act"), sent by its holder's game as it moves to another track, runs out of tape, or is
/// taken out. The server keeps it in the item's row, for the players near the holder who hear it
/// (<see cref="Clientbound.World.ClientboundStashedSoundPacket"/>) and for the object made from the row.
/// </summary>
public class ServerboundStashedCassettePacket
{
    public ushort NetId { get; set; }
    public int Track { get; set; }
    /// <summary>The position in the track, in samples, as vanilla's cassette player counts it.</summary>
    public long Seek { get; set; }
    /// <summary>The tape ran out: vanilla's player still says it plays, with nothing left to play.</summary>
    public bool Ended { get; set; }
}
