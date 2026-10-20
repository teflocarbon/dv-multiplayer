using Multiplayer.Shared.Protocol.Data.Gadgets;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;

/// <summary>
/// Every gadget installed on a car, for a player at <c>ReadyForCustomizers</c>; the items' Creates go
/// just before it (gadgets.md, "Late joiners"). Sent even when there are none, so the player knows
/// the step is done.
/// </summary>
public class ClientboundGadgetJoinSnapshotPacket
{
    public GadgetAnchorSnapshotData[] Anchors { get; set; } = [];
}
