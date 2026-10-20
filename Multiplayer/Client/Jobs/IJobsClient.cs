using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Client.Jobs;

/// <summary>
///     The job requests every player makes, the host's own included: the validator's reprint button
///     and an offered job's overview dropped in the office bin. <c>NetworkClient</c> implements it
///     (<c>Client/Session/NetworkClient.Jobs.cs</c>) and so does the host's player session
///     (<c>LocalPlayerSession.Jobs.cs</c>), which hands them to the server in-process.
/// </summary>
public interface IJobsClient
{
    void SendJobBookletSummonRequest(uint stationNetId);

    void SendJobRerollRequest(ushort jobNetId, ushort itemNetId, Vector3 binPosition);
}

public static class JobsClient
{
    /// <summary>
    ///     This game's player session, or null when there's none (no session running, or a dedicated
    ///     host with no local session, <c>-mp-local-session</c>).
    /// </summary>
    public static IJobsClient Current => NetworkLifecycle.Instance?.LocalSession as IJobsClient;
}
