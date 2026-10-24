namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

/// <summary>
/// What a nearby player's stashed boombox plays (docs/design/items/inventory.md, "Stashed items
/// that act"): sent by the server to each player near its holder as it starts, changes or stops,
/// or as they come near. Their game plays it at the holder's avatar. A stashed alarm clock's ring
/// goes the same way, once, as it rings (<see cref="Once"/>).
/// </summary>
public class ClientboundStashedSoundPacket
{
    public const byte AlarmRang = 1;
    public const byte AlarmTurned = 2;

    public byte HolderId { get; set; }
    public ushort NetId { get; set; }
    public string PrefabName { get; set; } = string.Empty;
    /// <summary>The cassette in its magazine, or empty.</summary>
    public string CassettePrefab { get; set; } = string.Empty;
    /// <summary>The boombox's own state as its row keeps it (power, mode, volume, station, the cassette's position); empty: it stopped.</summary>
    public string State { get; set; } = string.Empty;
    /// <summary>How long ago the state's cassette position was where it had got to.</summary>
    public int LateMilliseconds { get; set; }
    /// <summary>A sound made once rather than one that plays: <see cref="AlarmRang"/> or <see cref="AlarmTurned"/>; 0 for a boombox.</summary>
    public byte Once { get; set; }
}
