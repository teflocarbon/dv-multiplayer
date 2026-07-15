namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundLostItemRetrievePacket
{
    public uint RequestId { get; set; }
    public uint LostHandle { get; set; }
    public uint ExpectedRevision { get; set; }
    public int RequestedSlot { get; set; } = -1;
    public int ExistingItemSlot { get; set; } = -1;
    public int InventoryCapacity { get; set; }
    public int[] OccupiedSlots { get; set; }
}
