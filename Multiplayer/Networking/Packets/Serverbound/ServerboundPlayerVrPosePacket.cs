using UnityEngine;

namespace Multiplayer.Networking.Packets.Serverbound;

/// <summary>
/// Sent by a VR client to report the pose of their head and hands.
/// All poses are expressed in the local space of the player's root/body transform,
/// so they follow the body position/car-parenting already handled by the position packet.
/// </summary>
public class ServerboundPlayerVrPosePacket
{
    public Vector3 HeadPosition { get; set; }
    public Quaternion HeadRotation { get; set; }
    public Vector3 LeftHandPosition { get; set; }
    public Quaternion LeftHandRotation { get; set; }
    public Vector3 RightHandPosition { get; set; }
    public Quaternion RightHandRotation { get; set; }
}
