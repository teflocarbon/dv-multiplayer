using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

// Built on the server by Server/Career/SaveGameDataPacketBuilder.
public class ClientboundSaveGameDataPacket
{
    public string GameMode { get; set; }
    public string SerializedDifficulty { get; set; }
    public double Money { get; set; }
    public string[] AcquiredGeneralLicenses { get; set; }
    public string[] AcquiredJobLicenses { get; set; }
    public string[] UnlockedGarages { get; set; }
    public Vector3 Position { get; set; }
    public float Rotation { get; set; }

    public bool HasDebt { get; set; }
    // public string[] Debt_existing_locos { get; set; }
    // public string[] Debt_deleted_locos { get; set; }
    // public string[] Debt_existing_jobs { get; set; }
    // public string[] Debt_staged_jobs { get; set; }
    // public string Debt_existing_jobless_cars { get; set; }
    // public string Debt_deleted_jobless_cars { get; set; }
    // public string Debt_insurance { get; set; }

    public PlayerItemSaveData[] PlayerItems { get; set; }

    // What this player already owns somewhere the host keeps items (prefab names, and the
    // inventory slot it holds, or -1). The client's own DV only adds starting items and licence
    // papers missing from these, instead of a fresh set on every join (OwnedItemsSummary).
    public string[] OwnedItemPrefabs { get; set; }
    public int[] OwnedItemSlots { get; set; }

    public float JobManagerTime { get; set; }

    public ClientboundSaveGameDataPacket Clone()
    {
        return MemberwiseClone() as ClientboundSaveGameDataPacket;
    }
}
