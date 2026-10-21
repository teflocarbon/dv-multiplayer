using Multiplayer.Client.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

namespace Multiplayer.Client.Session;

// The host's own player and gadgets (gadgets.md): its gadget actions run in the server's game directly,
// so the only request is its tool use, which other players see (PR 11), and the only presentation
// other players' tool use.
public partial class LocalPlayerSession
{
    partial void RegisterGadgets()
    {
        Handle<ClientboundGadgetToolUsePacket>(GadgetToolPresence.Receive);
    }

    public void SendGadgetToolUse(ServerboundGadgetToolUsePacket packet) => Submit(packet);
}
