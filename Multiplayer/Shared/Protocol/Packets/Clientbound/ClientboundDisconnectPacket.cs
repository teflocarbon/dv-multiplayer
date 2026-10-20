namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

public class ClientboundDisconnectPacket
{
    public bool Kicked { get; set; }
    // Shown to a kicked player instead of the generic message when set.
    public string Reason { get; set; } = string.Empty;
}
