using UnityEngine;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player labels a car (gadgets.md, "Requests", PR 9): the label maker in their hand, the pose the
/// label goes at (as an install's) and the text they typed. The label is a new item, which only the
/// server makes: it spawns it, installs it and sets its text, and the change log's <c>Installed</c>
/// entry brings it to every game. A cancelled popup sends nothing.
/// </summary>
public class ServerboundGadgetSpawnInstallPacket
{
    public uint OperationId { get; set; }
    public ushort ToolItemNetId { get; set; }
    public ushort CarNetId { get; set; }
    public Vector3 LocalPosition { get; set; }
    public Quaternion LocalRotation { get; set; }
    public int MountHostUid { get; set; }
    public string ColliderPath { get; set; } = string.Empty;
    public byte ColliderIndex { get; set; }
    public string Text { get; set; } = string.Empty;
}
