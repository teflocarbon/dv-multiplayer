using Multiplayer.Shared.Protocol.Data.Jobs;
using System.Collections.Generic;

namespace Multiplayer.Server.Jobs;

public static class JobReportArtifactRegistry
{
    private static readonly Dictionary<ushort, JobReportArtifactData> ByItemNetId = new();

    public static void Register(JobReportArtifactData artifact)
    {
        if (artifact?.ItemNetId > 0)
            ByItemNetId[artifact.ItemNetId] = artifact;
    }

    public static bool TryGet(ushort itemNetId, out JobReportArtifactData artifact) =>
        ByItemNetId.TryGetValue(itemNetId, out artifact);

    public static void Remove(ushort itemNetId) => ByItemNetId.Remove(itemNetId);
    public static void Clear() => ByItemNetId.Clear();
}
