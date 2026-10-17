using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.Logic.Job;
using DV.ServicePenalty;
using DV.UserManagement;
using Multiplayer.Components.Networking;
using Multiplayer.Components.SaveGame;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Components.Networking.World;
using UnityEngine;

namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundSaveGameDataPacket
{
    public string GameMode { get; set; }
    public string SerializedDifficulty { get; set; }
    public float Money { get; set; }
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

    public static ClientboundSaveGameDataPacket CreatePacket(ServerPlayer player)
    {
        Multiplayer.LogDebug(() => $"ClientboundSaveGameDataPacket.CreatePacket() for player (is null: {player == null}) {player?.Username} ({player?.Guid})");
        if (WorldStreamingInit.isLoaded)
            SaveGameManager.Instance.UpdateInternalData();

        SaveGameData data = SaveGameManager.Instance.data;

        JObject difficulty = new();
        DifficultyDataUtils.SetDifficultyToJSON(difficulty, NetworkLifecycle.Instance.Server.Difficulty);

        JObject playerData = NetworkedSaveGameManager.Instance.Server_GetPlayerData(data, player.Guid);

        Multiplayer.LogDebug(() =>
        {
            string unlockedGen = string.Join(", ", UnlockablesManager.Instance.UnlockedGeneralLicenses);
            string packetGen = string.Join(", ", data.GetStringArray(SaveGameKeys.Licenses_General));

            string unlockedJob = string.Join(", ", UnlockablesManager.Instance.UnlockedJobLicenses);
            string packetJob = string.Join(", ", data.GetStringArray(SaveGameKeys.Licenses_Jobs));

            return $"ClientboundSaveGameDataPacket.CreatePacket() UnlockedGen: {{{unlockedGen}}}, PacketGen: {{{packetGen}}},  UnlockedJob: {{{unlockedJob}}}, PacketJob: {{{packetJob}}}";
        });

        // A starter kit for a player joining this world for the first time. It used to be handed out
        // on every join, a free set of tools each time someone reconnected.
        List<PlayerItemSaveData> playerItems = [];
        bool firstJoin = playerData == null && NetworkLifecycle.Instance.Server.StarterKitsGiven.Add(player.Guid);
        string[] items = firstJoin ? ["shovel", "lighter", "Oiler", "Lantern", "Flashlight", "Hanger", "DuctTape"] : [];
        string[] states = ["", "", "", "", "{\"Restock\": true,\"Battery_power\": 100}", "", ""];

        for (int i = 0; i < items.Length; i++)
        {
            JObject state;

            if (!string.IsNullOrEmpty(states[i]))
                state = JObject.Parse(states[i]);
            else
                state = [];

            var starterItem = new PlayerItemSaveData()
            {
                ItemPrefabName = items[i],
                BelongsToPlayer = true,
                InventorySlotIndex = 14 + i,
                State = state
            };
            playerItems.Add(starterItem);
        }

        List<OwnedItemsSummary.OwnedItem> owned = OwnedItemsSummary.For(player);
        Multiplayer.LogDebug(() => $"ClientboundSaveGameDataPacket.CreatePacket() {player.Username} already owns {owned.Count} item(s)");

        return new ClientboundSaveGameDataPacket
        {
            GameMode = data.GetString(SaveGameKeys.Game_mode),
            SerializedDifficulty = difficulty.ToString(Formatting.None),
            Money = StartingItemsController.Instance == null || !StartingItemsController.Instance.itemsLoaded ? data.GetFloat(SaveGameKeys.Player_money).GetValueOrDefault(0) : (float)Inventory.Instance.PlayerMoney,
            AcquiredGeneralLicenses = data.GetStringArray(SaveGameKeys.Licenses_General),
            AcquiredJobLicenses = data.GetStringArray(SaveGameKeys.Licenses_Jobs),
            UnlockedGarages = data.GetStringArray(SaveGameKeys.Garages),
            Position = playerData?.GetVector3(SaveGameKeys.Player_position) ?? LevelInfo.DefaultSpawnPosition,
            Rotation = playerData?.GetFloat(SaveGameKeys.Player_rotation) ?? LevelInfo.DefaultSpawnRotation.y,
            HasDebt = data.GetFloat(SaveGameKeys.Debt_total).GetValueOrDefault(CareerManagerDebtController.Instance != null ? CareerManagerDebtController.Instance.NumberOfNonZeroPricedDebts : 0) > 0,
            // Debt_existing_locos = data.GetJObjectArray(SaveGameKeys.Debt_existing_locos)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_deleted_locos = data.GetJObjectArray(SaveGameKeys.Debt_deleted_locos)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_existing_jobs = data.GetJObjectArray(SaveGameKeys.Debt_existing_jobs)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_staged_jobs = data.GetJObjectArray(SaveGameKeys.Debt_staged_jobs)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_existing_jobless_cars = data.GetJObject(SaveGameKeys.Debt_existing_jobless_cars)?.ToString(),
            // Debt_deleted_jobless_cars = data.GetJObject(SaveGameKeys.Debt_deleted_jobless_cars)?.ToString(),
            // Debt_insurance = data.GetJObject(SaveGameKeys.Debt_insurance)?.ToString()

            JobManagerTime = JobsManager.Instance.Time,

            PlayerItems = playerItems.ToArray(),
            OwnedItemPrefabs = owned.Select(item => item.Prefab).ToArray(),
            OwnedItemSlots = owned.Select(item => item.Slot).ToArray(),
        };
    }

    public ClientboundSaveGameDataPacket Clone()
    {
        return MemberwiseClone() as ClientboundSaveGameDataPacket;
    }
}
