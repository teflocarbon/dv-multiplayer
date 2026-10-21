using LiteNetLib.Utils;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Gadgets;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Protocol.Data.Player;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Data.World;

namespace Multiplayer.Shared.Protocol.Serialization;

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
        packetProcessor.RegisterNestedType(TrainEndpointStateData.Serialize, TrainEndpointStateData.Deserialize);
        packetProcessor.RegisterNestedType(BrakeLineStateData.Serialize, BrakeLineStateData.Deserialize);
        packetProcessor.RegisterNestedType(TrainControlRevisionData.Serialize, TrainControlRevisionData.Deserialize);
        packetProcessor.RegisterNestedType(TrainControlStateData.Serialize, TrainControlStateData.Deserialize);
        packetProcessor.RegisterNestedType(TrainCarHealthData.Serialize, TrainCarHealthData.Deserialize);
        packetProcessor.RegisterNestedType(PitStopPlugMappingData.Serialize, PitStopPlugMappingData.Deserialize);
        packetProcessor.RegisterNestedType(LocoResourceModuleData.Serialize, LocoResourceModuleData.Deserialize);
        packetProcessor.RegisterNestedType(PitStopPlugData.Serialize, PitStopPlugData.Deserialize);
        packetProcessor.RegisterNestedType(PlayerTrackingData.Serialize, PlayerTrackingData.Deserialize);
        packetProcessor.RegisterNestedType(ItemUpdateData.Serialize, ItemUpdateData.Deserialize);
        packetProcessor.RegisterNestedType(ItemSpatialStateData.Serialize, ItemSpatialStateData.Deserialize);
        packetProcessor.RegisterNestedType(CustomizationHoleData.Serialize, CustomizationHoleData.Deserialize);
        packetProcessor.RegisterNestedType(GadgetAnchorSnapshotData.Serialize, GadgetAnchorSnapshotData.Deserialize);
        packetProcessor.RegisterNestedType(GadgetChangeData.Serialize, GadgetChangeData.Deserialize);
        packetProcessor.RegisterNestedType(Vector2Serializer.Serialize, Vector2Serializer.Deserialize);
        packetProcessor.RegisterNestedType(Vector3Serializer.Serialize, Vector3Serializer.Deserialize);
        packetProcessor.RegisterNestedType(QuaternionSerializer.Serialize, QuaternionSerializer.Deserialize);
        packetProcessor.RegisterNestedType(ColorSerializer.Serialize, ColorSerializer.Deserialize);
        packetProcessor.RegisterNestedType(TrainUncoupleCauseData.Serialize, TrainUncoupleCauseData.Deserialize);
        packetProcessor.RegisterNestedType<ChangeCause<DerailReason>>(ChangeCauseData.Serialize,
            ChangeCauseData.Deserialize<DerailReason>);
        packetProcessor.RegisterNestedType<ChangeCause<CarRemovalReason>>(ChangeCauseData.Serialize,
            ChangeCauseData.Deserialize<CarRemovalReason>);
    }
}
