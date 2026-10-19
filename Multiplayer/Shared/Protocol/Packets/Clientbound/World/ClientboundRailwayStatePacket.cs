namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

public class ClientboundRailwayStatePacket
{
    public byte[] SelectedJunctionBranches { get; set; }
    public float[] TurntableRotations { get; set; }
}
