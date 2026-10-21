using DV;
using DV.InventorySystem;
using DV.ServicePenalty.UI;
using LiteNetLib;
using Multiplayer.Client.Career;
using Multiplayer.Client.World;
using Multiplayer.Shared.Protocol.Packets.Clientbound.SaveGame;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Object = UnityEngine.Object;

namespace Multiplayer.Client.Session;

// NetworkClient's career handlers and sends (see docs/design/code-layout.md).
public partial class NetworkClient
{
    private void OnClientboundGameParamsPacket(ClientboundGameParamsPacket packet)
    {
        LogDebug(() => $"Received {nameof(ClientboundGameParamsPacket)} ({packet.SerializedGameParams.Length} chars)");
        if (Globals.G.GameParams != null)
            packet.Apply(Globals.G.GameParams);
        if (Globals.G.gameParamsInstance != null)
            packet.Apply(Globals.G.gameParamsInstance);

        TimeAdvancePatch.FastTravelAdvancesTime = packet.FastTravelAdvancesTime;
    }

    // Money updates are unordered: an older balance arriving late must not overwrite a newer one.
    private uint moneyRevision;

    private void OnClientboundMoneyPacket(ClientboundMoneyPacket packet)
    {
        if (moneyRevision != 0 && unchecked((int)(packet.Revision - moneyRevision)) <= 0)
        {
            LogDebug(() => $"Ignored money ${packet.Amount} (revision {packet.Revision}, have {moneyRevision})");
            return;
        }
        moneyRevision = packet.Revision;
        LogDebug(() => $"Received new money amount ${packet.Amount}");
        Inventory.Instance.SetMoney(packet.Amount);
    }

    private void OnClientboundLicenseAcquiredPacket(ClientboundLicenseAcquiredPacket packet)
    {
        LogDebug(() => $"Received new {(packet.IsJobLicense ? "job" : "general")} license {packet.Id}");

        if (packet.IsJobLicense)
            LicenseManager.Instance.AcquireJobLicense(Globals.G.Types.jobLicenses.Find(l => l.id == packet.Id));
        else
            LicenseManager.Instance.AcquireGeneralLicense(Globals.G.Types.generalLicenses.Find(l => l.id == packet.Id));

        foreach (CareerManagerLicensesScreen screen in Object.FindObjectsOfType<CareerManagerLicensesScreen>())
            screen.PopulateTextsFromIndex(screen.IndexOfFirstDisplayedEntry); //B99
    }

    private void OnClientboundGarageUnlockPacket(ClientboundGarageUnlockPacket packet)
    {
        LogDebug(() => $"Received new garage {packet.Id}");
        LicenseManager.Instance.UnlockGarage(Globals.G.types.garages.Find(g => g.id == packet.Id));
    }

    private void OnClientboundDebtStatusPacket(ClientboundDebtStatusPacket packet)
    {
        CareerManagerDebtControllerPatch.HasDebt = packet.HasDebt;
    }

    public void SendLicensePurchaseRequest(string id, bool isJobLicense)
    {
        SendPacketToServer(new ServerboundLicensePurchaseRequestPacket
        {
            Id = id,
            IsJobLicense = isJobLicense
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendPayFeesRequest()
    {
        SendPacketToServer(new ServerboundPayFeesRequestPacket(), DeliveryMethod.ReliableUnordered);
    }
}
