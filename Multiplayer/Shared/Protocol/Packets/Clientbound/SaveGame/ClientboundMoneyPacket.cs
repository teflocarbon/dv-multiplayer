namespace Multiplayer.Shared.Protocol.Packets.Clientbound.SaveGame;

// The shared wallet's balance after every change. A double, as the game keeps it (a float lost cents
// on large balances), with a revision: it's sent unordered, and a client keeps the newest.
public class ClientboundMoneyPacket
{
    public double Amount { get; set; }
    public uint Revision { get; set; }
}
