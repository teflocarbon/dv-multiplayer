namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

public class ServerboundLicensePurchaseRequestPacket
{
    public string Id { get; set; }
    public bool IsJobLicense { get; set; }
}
