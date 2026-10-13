namespace Multiplayer.Networking.Packets.Serverbound;

// A client put a banknote/coin into its inventory. The wallet is shared and host-owned, so the
// host spends the item's amount into it and destroys the item for everyone.
public class ServerboundMoneyItemDepositPacket
{
    public ushort ItemNetId { get; set; }
}
