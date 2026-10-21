using UnityEngine;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player installs the gadget item in their hand on a car (gadgets.md, "Requests"), where vanilla's
/// <c>GadgetItem.TryPlace</c> would. The pose is in the car's parenting frame. The server checks it, and
/// answers with the change log's <c>Installed</c> entry or a rejection.
/// </summary>
public class ServerboundGadgetInstallPacket
{
    public uint OperationId { get; set; }
    public ushort ItemNetId { get; set; }
    public ushort CarNetId { get; set; }
    public Vector3 LocalPosition { get; set; }
    public Quaternion LocalRotation { get; set; }
    /// <summary>The UID of the mount (a plate) it goes on, 0 for none.</summary>
    public int MountHostUid { get; set; }
    /// <summary>The collider it was placed onto, as a path below the car, for vanilla's placement data
    /// (on glass); empty when the server can't have it (the player's loaded cab).</summary>
    public string ColliderPath { get; set; } = string.Empty;
    public byte ColliderIndex { get; set; }
}
