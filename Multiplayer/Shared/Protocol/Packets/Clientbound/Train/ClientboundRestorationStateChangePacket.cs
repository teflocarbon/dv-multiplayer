using DV.LocoRestoration;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Train;

public class ClientboundRestorationStateChangePacket
{
    public ushort NetId { get; set; }
    public LocoRestorationController.RestorationState NewState { get; set; }
    public ushort[] TransportCarNetIds { get; set; }
}
