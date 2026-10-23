namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

/// <summary>Whether an owned-item retrieval went to Lost and Found, and why not when it didn't. A fresh list follows.</summary>
public class ClientboundOwnedItemRetrieveResultPacket
{
    public uint RequestId { get; set; }
    public uint Handle { get; set; }
    public bool Accepted { get; set; }
    public string RejectionReason { get; set; }
}
