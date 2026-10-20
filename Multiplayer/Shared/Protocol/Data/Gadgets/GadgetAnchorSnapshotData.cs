using LiteNetLib.Utils;

namespace Multiplayer.Shared.Protocol.Data.Gadgets;

/// <summary>
/// An installed gadget as a joining player gets it (gadgets.md, "Late joiners"): its item, the UID the
/// server gave it, and its item's vanilla save data, which places it on its car and restores its
/// state (soldering, wires, mount points, settings) the way loading a save does.
/// </summary>
public class InstalledGadgetData
{
    public ushort ItemNetId;
    public int Uid;
    /// <summary>The item's <c>ItemSaveData</c> as JSON, the mod's own keys left out.</summary>
    public string State = string.Empty;

    public static void Serialize(NetDataWriter writer, InstalledGadgetData data)
    {
        writer.Put(data.ItemNetId);
        writer.Put(data.Uid);
        writer.Put(data.State ?? string.Empty);
    }

    public static InstalledGadgetData Deserialize(NetDataReader reader) => new()
    {
        ItemNetId = reader.GetUShort(),
        Uid = reader.GetInt(),
        State = reader.GetString()
    };
}

/// <summary>The gadgets installed on one car.</summary>
public class GadgetAnchorSnapshotData
{
    public ushort CarNetId;
    public InstalledGadgetData[] Gadgets = [];

    public static void Serialize(NetDataWriter writer, GadgetAnchorSnapshotData data)
    {
        writer.Put(data.CarNetId);
        InstalledGadgetData[] gadgets = data.Gadgets ?? [];
        writer.Put(gadgets.Length);
        foreach (InstalledGadgetData gadget in gadgets)
            InstalledGadgetData.Serialize(writer, gadget);
    }

    public static GadgetAnchorSnapshotData Deserialize(NetDataReader reader)
    {
        GadgetAnchorSnapshotData data = new() { CarNetId = reader.GetUShort() };
        data.Gadgets = new InstalledGadgetData[reader.GetInt()];
        for (int i = 0; i < data.Gadgets.Length; i++)
            data.Gadgets[i] = InstalledGadgetData.Deserialize(reader);
        return data;
    }
}
