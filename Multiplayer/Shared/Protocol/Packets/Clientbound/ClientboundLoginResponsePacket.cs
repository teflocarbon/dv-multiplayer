using Multiplayer.Shared.Protocol.Data;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

public class ClientboundLoginResponsePacket
{
    public bool Accepted { get; set; }
    public byte PlayerId { get; set; }
    public string OverrideUsername { get; set; }
    public string ReasonKey { get; set; }
    public string[] ReasonArgs { get; set; }
    public ModInfo[] Missing { get; set; } = [];
    public ModInfo[] Extra { get; set; } = [];
    public string ProtocolFingerprint { get; set; } = "";
}
