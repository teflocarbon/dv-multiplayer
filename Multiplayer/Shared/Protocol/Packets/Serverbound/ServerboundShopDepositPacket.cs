namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

// A player paid into a shop register: from the wallet (MoneyNetId 0, what is still owed) or with a
// banknote (all of it).
public class ServerboundShopDepositPacket
{
    // This player's request counter: the answer names it, and its prediction leaves when it comes.
    public uint RequestId { get; set; }
    public byte ShopIndex { get; set; }
    public ushort MoneyNetId { get; set; }
}
