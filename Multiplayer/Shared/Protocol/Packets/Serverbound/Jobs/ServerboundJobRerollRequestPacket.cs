using UnityEngine;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Jobs;

/// <summary>
/// A player dropped an offered job's overview in a station office bin: replace the job. The
/// server either re-rolls it or throws the paper back out (Server/Jobs/JobReroll).
/// </summary>
public sealed class ServerboundJobRerollRequestPacket
{
    public ushort JobNetId { get; set; }
    public ushort ItemNetId { get; set; }
    /// <summary>The bin it went in, absolute (without <c>WorldMover.currentMove</c>).</summary>
    public Vector3 BinPosition { get; set; }
}
