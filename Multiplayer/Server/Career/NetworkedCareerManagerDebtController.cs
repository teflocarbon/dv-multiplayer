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

    // Sent when "has debt" changes. It used to go out only at exactly 0 or 1 debts, so going from
    // none to two (a derail with two locos) left clients thinking there was no debt.
    private void Server_OnDebtListsUpdated()
    {
        bool hasDebt = controller.NumberOfNonZeroPricedDebts > 0;
        if (lastSentHasDebt == hasDebt)
            return;
        lastSentHasDebt = hasDebt;
        NetworkServer.Instance.SendDebtStatus(hasDebt);
    }
}
