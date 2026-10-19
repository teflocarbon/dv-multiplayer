using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

public class ClientboundGameParamsPacket
{
    public string SerializedGameParams { get; set; }
    public bool FastTravelAdvancesTime { get; set; }

    public void Apply(GameParams gameParams)
    {
        // FromJsonOverwrite writes the fields directly and raises no change events, so anything
        // already listening (the map's markers and fast travel, the comms radio modes) kept what
        // it saw before. Raise the event for every property this changed.
        Dictionary<string, object> before = Snapshot(gameParams);
        JsonUtility.FromJsonOverwrite(SerializedGameParams, gameParams);
        foreach (KeyValuePair<string, object> after in Snapshot(gameParams))
            if (!Equals(before[after.Key], after.Value))
                gameParams.OnPropertyChanged(after.Key);
    }

    private static Dictionary<string, object> Snapshot(GameParams gameParams) =>
        typeof(GameParams).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, p => p.GetValue(gameParams));

    public static ClientboundGameParamsPacket FromGameParams(GameParams gameParams)
    {
        return new ClientboundGameParamsPacket {
            SerializedGameParams = JsonUtility.ToJson(gameParams)
        };
    }
}
