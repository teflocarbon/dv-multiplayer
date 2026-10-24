using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Data.Jobs;
using System.Collections.Generic;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Jobs;
public class ClientboundJobsUpdatePacket
{
    public uint StationNetId { get; set; }
    public JobUpdateStruct[] JobUpdates { get; set; }

    
    public static ClientboundJobsUpdatePacket FromNetworkedJobs(uint stationNetID, NetworkedJob[] jobs)
    {
        Multiplayer.Log($"ClientboundJobsUpdatePacket.FromNetworkedJobs({stationNetID}, {jobs.Length})");

        List<JobUpdateStruct> jobData = new List<JobUpdateStruct>();
        foreach (var job in jobs)
        {
            uint validationStationNetId = 0;
            ushort validationItemNetId = 0;
            ItemPositionData itemPositionData = new ItemPositionData();

            if (NetworkedStationController.GetFromJobValidator(job.JobValidator, out NetworkedStationController netValidationStation))
                validationStationNetId = netValidationStation.NetId;

            switch (job.Cause)
            {
                case NetworkedJob.DirtyCause.JobOverview:
                    // Its overview can have gone since (a paper made from its row to end with its job).
                    if (job.JobOverview is { } overview)
                    {
                        validationItemNetId = overview.NetId;
                        itemPositionData = ItemPositionData.FromItem(overview);
                    }
                    break;
                case NetworkedJob.DirtyCause.JobBooklet:
                    validationItemNetId = job.LastChangedJobBooklet?.NetId ?? 0;
                    if (job.LastChangedJobBooklet != null)
                        itemPositionData = ItemPositionData.FromItem(job.LastChangedJobBooklet);
                    break;
            }

            JobUpdateStruct data = new JobUpdateStruct
            {
                JobNetID = job.NetId,
                JobState = job.Job.State,
                StartTime = job.Job.startTime,
                FinishTime = job.Job.finishTime,
                ValidationStationId = validationStationNetId,
                ItemNetID = validationItemNetId,
                ItemPositionData = itemPositionData,
                IssuedToPlayerId = job.Cause == NetworkedJob.DirtyCause.JobBooklet
                    ? JobServer.Current?.PlayerNumberOf(job.LastChangedJobBookletIssuedTo) ?? 0 : (byte)0
            };

            jobData.Add(data);
        }

        return new ClientboundJobsUpdatePacket
                                                {
                                                    StationNetId = stationNetID,
                                                    JobUpdates = jobData.ToArray()
                                                };
    }
}
