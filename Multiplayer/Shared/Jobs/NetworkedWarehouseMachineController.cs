using DV.Logic.Job;
using Multiplayer.Shared.Session;
using System.Collections.Generic;



namespace Multiplayer.Shared.Jobs;

/// <summary>
/// A warehouse machine's NetId on every game. The server runs the machine and sends what its
/// screen says (<c>NetworkServer.Jobs</c>, <c>Server/Jobs/WarehouseMachineControllerServerPatch</c>);
/// a client asks it to load or unload and shows its screen (<c>Client/Jobs</c>).
/// </summary>
public class NetworkedWarehouseMachineController : IdMonoBehaviour<ushort, NetworkedWarehouseMachineController>
{
    #region Lookup Cache
    private static readonly Dictionary<WarehouseMachineController, NetworkedWarehouseMachineController> warehouseMachineControllerToNetworked = [];
    private static readonly Dictionary<WarehouseMachine, NetworkedWarehouseMachineController> warehouseMachineToNetworked = [];

    public static bool Get(ushort netId, out NetworkedWarehouseMachineController obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedWarehouseMachineController> rawObj);
        obj = (NetworkedWarehouseMachineController)rawObj;
        return b;
    }

    public static bool TryGetNetId(WarehouseMachineController warehouseMachineController, out ushort netId)
    {
        if (GetFromWarehouseMachineController(warehouseMachineController, out var networkedWarehouseMachineController))
        {
            netId = networkedWarehouseMachineController.NetId;
            return true;
        }

        netId = 0;
        return false;
    }

    public static bool TryGetNetId(WarehouseMachine warehouseMachine, out ushort netId)
    {
        var networkedWarehouseMachineController = GetFromWarehouseMachine(warehouseMachine);
        if (networkedWarehouseMachineController != null)
        {
            netId = networkedWarehouseMachineController.NetId;
            return true;
        }

        netId = 0;
        return false;
    }

    public static bool TryGet(ushort netId, out WarehouseMachineController warehouseMachineController)
    {
        if (Get(netId, out var networkedWarehouseMachineController))
        {
            warehouseMachineController = networkedWarehouseMachineController.WarehouseMachineController;
            return true;
        }

        warehouseMachineController = null;
        return false;
    }

    public static bool TryGet(ushort netId, out WarehouseMachine warehouseMachine)
    {
        if (Get(netId, out var networkedWarehouseMachineController))
        {
            warehouseMachine = networkedWarehouseMachineController.WarehouseMachine;
            return true;
        }

        warehouseMachine = null;
        return false;
    }

    public static bool GetFromWarehouseMachineController(WarehouseMachineController warehouseMachineController, out NetworkedWarehouseMachineController networkedWarehouseMachineController)
    {
        return warehouseMachineControllerToNetworked.TryGetValue(warehouseMachineController, out networkedWarehouseMachineController);
    }

    public static NetworkedWarehouseMachineController GetFromWarehouseMachine(WarehouseMachine warehouseMachine)
    {
        //fast path lookup
        if (warehouseMachineToNetworked.TryGetValue(warehouseMachine, out NetworkedWarehouseMachineController networkedWarehouseMachineController))
            return networkedWarehouseMachineController;

        //cache miss, try to find parent WarehouseMachineController
        var warehouseMachineController = GetFomId(warehouseMachine.ID);
        if (warehouseMachineController != null)
        {
            //Warehouse Machine Controller found, check for NetworkedWarehouseMachineController
            if (!GetFromWarehouseMachineController(warehouseMachineController, out networkedWarehouseMachineController) && networkedWarehouseMachineController != null)
                warehouseMachineToNetworked[warehouseMachine] = networkedWarehouseMachineController;
        }

        return networkedWarehouseMachineController;
    }

    private static WarehouseMachineController GetFomId(string ID)
    {
        foreach (var warehouse in WarehouseMachineController.allControllers)
        {
            if (warehouse.warehouseMachine.ID == ID)
            {
                return warehouse;
            }
        }
        return null;
    }

    #endregion
    protected override bool IsIdServerAuthoritative => false;

    public string Id => WarehouseMachine?.ID;
    public WarehouseMachineController WarehouseMachineController { get; private set; }
    public WarehouseMachine WarehouseMachine => WarehouseMachineController?.warehouseMachine;

    protected override void Awake()
    {
        base.Awake();
        WarehouseMachineController = GetComponent<WarehouseMachineController>();
        warehouseMachineControllerToNetworked[WarehouseMachineController] = this;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();

        warehouseMachineControllerToNetworked.Remove(WarehouseMachineController);

        if (WarehouseMachineController.warehouseMachine != null)
            warehouseMachineToNetworked.Remove(WarehouseMachineController.warehouseMachine);
    }
}
