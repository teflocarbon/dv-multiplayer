using UnityEngine;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player fills a hole in a car with the hole filler (gadgets.md, "Requests"): the hole at
/// <see cref="LocalPosition"/> in the car's parenting frame. Answered with the change log's
/// <c>HoleRemoved</c> entry or a rejection.
/// </summary>
public class ServerboundGadgetFillHolePacket
{
    public uint OperationId { get; set; }
    public ushort CarNetId { get; set; }
    public Vector3 LocalPosition { get; set; }
}
