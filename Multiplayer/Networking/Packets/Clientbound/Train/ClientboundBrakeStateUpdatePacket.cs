namespace Multiplayer.Networking.Packets.Clientbound.Train;

public class ClientboundBrakeStateUpdatePacket
{
    public uint TopologyRevision { get; set; }
    public ushort NetId { get; set; }
    public ushort BrakeLineAnchorCarNetId { get; set; }
    public uint BrakeLineMemberDigest { get; set; }
    public float MainReservoirPressure { get; set; }
    public float BrakePipePressure { get; set; }
    public float BrakeCylinderPressure { get; set; }

    public float OverheatPercent { get; set; }
    public float OverheatReductionFactor { get; set; }
    public float Temperature { get; set; }
}
