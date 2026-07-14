using LiteNetLib.Utils;
using Multiplayer.Networking.Serialization;
using UnityEngine;

namespace Multiplayer.Networking.Data.Items;

public struct ItemAdoptionRequestData
{
    public string AdoptionToken { get; set; }
    public string PrefabName { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public ItemUpdateData Snapshot { get; set; }

    public static void Serialize(NetDataWriter writer, ItemAdoptionRequestData data)
    {
        writer.Put(data.AdoptionToken ?? string.Empty);
        writer.Put(data.PrefabName ?? string.Empty);
        Vector3Serializer.Serialize(writer, data.Position);
        QuaternionSerializer.Serialize(writer, data.Rotation);
        ItemUpdateData.Serialize(writer, data.Snapshot ?? new ItemUpdateData());
    }

    public static ItemAdoptionRequestData Deserialize(NetDataReader reader)
    {
        return new ItemAdoptionRequestData
        {
            AdoptionToken = reader.GetString(),
            PrefabName = reader.GetString(),
            Position = Vector3Serializer.Deserialize(reader),
            Rotation = QuaternionSerializer.Deserialize(reader),
            Snapshot = ItemUpdateData.Deserialize(reader)
        };
    }
}

public struct ItemAdoptionResultData
{
    public string AdoptionToken { get; set; }
    public bool Accepted { get; set; }
    public ushort AssignedNetId { get; set; }
    public string RejectionReason { get; set; }
    public uint AuthorityRevision { get; set; }
    public byte PersistentOwnerPlayerId { get; set; }
    public byte InventoryClaimPlayerId { get; set; }
    public int InventoryClaimSlot { get; set; }
    public ItemInventoryClaimFlags InventoryClaimFlags { get; set; }

    public static void Serialize(NetDataWriter writer, ItemAdoptionResultData data)
    {
        writer.Put(data.AdoptionToken ?? string.Empty);
        writer.Put(data.Accepted);
        writer.Put(data.AssignedNetId);
        writer.Put(data.RejectionReason ?? string.Empty);
        writer.Put(data.AuthorityRevision);
        writer.Put(data.PersistentOwnerPlayerId);
        writer.Put(data.InventoryClaimPlayerId);
        writer.Put(data.InventoryClaimSlot);
        writer.Put((byte)data.InventoryClaimFlags);
    }

    public static ItemAdoptionResultData Deserialize(NetDataReader reader)
    {
        return new ItemAdoptionResultData
        {
            AdoptionToken = reader.GetString(),
            Accepted = reader.GetBool(),
            AssignedNetId = reader.GetUShort(),
            RejectionReason = reader.GetString(),
            AuthorityRevision = reader.GetUInt(),
            PersistentOwnerPlayerId = reader.GetByte(),
            InventoryClaimPlayerId = reader.GetByte(),
            InventoryClaimSlot = reader.GetInt(),
            InventoryClaimFlags = (ItemInventoryClaimFlags)reader.GetByte()
        };
    }
}
