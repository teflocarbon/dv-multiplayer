using Multiplayer.Client.Jobs;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Jobs;
using UnityEngine;

namespace Multiplayer.Client.Session;

// The host's player's job requests (the reprint button, the office bin): the same packets a client
// sends, handed to the server in-process (NetworkServer.RegisterLocalJobs). The host's job UI is
// vanilla otherwise (its validator, booklets and overviews are the server's own), and the server
// sends its player no job state, so there's nothing to present.
public partial class LocalPlayerSession : IJobsClient
{
    public void SendJobBookletSummonRequest(uint stationNetId) =>
        Submit(new ServerboundJobBookletSummonRequestPacket { StationNetId = stationNetId });

    public void SendJobRerollRequest(ushort jobNetId, ushort itemNetId, Vector3 binPosition) =>
        Submit(new ServerboundJobRerollRequestPacket
        {
            JobNetId = jobNetId,
            ItemNetId = itemNetId,
            BinPosition = binPosition
        });
}
