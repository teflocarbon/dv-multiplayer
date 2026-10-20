using DV.ServicePenalty;
using DV.Utils;
using Multiplayer.Shared.Session;
using Multiplayer.Server.Session;

namespace Multiplayer.Server.Career;

public class NetworkedCareerManagerDebtController : SingletonBehaviour<NetworkedCareerManagerDebtController>
{
    private CareerManagerDebtController controller;

    protected override void Awake()
    {
        base.Awake();
        controller = GetComponent<CareerManagerDebtController>();
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        controller.DebtListsUpdated += Server_OnDebtListsUpdated;
    }

    protected override void OnDestroy()
    {
        if (UnloadWatcher.isUnloading)
            return;
        controller.DebtListsUpdated -= Server_OnDebtListsUpdated;
    }

    private bool? lastSentHasDebt;

    private void Server_OnDebtListsUpdated() => SendDebtStatusIfChanged();

    // Sent when "has debt" changes. It used to go out only at exactly 0 or 1 debts, so going from
    // none to two (a derail with two locos) left clients thinking there was no debt. Also checked
    // after each refresh of the debts (CareerManagerDebtControllerServerPatch).
    public void SendDebtStatusIfChanged()
    {
        if (controller == null || !NetworkLifecycle.Instance.IsHost())
            return;
        bool hasDebt = controller.NumberOfNonZeroPricedDebts > 0;
        if (lastSentHasDebt == hasDebt)
            return;
        lastSentHasDebt = hasDebt;
        NetworkServer.Instance.SendDebtStatus(hasDebt);
    }
}
