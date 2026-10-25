// The server's answers to item requests, as a step keeps them.
using Multiplayer.Shared.Protocol.Packets.Items;

namespace Dvmp.Scenarios
{
    public static class ItemAnswers
    {
        /// <summary>
        /// A copy of an answer, to keep past its handler. The client reads every answer into the one
        /// packet object (a reusable subscription), so a step that kept the packet read the next
        /// answer's fields: magazine-tools' retrieval read as refused when the client's own next
        /// request was. The packet's own <c>Clone</c>, so a field added to it is copied too.
        /// </summary>
        public static ClientboundItemResultPacket Copy(ClientboundItemResultPacket answer) => answer.Clone();
    }
}
