using DV.Logic.Job;
using DV.Utils;
using Multiplayer.Shared.Utils;
using Multiplayer.Shared.Session;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Multiplayer.Shared.Jobs;

/// <summary>
/// A station on every game: its NetId (a hash of the station ID, the same everywhere), its
/// validator and its networked jobs. The server registers the station's jobs and broadcasts them
/// (Server/Jobs/ServerStationController); a remote client builds its copies from those packets
/// (Client/Jobs/ClientStationJobs).
/// </summary>
public class NetworkedStationController : IdMonoBehaviour<uint, NetworkedStationController>
{
    #region Lookup Cache
    private static readonly Dictionary<StationController, NetworkedStationController> stationControllerToNetworkedStationController = [];
    private static readonly Dictionary<string, NetworkedStationController> stationIdToNetworkedStationController = [];
    private static readonly Dictionary<string, StationController> stationIdToStationController = [];
    private static readonly Dictionary<Station, NetworkedStationController> stationToNetworkedStationController = [];
    private static readonly Dictionary<JobValidator, NetworkedStationController> jobValidatorToNetworkedStation = [];
    private static readonly List<JobValidator> jobValidators = [];

    public static bool Get(uint netId, out NetworkedStationController obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<uint, NetworkedStationController> rawObj);
        obj = (NetworkedStationController)rawObj;
        return b;
    }

    public static bool TryGet(uint netId, out StationController stationController)
    {
        if (Get(netId, out var networkedStationController))
        {
            stationController = networkedStationController.StationController;
            return true;
        }

        stationController = null;
        return false;
    }

    public static bool TryGet(uint netId, out Station station)
    {
        if (Get(netId, out var networkedStationController))
        {
            station = networkedStationController.StationController.logicStation;
            return true;
        }

        station = null;
        return false;
    }

    public static bool TryGet(uint netId, out JobValidator jobValidator)
    {
        if (Get(netId, out var networkedStationController))
        {
            jobValidator = networkedStationController.JobValidator;
            return true;
        }

        jobValidator = null;
        return false;
    }

    public static bool TryGetNetId(StationController stationController, out uint netId)
    {
        if (GetFromStationController(stationController, out var networkedStationController))
        {
            netId = networkedStationController.NetId;
            return true;
        }

        netId = 0;
        return false;
    }

    public static bool TryGetNetId(Station station, out uint netId)
    {
        if (GetFromStation(station, out var networkedStationController))
        {
            netId = networkedStationController.NetId;
            return true;
        }

        netId = 0;
        return false;
    }

    public static bool TryGetNetId(JobValidator jobValidator, out uint netId)
    {
        if (GetFromJobValidator(jobValidator, out var networkedStationController))
        {
            netId = networkedStationController.NetId;
            return true;
        }

        netId = 0;
        return false;
    }

    public static bool GetStationController(uint netId, out StationController obj)
    {
        bool b = Get(netId, out NetworkedStationController networkedStationController);
        obj = b ? networkedStationController.StationController : null;
        return b;
    }

    public static bool GetFromStationId(string stationId, out NetworkedStationController networkedStationController)
    {
        return stationIdToNetworkedStationController.TryGetValue(stationId, out networkedStationController);
    }

    public static bool GetFromStation(Station station, out NetworkedStationController networkedStationController)
    {
        return stationToNetworkedStationController.TryGetValue(station, out networkedStationController);
    }
    public static bool GetStationControllerFromStationId(string stationId, out StationController stationController)
    {
        return stationIdToStationController.TryGetValue(stationId, out stationController);
    }

    public static bool GetFromStationController(StationController stationController, out NetworkedStationController networkedStationController)
    {
        return stationControllerToNetworkedStationController.TryGetValue(stationController, out networkedStationController);
    }

    public static bool GetFromJobValidator(JobValidator jobValidator, out NetworkedStationController networkedStationController)
    {
        if (jobValidator == null)
        {
            networkedStationController = null;
            return false;
        }

        return jobValidatorToNetworkedStation.TryGetValue(jobValidator, out networkedStationController);
    }

    public static void RegisterStationController(NetworkedStationController networkedStationController, StationController stationController)
    {
        string stationID = stationController.logicStation.ID;
        networkedStationController.NetId = StringHashing.Fnv1aHash(stationID);

        stationControllerToNetworkedStationController[stationController] = networkedStationController;
        stationIdToNetworkedStationController[stationID] = networkedStationController;
        stationIdToStationController[stationID] = stationController;
        stationToNetworkedStationController[stationController.logicStation] = networkedStationController;
    }

    public static void QueueJobValidator(JobValidator jobValidator)
    {
        //Multiplayer.Log($"QueueJobValidator() {jobValidator.transform.parent.name}");

        jobValidators.Add(jobValidator);
    }

    private static void RegisterJobValidator(JobValidator jobValidator, NetworkedStationController stationController)
    {
        //Multiplayer.Log($"RegisterJobValidator() {jobValidator.transform.parent.name}, {stationController.name}");
        stationController.JobValidator = jobValidator;
        jobValidatorToNetworkedStation[jobValidator] = stationController;
    }
    #endregion

    protected override bool IsIdServerAuthoritative => false;

    public StationController StationController;

    public JobValidator JobValidator;

    public HashSet<NetworkedJob> NetworkedJobs { get; } = [];

    // The station's own job lists; null until its logic station exists.
    public List<Job> AvailableJobs => StationController.logicStation?.availableJobs;
    public List<Job> TakenJobs => StationController.logicStation?.takenJobs;
    public List<Job> AbandonedJobs => StationController.logicStation?.abandonedJobs;
    public List<Job> CompletedJobs => StationController.logicStation?.completedJobs;

    /// <summary>The logic station exists and the station is in the lookups.</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>Raised once, when <see cref="IsRegistered"/> becomes true.</summary>
    public event Action Registered;

    protected override void Awake()
    {
        base.Awake();
        StationController = GetComponent<StationController>();
        StartCoroutine(WaitForLogicStation());
    }

    protected void OnDisable()
    {

        if (UnloadWatcher.isQuitting)
            return;

        if (StationController != null)
        {
            string stationId = StationController.logicStation?.ID;

            stationControllerToNetworkedStationController.Remove(StationController);

            if (stationId != null)
            {
                stationIdToNetworkedStationController.Remove(stationId);
                stationIdToStationController.Remove(stationId);
            }

            if (StationController.logicStation != null)
                stationToNetworkedStationController.Remove(StationController.logicStation);

            if (JobValidator != null)
            {
                jobValidatorToNetworkedStation.Remove(JobValidator);
                jobValidators.Remove(this.JobValidator);
            }
        }

        Destroy(this);
    }

    private IEnumerator WaitForLogicStation()
    {
        while (StationController.logicStation == null)
            yield return null;

        RegisterStationController(this, StationController);

        // The server registers the jobs the station already has here (ServerStationController).
        IsRegistered = true;
        Registered?.Invoke();

        //Multiplayer.Log($"NetworkedStation.Awake({StationController.logicStation.ID})");

        foreach (JobValidator validator in jobValidators)
        {
            string stationName = validator.transform.parent.name ?? "";
            stationName += "_office_anchor";

            if (this.transform.parent.name.Equals(stationName, StringComparison.OrdinalIgnoreCase))
            {
                JobValidator = validator;
                RegisterJobValidator(validator, this);
                jobValidators.Remove(validator);
                break;
            }
        }
    }
}
