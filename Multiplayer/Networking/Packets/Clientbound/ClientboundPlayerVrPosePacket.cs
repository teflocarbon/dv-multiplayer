using UnityEngine;

namespace Multiplayer.Networking.Packets.Clientbound;

/// <summary>
/// Broadcast by the server to relay a VR player's head and hand pose to other clients.
/// All poses are in the local space of the player's root/body transform.
/// </summary>
public class ClientboundPlayerVrPosePacket
{
    public byte PlayerId { get; set; }
    public Vector3 HeadPosition { get; set; }
    public Quaternion HeadRotation { get; set; }
    public Vector3 LeftHandPosition { get; set; }
    public Quaternion LeftHandRotation { get; set; }
    public Vector3 RightHandPosition { get; set; }
    public Quaternion RightHandRotation { get; set; }
}
