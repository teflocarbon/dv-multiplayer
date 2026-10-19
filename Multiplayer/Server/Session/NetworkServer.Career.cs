using DV;
using DV.InventorySystem;
using DV.ServicePenalty;
using DV.ThingTypes;
using LiteNetLib;
using Multiplayer.Server.Chat;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Clientbound.SaveGame;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Transport;

namespace Multiplayer.Server.Session;

// NetworkServer's career handlers and sends (see docs/design/code-layout.md).
public partial class NetworkServer
{
    public void SendGameParams(GameParams gameParams)
    {
        var packet = ClientboundGameParamsPacket.FromGameParams(gameParams);
        packet.FastTravelAdvancesTime = fastTravelAdvancesTime;
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForGameData, excludeSelf: true);
    }

    public void SendMoney(float amount)
    {
        SendPacketToAll
        (
            new ClientboundMoneyPacket
            {
                Amount = amount
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            excludeSelf: true
        );
    }

    public void SendLicense(string id, bool isJobLicense)
    {
        SendPacketToAll
        (
            new ClientboundLicenseAcquiredPacket
            {
                Id = id,
                IsJobLicense = isJobLicense
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            excludeSelf: true
        );
    }

    public void SendGarage(string id)
    {
        SendPacketToAll
        (
            new ClientboundGarageUnlockPacket
            {
                Id = id
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            excludeSelf: true
        );
    }

    public void SendDebtStatus(bool hasDebt)
    {
        SendPacketToAll
        (
            new ClientboundDebtStatusPacket
            {
                HasDebt = hasDebt
            }, DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            SelfPeer
        );
    }

    private void OnServerboundLicensePurchaseRequestPacket(ServerboundLicensePurchaseRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        JobLicenseType_v2 jobLicense = null;
        GeneralLicenseType_v2 generalLicense = null;
        float? price = packet.IsJobLicense
            ? (jobLicense = Globals.G.Types.jobLicenses.Find(l => l.id == packet.Id))?.price
            : (generalLicense = Globals.G.Types.generalLicenses.Find(l => l.id == packet.Id))?.price;

        if (!price.HasValue)
        {
            LogWarning($"{player.Username} tried to purchase an invalid {(packet.IsJobLicense ? "job" : "general")} license with id {packet.Id}!");
            return;
        }

        CareerManagerDebtController.Instance.RefreshExistingDebtsState();
        if (CareerManagerDebtController.Instance.NumberOfNonZeroPricedDebts > 0)
        {
            LogWarning($"{player.Username} tried to purchase a {(packet.IsJobLicense ? "job" : "general")} license with id {packet.Id} while having existing debts!");
            return;
        }

        if (!Inventory.Instance.RemoveMoney(price.Value))
        {
            LogWarning($"{player.Username} tried to purchase a {(packet.IsJobLicense ? "job" : "general")} license with id {packet.Id} without enough money to do so!");
            return;
        }

        if (packet.IsJobLicense)
            LicenseManager.Instance.AcquireJobLicense(jobLicense);
        else
            LicenseManager.Instance.AcquireGeneralLicense(generalLicense);
    }

    private void OnServerboundPayFeesRequestPacket(ServerboundPayFeesRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        ChatManager.PayFees(player);
    }
}
