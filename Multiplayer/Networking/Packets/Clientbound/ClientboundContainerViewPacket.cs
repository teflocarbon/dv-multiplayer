namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundContainerViewPacket
{
    public uint RequestId { get; set; }
    public bool Accepted { get; set; }
    public string RejectionReason { get; set; }
    public uint ContainerHandle { get; set; }
    public uint Revision { get; set; }
    public int Capacity { get; set; }
    public int Offset { get; set; }
    public bool HasMore { get; set; }
    public int[] Slots { get; set; }
    public uint[] ItemHandles { get; set; }
    public string[] PrefabNames { get; set; }
    public string[] DisplayNames { get; set; }
    public bool[] ForeignOwned { get; set; }
    public uint[] ChildContainerHandles { get; set; }
    public int[] ChildItemCounts { get; set; }
    public uint[] StateVersions { get; set; }
}
