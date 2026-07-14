using LiteNetLib.Utils;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Networking.Data.Player;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Data.World;

namespace Multiplayer.Networking.Serialization;

/// <summary>
/// The complete nested-type registration shared by every endpoint that speaks the multiplayer
/// protocol. Keep this independent of Unity lifecycle code so development tools use the exact
/// same LiteNetLib serialization rules as the mod.
/// </summary>
public static class PacketSerializationRegistry
{
    public static void Register(NetPacketProcessor packetProcessor)
    {
        packetProcessor.RegisterNestedType(BogieData.Serialize, BogieData.Deserialize);
        packetProcessor.RegisterNestedType<JobUpdateStruct>();
        packetProcessor.RegisterNestedType(JobData.Serialize, JobData.Deserialize);
        packetProcessor.RegisterNestedType(ModInfo.Serialize, ModInfo.Deserialize);
        packetProcessor.RegisterNestedType(RigidbodySnapshot.Serialize, RigidbodySnapshot.Deserialize);
        packetProcessor.RegisterNestedType(StationsChainNetworkData.Serialize, StationsChainNetworkData.Deserialize);
        packetProcessor.RegisterNestedType(TrainsetMovementPart.Serialize, TrainsetMovementPart.Deserialize);
        packetProcessor.RegisterNestedType(TrainsetSpawnPart.Serialize, TrainsetSpawnPart.Deserialize);
        packetProcessor.RegisterNestedType(TrainCarHealthData.Serialize, TrainCarHealthData.Deserialize);
        packetProcessor.RegisterNestedType(PitStopPlugMappingData.Serialize, PitStopPlugMappingData.Deserialize);
        packetProcessor.RegisterNestedType(LocoResourceModuleData.Serialize, LocoResourceModuleData.Deserialize);
        packetProcessor.RegisterNestedType(PitStopPlugData.Serialize, PitStopPlugData.Deserialize);
        packetProcessor.RegisterNestedType(PlayerItemSaveData.Serialize, PlayerItemSaveData.Deserialize);
        packetProcessor.RegisterNestedType(ItemAdoptionRequestData.Serialize, ItemAdoptionRequestData.Deserialize);
        packetProcessor.RegisterNestedType(ItemAdoptionResultData.Serialize, ItemAdoptionResultData.Deserialize);
        packetProcessor.RegisterNestedType(PlayerTrackingData.Serialize, PlayerTrackingData.Deserialize);
        packetProcessor.RegisterNestedType(ItemUpdateData.Serialize, ItemUpdateData.Deserialize);
        packetProcessor.RegisterNestedType(CustomizationHoleData.Serialize, CustomizationHoleData.Deserialize);
        packetProcessor.RegisterNestedType(Vector2Serializer.Serialize, Vector2Serializer.Deserialize);
        packetProcessor.RegisterNestedType(Vector3Serializer.Serialize, Vector3Serializer.Deserialize);
        packetProcessor.RegisterNestedType(QuaternionSerializer.Serialize, QuaternionSerializer.Deserialize);
        packetProcessor.RegisterNestedType(ColorSerializer.Serialize, ColorSerializer.Deserialize);
    }
}
