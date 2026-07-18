using LiteNetLib.Utils;
using Multiplayer.Networking.Serialization;
using UnityEngine;

namespace Multiplayer.Networking.Data.Items;

public enum ItemSpatialPhase : byte
{
    Settled,
    InFlight,
    Sliding
}

/// <summary>
/// Sequenced transient pose for one host-issued item simulation epoch. World positions are
/// floating-origin-independent. Anchored items additionally carry their parent-local pose and
/// parent-relative velocities.
/// </summary>
public sealed class ItemSpatialStateData
{
    public ushort ItemNetId { get; set; }
    public uint AuthorityRevision { get; set; }
    public uint SimulationEpoch { get; set; }
    public uint SampleSequence { get; set; }
    public uint SourceTick { get; set; }
    public byte SimulatorPlayerId { get; set; }
    public ItemSpatialPhase Phase { get; set; }
    public Vector3 AbsolutePosition { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 LinearVelocity { get; set; }
    public Vector3 AngularVelocity { get; set; }
    public ItemWorldParentKind WorldParentKind { get; set; }
    public ushort WorldParentNetId { get; set; }
    public string WorldParentKey { get; set; }
    public Vector3 ParentLocalPosition { get; set; }
    public Quaternion ParentLocalRotation { get; set; }
    public bool Sleeping { get; set; }

    public ItemSpatialStateData Clone() => new()
    {
        ItemNetId = ItemNetId,
        AuthorityRevision = AuthorityRevision,
        SimulationEpoch = SimulationEpoch,
        SampleSequence = SampleSequence,
        SourceTick = SourceTick,
        SimulatorPlayerId = SimulatorPlayerId,
        Phase = Phase,
        AbsolutePosition = AbsolutePosition,
        Rotation = Rotation,
        LinearVelocity = LinearVelocity,
        AngularVelocity = AngularVelocity,
        WorldParentKind = WorldParentKind,
        WorldParentNetId = WorldParentNetId,
        WorldParentKey = WorldParentKey,
        ParentLocalPosition = ParentLocalPosition,
        ParentLocalRotation = ParentLocalRotation,
        Sleeping = Sleeping
    };

    public static void Serialize(NetDataWriter writer, ItemSpatialStateData data)
    {
        writer.Put(data.ItemNetId);
        writer.Put(data.AuthorityRevision);
        writer.Put(data.SimulationEpoch);
        writer.Put(data.SampleSequence);
        writer.Put(data.SourceTick);
        writer.Put(data.SimulatorPlayerId);
        writer.Put((byte)data.Phase);
        Vector3Serializer.Serialize(writer, data.AbsolutePosition);
        QuaternionSerializer.Serialize(writer, data.Rotation);
        Vector3Serializer.Serialize(writer, data.LinearVelocity);
        Vector3Serializer.Serialize(writer, data.AngularVelocity);
        writer.Put((byte)data.WorldParentKind);
        if (data.WorldParentKind == ItemWorldParentKind.TrainInterior)
            writer.Put(data.WorldParentNetId);
        else if (data.WorldParentKind == ItemWorldParentKind.StaticParent)
            writer.Put(data.WorldParentKey ?? string.Empty);
        if (data.WorldParentKind != ItemWorldParentKind.World)
        {
            Vector3Serializer.Serialize(writer, data.ParentLocalPosition);
            QuaternionSerializer.Serialize(writer, data.ParentLocalRotation);
        }
        writer.Put(data.Sleeping);
    }

    public static ItemSpatialStateData Deserialize(NetDataReader reader)
    {
        ItemSpatialStateData data = new()
        {
            ItemNetId = reader.GetUShort(),
            AuthorityRevision = reader.GetUInt(),
            SimulationEpoch = reader.GetUInt(),
            SampleSequence = reader.GetUInt(),
            SourceTick = reader.GetUInt(),
            SimulatorPlayerId = reader.GetByte(),
            Phase = (ItemSpatialPhase)reader.GetByte(),
            AbsolutePosition = Vector3Serializer.Deserialize(reader),
            Rotation = QuaternionSerializer.Deserialize(reader),
            LinearVelocity = Vector3Serializer.Deserialize(reader),
            AngularVelocity = Vector3Serializer.Deserialize(reader),
            WorldParentKind = (ItemWorldParentKind)reader.GetByte()
        };
        if (data.WorldParentKind == ItemWorldParentKind.TrainInterior)
            data.WorldParentNetId = reader.GetUShort();
        else if (data.WorldParentKind == ItemWorldParentKind.StaticParent)
            data.WorldParentKey = reader.GetString();
        if (data.WorldParentKind != ItemWorldParentKind.World)
        {
            data.ParentLocalPosition = Vector3Serializer.Deserialize(reader);
            data.ParentLocalRotation = QuaternionSerializer.Deserialize(reader);
        }
        data.Sleeping = reader.GetBool();
        return data;
    }
}
