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
        /// request was.
        /// </summary>
        public static ClientboundItemResultPacket Copy(ClientboundItemResultPacket answer) => new()
        {
            RequestId = answer.RequestId,
            Refusal = answer.Refusal,
            Detail = answer.Detail,
            Revision = answer.Revision,
            ItemNetId = answer.ItemNetId
        };
    }
}
