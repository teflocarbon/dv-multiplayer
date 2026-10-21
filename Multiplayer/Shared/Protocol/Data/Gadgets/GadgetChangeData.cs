using LiteNetLib.Utils;
using UnityEngine;

namespace Multiplayer.Shared.Protocol.Data.Gadgets;

/// <summary>
/// One entry of a car's gadget change log (gadgets.md, "The change log"): what the server observed
/// happen, at the car's next revision. An <c>Installed</c> carries the item's vanilla save data, as a
/// joining player gets it, so applying it is vanilla's load; a <c>Removed</c> is the native removal,
/// which reproduces its holes, unwiring and unmounting in every game.
/// </summary>
public class GadgetChangeData
{
    public uint Revision;
    /// <summary>Multiplayer.Core.Gadgets.GadgetChangeKind.</summary>
    public byte Kind;
    public ushort ItemNetId;
    public int Uid;
    /// <summary>Installed: the item's <c>ItemSaveData</c> as JSON, the mod's own keys left out.</summary>
    public string State = string.Empty;
    /// <summary>Removed: knocked or taken off by code (a deleted car, broken glass), not by a player.</summary>
    public bool Forced;
    /// <summary>MountPoint: the point's index and its new state (MountPoint.States).</summary>
    public int Index;
    public byte Value;
    /// <summary>A hole: its position and direction in the car's parenting frame.</summary>
    public Vector3 Position;
    public Vector3 Direction;
    /// <summary>Wired, Unwired: the other gadget's UID and its port (Index is this one's port).</summary>
    public int OtherUid;
    public int OtherIndex;
    /// <summary>The request it answers: its player and operation, 0 for none.</summary>
    public byte PlayerId;
    public uint OperationId;

    public static void Serialize(NetDataWriter writer, GadgetChangeData data)
    {
        writer.Put(data.Revision);
        writer.Put(data.Kind);
        writer.Put(data.ItemNetId);
        writer.Put(data.Uid);
        writer.Put(data.State ?? string.Empty);
        writer.Put(data.Forced);
        writer.Put(data.PlayerId);
        writer.Put(data.OperationId);
        writer.Put(data.Index);
        writer.Put(data.Value);
        Put(writer, data.Position);
        Put(writer, data.Direction);
        writer.Put(data.OtherUid);
        writer.Put(data.OtherIndex);
    }

    public static GadgetChangeData Deserialize(NetDataReader reader) => new()
    {
        Revision = reader.GetUInt(),
        Kind = reader.GetByte(),
        ItemNetId = reader.GetUShort(),
        Uid = reader.GetInt(),
        State = reader.GetString(),
        Forced = reader.GetBool(),
        PlayerId = reader.GetByte(),
        OperationId = reader.GetUInt(),
        Index = reader.GetInt(),
        Value = reader.GetByte(),
        Position = GetVector(reader),
        Direction = GetVector(reader),
        OtherUid = reader.GetInt(),
        OtherIndex = reader.GetInt()
    };

    private static void Put(NetDataWriter writer, Vector3 v)
    {
        writer.Put(v.x);
        writer.Put(v.y);
        writer.Put(v.z);
    }

    private static Vector3 GetVector(NetDataReader reader) => new(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
}
