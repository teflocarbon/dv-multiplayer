using UnityEngine;

namespace Multiplayer.Networking.Packets.Serverbound;

/// <summary>
/// Reliable physical evidence from the peer simulating an active train item. The host validates
/// the current spatial epoch, both canonical train placements, and all motion envelopes before it
/// may issue a wake lease for the struck settled item.
/// </summary>
public sealed class ServerboundItemTrainWakeWitnessPacket
{
    public uint WitnessId { get; set; }
    public ushort SourceItemNetId { get; set; }
    public uint SourceAuthorityRevision { get; set; }
    public uint SourceSimulationEpoch { get; set; }
    public ushort TargetItemNetId { get; set; }
    public uint TargetAuthorityRevision { get; set; }
    public ushort TrainCarNetId { get; set; }
    public uint SourceTick { get; set; }
    public Vector3 RelativeVelocity { get; set; }
    public Vector3 Impulse { get; set; }
    public Vector3 AbsoluteContactPoint { get; set; }
}
