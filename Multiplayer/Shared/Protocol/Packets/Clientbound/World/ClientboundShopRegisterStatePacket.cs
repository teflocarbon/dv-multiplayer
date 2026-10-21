namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

// A shop register as the server has it, whole, after every change (and at join): the shared basket's
// lines (module, owner, units, as parallel arrays), the cash paid in, whether a purchase is being
// delivered, and what just happened there (ShopRegisterEvent), so every game shows and plays it.
public class ClientboundShopRegisterStatePacket
{
    public byte ShopIndex { get; set; }
    public uint Revision { get; set; }
    public byte[] LineModules { get; set; }
    public byte[] LineOwners { get; set; }
    public byte[] LineUnits { get; set; }
    public double Deposited { get; set; }
    public bool Delivering { get; set; }
    public byte Event { get; set; }
    public byte EventPlayerId { get; set; }
    public byte EventModule { get; set; }
    // The request this state answers: its player's game drops that request (and earlier ones) from its prediction.
    public byte AckPlayerId { get; set; }
    public uint AckRequestId { get; set; }
}
