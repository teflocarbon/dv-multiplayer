using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.ThingTypes;
using DV.Utils;
using JetBrains.Annotations;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Session;
using Newtonsoft.Json.Linq;
using System;

namespace Multiplayer.Server.Career;

public class NetworkedSaveGameManager : SingletonBehaviour<NetworkedSaveGameManager>
{
    private const string ROOT_KEY = "Multiplayer";
    private const string PLAYERS_KEY = "Players";
    private const string INVENTORY_KEY = "Inventory";

    protected override void Awake()
    {
        base.Awake();
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        Inventory.Instance.MoneyChanged += Server_OnMoneyChanged;
        LicenseManager.Instance.LicenseAcquired += Server_OnLicenseAcquired;
        LicenseManager.Instance.JobLicenseAcquired += Server_OnJobLicenseAcquired;
        LicenseManager.Instance.GarageUnlocked += Server_OnGarageUnlocked;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (UnloadWatcher.isUnloading)
            return;
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        Inventory.Instance.MoneyChanged -= Server_OnMoneyChanged;
        LicenseManager.Instance.LicenseAcquired -= Server_OnLicenseAcquired;
        LicenseManager.Instance.JobLicenseAcquired -= Server_OnJobLicenseAcquired;
        LicenseManager.Instance.GarageUnlocked -= Server_OnGarageUnlocked;
    }

    #region Server

    private static void Server_OnMoneyChanged(double oldAmount, double newAmount)
    {
        NetworkServer.Instance.SendMoney(newAmount);
    }

    private static void Server_OnLicenseAcquired(GeneralLicenseType_v2 license)
    {
        NetworkServer.Instance.SendLicense(license.id, false);
        // A licence can make a shop item buyable (the wireless MU controller).
        NetworkServer.Instance.SendShopStock();
    }

    private static void Server_OnJobLicenseAcquired(JobLicenseType_v2 license)
    {
        NetworkServer.Instance.SendLicense(license.id, true);
    }

    private static void Server_OnGarageUnlocked(GarageType_v2 garage)
    {
        NetworkServer.Instance.SendGarage(garage.id);
        // An unlocked garage takes its key off sale.
        NetworkServer.Instance.SendShopStock();
    }

    public void Server_UpdateInternalData(SaveGameData data)
    {
        JObject root = data.GetJObject(ROOT_KEY) ?? [];
        JObject players = root.GetJObject(PLAYERS_KEY) ?? [];

        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
        {
            // Every player by GUID, the host's own included (PerPlayerSaveFormat).
            if (player.LoadingState != PlayerLoadingState.Complete)
                continue;

            JObject playerData = [];
            // Player position data: the host's own player from its local transform, the rest from
            // what their games send.
            bool local = NetworkLifecycle.Instance.IsHost(player) && PlayerManager.PlayerTransform != null;
            playerData.SetVector3(SaveGameKeys.Player_position,
                local ? PlayerManager.PlayerTransform.position - WorldMover.currentMove : player.AbsoluteWorldPosition);
            playerData.SetFloat(SaveGameKeys.Player_rotation,
                local ? PlayerManager.PlayerTransform.eulerAngles.y : player.WorldRotationY);

            //Player Inventory data
            SaveGameData saveGameData = new();
            playerData.Remove(INVENTORY_KEY);
            StorageSerializer.SaveStorage(player.Inventory, saveGameData);
            ItemSaveLoad.RemoveVanillaStorageDuplicates(saveGameData);
            playerData.Merge(saveGameData.GetJsonObject());

            players.SetJObject(player.Guid.ToString(), playerData);

        }

        Multiplayer.LogDebug(() => $"Updated save data: {players.ToString()}");
        root.SetJObject(PLAYERS_KEY, players);
        LostAndFoundServer.WriteSave(root);
        ContainerServer.WriteSave(root);
        ItemSaveLoad.RemoveVanillaStorageDuplicates(data, keepInventory: true);
        ItemSaveLoad.WriteSave(root);
        ServerLedger.WriteSave(root);
        data.SetJObject(ROOT_KEY, root);
    }

    public JObject Server_GetPlayerData(SaveGameData data, Guid guid)
    {
        return data?.GetJObject(ROOT_KEY)?.GetJObject(PLAYERS_KEY)?.GetJObject(guid.ToString());
    }

    #endregion

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkedSaveGameManager)}]";
    }
}
