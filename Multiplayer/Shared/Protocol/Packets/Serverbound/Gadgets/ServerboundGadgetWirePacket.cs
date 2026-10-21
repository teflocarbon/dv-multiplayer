namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player wires two gadgets on a car together, or unwires them, with the crimping tool (gadgets.md,
/// "Requests"): a port of each, by the gadget's UID and the port's index in its wiring module.
/// Answered with the change log's <c>Wired</c>/<c>Unwired</c> entry or a rejection.
/// </summary>
public class ServerboundGadgetWirePacket
{
    public uint OperationId { get; set; }
    public ushort CarNetId { get; set; }
    public int SourceUid { get; set; }
    public int SourcePort { get; set; }
    public int TargetUid { get; set; }
    public int TargetPort { get; set; }
    /// <summary>True to wire, false to unwire.</summary>
    public bool Wire { get; set; }
}
