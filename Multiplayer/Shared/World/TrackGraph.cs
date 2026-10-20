using Multiplayer.Core.World;

namespace Multiplayer.Shared.World;

/// <summary>
/// This game's track graph as the network sees it: which GameObject has which NetId among the
/// rail tracks, junctions and turntables. Those NetIds are given out as the objects wake, so the
/// host refuses a client whose graph differs (see <see cref="TrackGraphFingerprint"/>).
/// </summary>
public static class TrackGraph
{
    /// <summary>Only meaningful once <c>WorldStreamingInit.LoadingFinished</c> has fired.</summary>
    public static TrackGraphFingerprint Compute() =>
        TrackGraphFingerprint.Compute(NetworkedRailTrack.NetIdTable, NetworkedJunction.NetIdTable, NetworkedTurntable.NetIdTable);
}
