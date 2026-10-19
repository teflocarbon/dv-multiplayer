using LiteNetLib.Utils;
using Multiplayer.Core.Trains;

namespace Multiplayer.Shared.Protocol.Data.Train;

public sealed class TrainEndpointStateData
{
    public ushort CarNetId { get; set; }
    public TrainEnd End { get; set; }
    public TrainEndpointCapabilities Capabilities { get; set; }
    public bool HasMechanicalPartner { get; set; }
    public ushort MechanicalPartnerCarNetId { get; set; }
    public TrainEnd MechanicalPartnerEnd { get; set; }
    public bool HasHosePartner { get; set; }
    public ushort HosePartnerCarNetId { get; set; }
    public TrainEnd HosePartnerEnd { get; set; }
    public bool HasMultipleUnitPartner { get; set; }
    public ushort MultipleUnitPartnerCarNetId { get; set; }
    public TrainEnd MultipleUnitPartnerEnd { get; set; }
    public TrainChainState ChainState { get; set; }
    public bool CockOpen { get; set; }
    public bool PreventAutoCouple { get; set; }

    public TrainEndpointState ToCore() => new()
    {
        Endpoint = new TrainEndpointId(CarNetId, End),
        Capabilities = Capabilities,
        MechanicalPartner = HasMechanicalPartner
            ? new TrainEndpointId(MechanicalPartnerCarNetId, MechanicalPartnerEnd)
            : (TrainEndpointId?)null,
        HosePartner = HasHosePartner
            ? new TrainEndpointId(HosePartnerCarNetId, HosePartnerEnd)
            : (TrainEndpointId?)null,
        MultipleUnitPartner = HasMultipleUnitPartner
            ? new TrainEndpointId(MultipleUnitPartnerCarNetId, MultipleUnitPartnerEnd)
            : (TrainEndpointId?)null,
        ChainState = ChainState,
        CockOpen = CockOpen,
        PreventAutoCouple = PreventAutoCouple
    };

    public static TrainEndpointStateData FromCore(TrainEndpointState state) => new()
    {
        CarNetId = state.Endpoint.CarNetId,
        End = state.Endpoint.End,
        Capabilities = state.Capabilities,
        HasMechanicalPartner = state.MechanicalPartner.HasValue,
        MechanicalPartnerCarNetId = state.MechanicalPartner?.CarNetId ?? 0,
        MechanicalPartnerEnd = state.MechanicalPartner?.End ?? TrainEnd.Rear,
        HasHosePartner = state.HosePartner.HasValue,
        HosePartnerCarNetId = state.HosePartner?.CarNetId ?? 0,
        HosePartnerEnd = state.HosePartner?.End ?? TrainEnd.Rear,
        HasMultipleUnitPartner = state.MultipleUnitPartner.HasValue,
        MultipleUnitPartnerCarNetId = state.MultipleUnitPartner?.CarNetId ?? 0,
        MultipleUnitPartnerEnd = state.MultipleUnitPartner?.End ?? TrainEnd.Rear,
        ChainState = state.ChainState,
        CockOpen = state.CockOpen,
        PreventAutoCouple = state.PreventAutoCouple
    };

    public static void Serialize(NetDataWriter writer, TrainEndpointStateData data)
    {
        writer.Put(data.CarNetId);
        writer.Put((byte)data.End);
        writer.Put((byte)data.Capabilities);
        PutPartner(writer, data.HasMechanicalPartner, data.MechanicalPartnerCarNetId,
            data.MechanicalPartnerEnd);
        PutPartner(writer, data.HasHosePartner, data.HosePartnerCarNetId, data.HosePartnerEnd);
        PutPartner(writer, data.HasMultipleUnitPartner, data.MultipleUnitPartnerCarNetId,
            data.MultipleUnitPartnerEnd);
        writer.Put((byte)data.ChainState);
        writer.Put(data.CockOpen);
        writer.Put(data.PreventAutoCouple);
    }

    public static TrainEndpointStateData Deserialize(NetDataReader reader)
    {
        var data = new TrainEndpointStateData
        {
            CarNetId = reader.GetUShort(),
            End = (TrainEnd)reader.GetByte(),
            Capabilities = (TrainEndpointCapabilities)reader.GetByte()
        };
        GetPartner(reader, out bool mechanical, out ushort mechanicalCar, out TrainEnd mechanicalEnd);
        data.HasMechanicalPartner = mechanical;
        data.MechanicalPartnerCarNetId = mechanicalCar;
        data.MechanicalPartnerEnd = mechanicalEnd;
        GetPartner(reader, out bool hose, out ushort hoseCar, out TrainEnd hoseEnd);
        data.HasHosePartner = hose;
        data.HosePartnerCarNetId = hoseCar;
        data.HosePartnerEnd = hoseEnd;
        GetPartner(reader, out bool mu, out ushort muCar, out TrainEnd muEnd);
        data.HasMultipleUnitPartner = mu;
        data.MultipleUnitPartnerCarNetId = muCar;
        data.MultipleUnitPartnerEnd = muEnd;
        data.ChainState = (TrainChainState)reader.GetByte();
        data.CockOpen = reader.GetBool();
        data.PreventAutoCouple = reader.GetBool();
        return data;
    }

    private static void PutPartner(NetDataWriter writer, bool present, ushort carNetId, TrainEnd end)
    {
        writer.Put(present);
        if (!present)
            return;
        writer.Put(carNetId);
        writer.Put((byte)end);
    }

    private static void GetPartner(NetDataReader reader, out bool present, out ushort carNetId,
        out TrainEnd end)
    {
        present = reader.GetBool();
        carNetId = present ? reader.GetUShort() : (ushort)0;
        end = present ? (TrainEnd)reader.GetByte() : TrainEnd.Rear;
    }
}
