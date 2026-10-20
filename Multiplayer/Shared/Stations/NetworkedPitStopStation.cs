using DV.CabControls;
using DV.CashRegister;
using DV.Optimizers;
using DV.ThingTypes;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using static CashRegisterModule;

namespace Multiplayer.Shared.Stations;

/// <summary>
/// Handles networked interactions with pit stop stations, including vehicle selection and resource management.
/// </summary>
public class NetworkedPitStopStation : IdMonoBehaviour<ushort, NetworkedPitStopStation>
{
    #region Lookup Cache
    private static readonly Dictionary<PitStopStation, NetworkedPitStopStation> pitStopStationToNetworkedPitStopStation = [];

    public static bool Get(ushort netId, out NetworkedPitStopStation obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedPitStopStation> rawObj);
        obj = (NetworkedPitStopStation)rawObj;
        return b;
    }

    public static void InitialisePitStops()
    {

        //Find all pitstop stations that are placed on the map
        //sort them by their hierarchy path for consistent ordering
        var stations = Resources.FindObjectsOfTypeAll<PitStopStation>()
            .Where(p => p.transform.parent != null)
            .OrderBy(p => p.transform.position.x)
            .ThenBy(p => p.transform.position.y)
            .ThenBy(p => p.transform.position.z)
            .ToArray();

        pitStopStationToNetworkedPitStopStation.Clear();

        //Multiplayer.LogDebug(() => $"InitialisePitStops() Found: {stations?.Length}");

        foreach (var station in stations)
        {
            var netStation = station.GetOrAddComponent<NetworkedPitStopStation>();
            netStation.Station = station;

            if (netStation.NetId == 0)
                netStation.Awake();

            pitStopStationToNetworkedPitStopStation[station] = netStation;

            //Multiplayer.LogDebug(() => $"InitialisePitStops() Station: {station?.GetObjectPath()}, netId: {netStation.NetId}");

            CoroutineManager.Instance.StartCoroutine(netStation.Init());

        }
    }
    #endregion

    protected override bool IsIdServerAuthoritative => false;

    const float LOADING_TIMEOUT = 5f;
    const float ROTATION_SMOOTH_SPEED = 0.5f;

    #region Server variables
    /// <summary>
    /// Set by the server while it applies another player's interaction here
    /// (<c>Server/Stations/ServerPitStopStation</c>), so this game doesn't send it back.
    /// </summary>
    public bool ProcessingAsHost { get; set; } = false;
    #endregion

    #region Common variables
    public PitStopStation Station { get; set; }
    public string StationName { get; private set; }

    private bool initialised = false;

    private CashRegisterWithModules register;

    private ResourceType[] resourceTypes = [];

    private RotaryBase carSelectorGrab;
    private LeverBase faucetPositionerGrab;
    private HingeJointAngleFix faucetPositioner;
    private SteppedJoint faucetCrankSteppedJoint;

    private readonly Dictionary<ResourceType, (RotaryAmplitudeChecker amplitudeChecker, LocoResourceModule module, Action<int> leverHandler)> leverStateLookup = [];
    //private readonly Dictionary<ResourceType, LeverBase> grabbedHandlerLookup = [];
    private readonly Dictionary<ResourceType, LeverBase> leverLookup = [];
    private readonly Dictionary<ResourceType, NetworkedPluggableObject> resourceToPluggableObject = [];
    private readonly Dictionary<ResourceType, LocoResourceModule> resourceTypeToLocoResourceModule = [];

    private readonly Dictionary<ResourceType, bool> isResourceGrabbedDict = [];
    private readonly Dictionary<ResourceType, bool> isResourceRemoteGrabbedDict = [];
    private readonly Dictionary<ResourceType, float> lastRemoteValueDict = [];

    private bool faucetTargetReached = true;

    private Coroutine faucetMoveCoroutine;

    private bool Refreshed = false;
    #endregion

    #region Unity
    protected override void Awake()
    {
        if (NetId == 0)
            base.Awake();

        StationName = $"{transform?.parent?.parent?.name} - {transform?.parent?.name}";

        // Ensure host can interact
        if (NetworkLifecycle.Instance.IsHost())
            Refreshed = true;
    }

    protected void OnDisable()
    {
        if (!NetworkLifecycle.Instance.IsHost())
            Refreshed = false;
    }

    protected override void OnDestroy()
    {
        pitStopStationToNetworkedPitStopStation.Remove(Station);

        if (carSelectorGrab != null)
        {
            carSelectorGrab.Grabbed -= CarSelectorGrabbed;
            carSelectorGrab.Ungrabbed -= CarSelectorUnGrabbed;
        }

        if (Station?.pitstop != null)
        {
            Station.pitstop.CarSelected -= CarSelected;
        }

        if (faucetPositionerGrab != null)
        {
            faucetPositionerGrab.Grabbed -= FaucetCrankGrabbed;
            faucetPositionerGrab.Ungrabbed -= FaucetCrankUnGrabbed;
        }

        if (faucetCrankSteppedJoint != null)
        {
            faucetCrankSteppedJoint.PositionChanged -= FaucetCrankPositionChanged;
        }

        foreach (var kvp in leverStateLookup)
        {
            var (leverAmplitudeChecker, _, leverStateHandler) = kvp.Value;
            leverAmplitudeChecker.RotaryStateChanged -= leverStateHandler;
        }

        leverStateLookup.Clear();
        //grabbedHandlerLookup.Clear();
        leverLookup.Clear();
        base.OnDestroy();
    }
    #endregion

    #region Server
    // What the server's part (Server/Stations/ServerPitStopStation) reads and records.

    public Dictionary<ResourceType, ushort> GetPluggables()
    {
        Dictionary<ResourceType, ushort> keyValuePairs = [];
        foreach (var kvp in resourceToPluggableObject)
            keyValuePairs.Add(kvp.Key, kvp.Value.NetId);

        return keyValuePairs;
    }

    /// <summary>Every station on the map, once <see cref="InitialisePitStops"/> has run.</summary>
    public static IReadOnlyCollection<NetworkedPitStopStation> All => pitStopStationToNetworkedPitStopStation.Values;

    /// <summary>True once <c>Init</c> has found the station's controls, resource modules and plugs.</summary>
    public bool IsInitialised => initialised;

    public IEnumerable<LocoResourceModule> ResourceModules => resourceTypeToLocoResourceModule.Values;

    public IEnumerable<NetworkedPluggableObject> Plugs => resourceToPluggableObject.Values;

    /// <summary>The water tower faucet's notch, or -1 for a station without one.</summary>
    public int FaucetNotch => faucetCrankSteppedJoint != null ? faucetCrankSteppedJoint.currentNotch : -1;

    /// <summary>The server sent the players this resource's units to buy.</summary>
    public void NoteUnitsSent(ResourceType resourceType, float units) => lastRemoteValueDict[resourceType] = units;
    #endregion


    #region Common
    /// <summary>
    /// Looks up Pluggable object by resource type
    /// </summary>
    public bool TryGetPluggable(ResourceType type, out NetworkedPluggableObject netPluggable)
    {
        return resourceToPluggableObject.TryGetValue(type, out netPluggable);
    }

    /// <summary>
    /// Initializes the pit stop station and sets up event handlers for grab interactions.
    /// </summary>
    private IEnumerator Init()
    {
        Multiplayer.Log($"Initialising Station {Station.GetObjectPath()}");

        while (Station?.pitstop == null)
            yield return new WaitForEndOfFrame();

        Multiplayer.Log($"Pitstop {Station.GetObjectPath()} initialised");

        // Wait for cash registers to load
        yield return new WaitUntil(() => transform.parent.GetComponentInChildren<CashRegisterWithModules>(true) != null);
        register = transform.parent.GetComponentInChildren<CashRegisterWithModules>(true);

        //Wait for levers an knobs to load
        yield return new WaitUntil(() => GetComponentInChildren<RotaryBase>(true) != null);
        carSelectorGrab = GetComponentInChildren<RotaryBase>(true);

        if (carSelectorGrab != null)
        {
            Multiplayer.LogDebug(() => $"NetworkedPitStopStation.Init() Grab Handler found: {carSelectorGrab != null}, Name: {carSelectorGrab.name}");
            carSelectorGrab.Grabbed += CarSelectorGrabbed;
            carSelectorGrab.Ungrabbed += CarSelectorUnGrabbed;

            Station.pitstop.CarSelected += CarSelected;
        }

        // Water tower positioner handle
        var faucetGo = transform.parent.FindChildrenByName("FaucetCrank").FirstOrDefault();
        faucetPositionerGrab = faucetGo?.GetComponentInChildren<LeverBase>(true);
        faucetPositioner = faucetGo?.GetComponentInChildren<HingeJointAngleFix>(true);
        faucetCrankSteppedJoint = faucetGo?.GetComponentInChildren<SteppedJoint>(true);

        if (faucetPositionerGrab != null && faucetPositioner != null && faucetCrankSteppedJoint != null)
        {
            Multiplayer.LogDebug(() => $"NetworkedPitStopStation.Init() Grab Handler found: {carSelectorGrab != null}, Name: {carSelectorGrab.name}");
            faucetPositionerGrab.Grabbed += FaucetCrankGrabbed;
            faucetPositionerGrab.Ungrabbed += FaucetCrankUnGrabbed;
            faucetCrankSteppedJoint.PositionChanged += FaucetCrankPositionChanged;
        }

        //build dictionaries
        var resourceModules = Station?.locoResourceModules?.resourceModules;
        if (resourceModules == null)
        {
            Multiplayer.LogWarning($"No resource modules found for station {StationName}");
            yield break;
        }

        resourceTypes = resourceModules?.Select(m => m.resourceType).ToArray();

        foreach (var resourceType in resourceTypes)
        {
            isResourceGrabbedDict[resourceType] = false;
            isResourceRemoteGrabbedDict[resourceType] = false;
        }

        StringBuilder sb = new();
        sb.AppendLine($"NetworkedPitStopStation.Init() {StationName} resources:");

        if (resourceModules != null)
        {
            foreach (var resourceModule in resourceModules)
            {
                yield return new WaitUntil(() => resourceModule.initialized);

                resourceTypeToLocoResourceModule[resourceModule.resourceType] = resourceModule;

                var checker = resourceModule.GetComponentInChildren<RotaryAmplitudeChecker>();
                var grab = resourceModule.GetComponentInChildren<LeverBase>();
                var lever = resourceModule.GetComponentInChildren<LeverBase>();
                if (checker != null && grab != null)
                {

                    //Delegates for handlers
                    void LeverStatehandler(int state) => OnLeverPositionChange(resourceModule, state);

                    //Subscribe
                    checker.RotaryStateChanged += LeverStatehandler;

                    //Store delegate
                    leverStateLookup[resourceModule.resourceType] = (checker, resourceModule, LeverStatehandler);
                    //grabbedHandlerLookup[resourceModule.resourceType] = grab;
                    leverLookup[resourceModule.resourceType] = lever;

                    //sb.AppendLine($"\t{resourceModule.resourceType}, Grab Handler found: {grab != null}, Name: {grab.name}");
                    sb.AppendLine($"\t{resourceModule.resourceType}, Rotary Amplitude Handler found: {checker != null}, Name: {checker.name}");
                }
                else
                {
                    sb.AppendLine($"\t{resourceModule.resourceType}, Failed to find component. Grab Handler found: {grab != null}, Amplitude Checker found: {checker != null}");
                }

                var plug = resourceModule.resourceHose;
                if (plug != null)
                {
                    var netPlug = plug.GetOrAddComponent<NetworkedPluggableObject>();
                    resourceToPluggableObject[resourceModule.resourceType] = netPlug;
                    netPlug.InitPitStop(this);
                }
            }
        }
        else
        {
            sb.AppendLine($"ERROR Station is Null {Station == null}, resource modules: {Station?.locoResourceModules}");
        }

        Multiplayer.LogDebug(() => sb.ToString());

        initialised = true;
    }

    private IEnumerator SetUnitsDelayed(LocoResourceModule rm)
    {
        if (rm == null || !isResourceRemoteGrabbedDict.ContainsKey(rm.resourceType))
            yield break;

        var resourceType = rm.resourceType;

        yield return new WaitUntil(() => !isResourceRemoteGrabbedDict[resourceType] && !rm.IsFlowing);
        yield return null;

        if (!lastRemoteValueDict.ContainsKey(resourceType))
        {
            if (resourceTypeToLocoResourceModule.ContainsKey(resourceType))
                lastRemoteValueDict[resourceType] = rm.Data.unitsToBuy;
            else
                lastRemoteValueDict[resourceType] = 0f;
        }

        SetUnits(rm, lastRemoteValueDict[resourceType]);
    }

    private void SetUnits(LocoResourceModule rm, float units)
    {
        if (rm == null)
            return;

        float clamped = Mathf.Clamp(units, rm.AbsoluteMinValue, rm.AbsoluteMaxValue);

        lastRemoteValueDict[rm.resourceType] = clamped;

        Multiplayer.LogDebug(() => $"NetworkedPitStopStation.SetUnits({rm.resourceType}, {units}) clamped: {clamped}, flowMultiplier: {rm.flowMultiplier}, flowRate: {rm.flowRate}, isFlowing: {rm.IsFlowing}");
        rm.SetUnitsToBuy(clamped);
    }

    /// <summary>
    /// Initialises data elements for each car in each resource module
    /// </summary>
    public void InitialiseData()
    {
        foreach (var resourceModule in Station.locoResourceModules.resourceModules)
        {
            if (resourceModule == null)
                continue;

            // Make sure resourceData has enough entries for all cars
            while (resourceModule.resourceData.Count < Station.pitstop.carList.Count)
            {
                if (resourceModule.resourceData.Count > 0)
                    resourceModule.resourceData.Add(new CashRegisterModuleData(resourceModule.resourceData[0]));
                else
                    resourceModule.resourceData.Add(new CashRegisterModuleData());
            }
        }
    }

    /// <summary>
    /// Sets the rotation of the faucet handle to the specified percentage
    /// </summary>
    public void SetFaucetRotation(int notch)
    {
        if (faucetPositioner == null)
            return;

        if (faucetMoveCoroutine != null)
            StopCoroutine(faucetMoveCoroutine);

        faucetTargetReached = false;

        faucetMoveCoroutine = StartCoroutine(SmoothMoveToNotch(notch));
    }

    private IEnumerator SmoothMoveToNotch(int targetNotch)
    {
        float min = faucetCrankSteppedJoint.joint.limits.min;
        float max = faucetCrankSteppedJoint.joint.limits.max;

        float startAngle = faucetCrankSteppedJoint.jointAngleFix.Angle;
        float endAngle = faucetCrankSteppedJoint.AngleForNotch(targetNotch);
        float elapsed = 0f;

        //Multiplayer.LogDebug(() => $"NetworkedPitStopStation.SmoothMoveToNotch() targetNotch: {targetNotch}, startAngle: {startAngle}, endAngle: {endAngle}");

        targetNotch = Mathf.Clamp(targetNotch, 0, faucetCrankSteppedJoint.notches - 1);

        while (faucetCrankSteppedJoint.currentNotch != targetNotch && elapsed < 2f)
        {
            //Multiplayer.LogDebug(() => $"NetworkedPitStopStation.SmoothMoveToNotch() targetNotch: {targetNotch}, currentNotch: {faucetCrankSteppedJoint.currentNotch}");
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / ROTATION_SMOOTH_SPEED);
            float newAngle = Mathf.Lerp(startAngle, endAngle, t);

            //Multiplayer.LogDebug(() => $"NetworkedPitStopStation.SmoothMoveToNotch() targetNotch: {targetNotch}, startAngle: {startAngle}, endAngle: {endAngle}, newAngleUnclamped: {newAngle}");

            newAngle = Mathf.Clamp(newAngle, min, max);

            //Multiplayer.LogDebug(() => $"NetworkedPitStopStation.SmoothMoveToNotch() targetNotch: {targetNotch}, startAngle: {startAngle}, endAngle: {endAngle}, newAngleClamped: {newAngle}");

            var spring = faucetCrankSteppedJoint.joint.spring;
            spring.targetPosition = newAngle;
            faucetCrankSteppedJoint.joint.spring = spring;

            //Multiplayer.LogDebug(() => $"NetworkedPitStopStation.SmoothMoveToNotch()targetNotch: {targetNotch}, newAngle: {newAngle}, t: {t}, elapsed: {elapsed}");

            yield return null;
        }

        yield return null;

        faucetTargetReached = true;

        //Multiplayer.LogDebug(() => $"NetworkedPitStopStation.SmoothMoveToNotch() Finished moving to notch: {targetNotch}, final angle: {faucetCrankSteppedJoint.jointAngleFix.Angle}");
    }


    /// <summary>
    /// Set the car selection index
    /// </summary>
    public void SetCarSelection(int selection)
    {
        Multiplayer.LogDebug(() => $"NetworkedPitStopStation.SetCarSelection({selection}) [{StationName}, {NetId}] car count: {Station.pitstop.carList.Count}");
        if (selection >= 0 && selection < Station.pitstop.carList.Count)
        {
            Station.pitstop.currentCarIndex = selection;
            Station.pitstop.OnCarSelectionChanged();
        }
        else
        {
            Multiplayer.LogWarning($"Pit Stop car selection change out of bounds! Selected: {selection}, current car count: {Station.pitstop.carList.Count}");
        }
    }
    #endregion


    #region Client
    /// <summary>
    /// Handles grab interactions for the car selector knob.
    /// </summary>
    private void CarSelectorGrabbed(ControlImplBase _)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket || (NetworkLifecycle.Instance.IsHost() && ProcessingAsHost))
            return;

        //Prevent new players/players entering the area from sending packets until initalised
        if (!Refreshed)
            return;

        Multiplayer.LogDebug(() => $"CarSelectorGrabbed() {StationName}");
        StationsClient.Current?.SendPitStopInteractionPacket(NetId, PitStopStationInteractionType.CarSelectorGrab, null, 0);
    }

    /// <summary>
    /// Handles end of grab (release) interactions for the car selector knob.
    /// </summary>
    private void CarSelectorUnGrabbed(ControlImplBase _)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket || (NetworkLifecycle.Instance.IsHost() && ProcessingAsHost))
            return;

        //Prevent new players/players entering the area from sending packets until initalised
        if (!Refreshed)
            return;

        Multiplayer.LogDebug(() => $"CarSelectorUnGrabbed() {StationName}");
        StationsClient.Current?.SendPitStopInteractionPacket(NetId, PitStopStationInteractionType.CarSelectorUngrab, null, Station.pitstop.SelectedIndex);
    }

    /// <summary>
    /// Handles change of selected car events.
    /// </summary>
    private void CarSelected()
    {
        Multiplayer.LogDebug(() => $"CarSelected() [{StationName}, {NetId}]  selected: {Station.pitstop.SelectedIndex}");

        if (NetworkLifecycle.Instance.IsProcessingPacket || (NetworkLifecycle.Instance.IsHost() && ProcessingAsHost))
            return;

        // Prevent new players/players entering the area from sending packets until initalised
        if (!Refreshed)
            return;

        Multiplayer.LogDebug(() => $"CarSelected() selected: {Station.pitstop.SelectedIndex}");

        StationsClient.Current?.SendPitStopInteractionPacket(NetId, PitStopStationInteractionType.CarSelection, null, Station.pitstop.SelectedIndex);
    }

    /// <summary>
    /// Handles grab interactions for resource module levers.
    /// </summary>
    /// <param name="module">The resource module being grabbed.</param>
    private void OnLeverPositionChange(LocoResourceModule module, int state)
    {
        // Prevent new players/players entering the area from sending packets until initalised
        if (!Refreshed)
            return;

        Multiplayer.LogDebug(() => $"OnLeverPositionChange() {StationName}, module: {module.resourceType}, state: {state}");

        if (state == 0)
        {
            //lever returned home
            isResourceGrabbedDict[module.resourceType] = false;
        }
        else
        {
            isResourceGrabbedDict[module.resourceType] = true;
        }

        StationsClient.Current?.SendPitStopInteractionPacket(NetId, PitStopStationInteractionType.LeverState, module.resourceType, state);
    }

    /// <summary>
    /// Handles grab interactions for the faucet positioning handle (water towers).
    /// </summary>
    private void FaucetCrankGrabbed(ControlImplBase _)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket || (NetworkLifecycle.Instance.IsHost() && ProcessingAsHost))
            return;

        //Prevent new players/players entering the area from sending packets until initalised
        if (!Refreshed)
            return;

        Multiplayer.LogDebug(() => $"FaucetCrankGrabbed() {StationName}");

        int notch = -1;
        if (faucetCrankSteppedJoint != null)
        {
            notch = faucetCrankSteppedJoint.currentNotch;
        }

        StationsClient.Current?.SendPitStopInteractionPacket(NetId, PitStopStationInteractionType.FaucetGrab, null, notch);
    }

    /// <summary>
    /// Handles end of grab (release) interactions for the faucet positioning handle (water towers).
    /// </summary>
    private void FaucetCrankUnGrabbed(ControlImplBase _)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket || (NetworkLifecycle.Instance.IsHost() && ProcessingAsHost))
            return;

        //Prevent new players/players entering the area from sending packets until initalised
        if (!Refreshed)
            return;

        Multiplayer.LogDebug(() => $"FaucetCrankUnGrabbed() {StationName}, percentage: {faucetPositioner.Percentage}");

        int notch = -1;
        if (faucetCrankSteppedJoint != null)
            notch = faucetCrankSteppedJoint.currentNotch;

        StationsClient.Current?.SendPitStopInteractionPacket(NetId, PitStopStationInteractionType.FaucetUngrab, null, notch);
    }

    /// <summary>
    /// Handles non-grab changes to the faucet positioning handle (water towers), e.g. scrolling.
    /// </summary>
    private void FaucetCrankPositionChanged(ValueChangedEventArgs args)
    {
        Multiplayer.LogDebug(() => $"FaucetCrankPositionChanged() {StationName}, oldValue: {args.oldValue}, newValue: {args.newValue}, delta: {args.delta}");

        if (NetworkLifecycle.Instance.IsProcessingPacket || (NetworkLifecycle.Instance.IsHost() && ProcessingAsHost))
            return;

        //Prevent new players/players entering the area from sending packets until initalised
        if (!Refreshed)
            return;

        if (!faucetTargetReached)
        {
            Multiplayer.LogDebug(() => $"FaucetCrankPositionChanged() {StationName} faucet target not reached, ignoring position change");
            return;
        }

        int notch = -1;
        if (faucetCrankSteppedJoint != null)
        {
            notch = faucetCrankSteppedJoint.currentNotch;
        }

        StationsClient.Current?.SendPitStopInteractionPacket(NetId, PitStopStationInteractionType.FaucetPosition, null, notch);
    }

    public void ProcessBulkUpdate(ClientboundPitStopBulkUpdatePacket packet)
    {
        // Packet is broken up due to SubscribeResusable reusing/overwriting packet data
        CoroutineManager.Instance.StartCoroutine(ProcessBulkUpdate_Internal(packet.CarCount, packet.CarSelection, packet.FaucetNotch, packet.ResourceData, packet.PlugData));
    }

    private IEnumerator ProcessBulkUpdate_Internal(int carCount, int carSelection, int faucetNotch, LocoResourceModuleData[] resourceData, PitStopPlugData[] plugData)
    {
        float time = Time.time;

        // Allow pit stop to complete loading and cars to load in the pit stop
        Multiplayer.Log($"Processing bulk update for [{StationName}, {NetId}]");

        yield return new WaitUntil
        (
            () =>
            {
                Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] Initialised: {initialised}, Active:{Station?.gameObject?.activeInHierarchy}, Packet Car Count: {carCount}, Station Car Count: {Station.pitstop?.carList?.Count}, Car Count Matched: {carCount == Station.pitstop?.carList?.Count}, time elapsed: {(Time.time - time)}");

                if (Station?.gameObject?.activeInHierarchy == false)
                {
                    // Don't time out if we're waiting for the object to be enabled
                    time = Time.time;
                    return false;
                }

                // Try to trigger colliders manually
                if (initialised && Station?.pitstop?.carList != null && carCount != Station.pitstop.carList.Count)
                    Station?.pitstop?.RefreshPitStopCarPresence();

                return (initialised && Station?.pitstop?.carList != null && carCount == Station.pitstop.carList.Count)
                || (Time.time - time) > LOADING_TIMEOUT;

            }
        );


        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();

        if ((Time.time - time) > LOADING_TIMEOUT)
            Multiplayer.LogWarning($"PitStop [{StationName}] timed out waiting for load. PitStop Initialised: {initialised}, Packet Car Count: {carCount}, Station Car Count: {Station.pitstop?.carList?.Count}, Car Count Matched: {carCount == Station.pitstop?.carList?.Count}");

        Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}] Car count: {carCount}, resource data count: {resourceData.Count()}, resource data: [{string.Join(", ", resourceData.Select(x => $"{x.ResourceType}: {{{string.Join(", ", x.Values)}}}"))}]");
        // Make sure the data elements exist prior to attempting to load them
        InitialiseData();

        Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}] PitStop bulk data car count matches. Station module count: {Station?.locoResourceModules?.resourceModules?.Count()}, Packet resource count: {resourceData?.Count()}");

        // Load the data for each car and resource module
        foreach (var resource in resourceData)
        {
            if (!resourceTypeToLocoResourceModule.TryGetValue(resource.ResourceType, out var module))
            {
                Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] Failed to find resource module for type {resource.ResourceType}");
                continue;
            }

            if (module != null)
            {
                if (module.resourceData.Count == resource.Values.Length)
                {
                    for (int i = 0; i < module.resourceData.Count; i++)
                    {
                        module.resourceData[i].unitsToBuy = resource.Values[i];
                    }
                }
                else
                {
                    Multiplayer.LogWarning($"PitStop bulk data count mismatch post-force: {module.resourceData.Count} != {resource.Values.Count()}");
                }
            }
            else
                Multiplayer.LogWarning($"PitStop module not found for resource type: {resource.ResourceType}");

            Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] Resource module data loaded for {resource.ResourceType}");

            // Set the grab state
            bool grabbed = (resource.FillingState != LocoResourceModuleFillingState.None);
            bool isLocallyGrabbed = isResourceGrabbedDict.TryGetValue(resource.ResourceType, out var localGrabbed) && localGrabbed;

            leverLookup.TryGetValue(resource.ResourceType, out LeverBase lever);
            //grabbedHandlerLookup.TryGetValue(resource.ResourceType, out LeverBase grab);

            if (!isLocallyGrabbed)
            {
                lever?.BlockControl(grabbed);
                if (lever != null)
                    lever.InteractionAllowed = !grabbed;

                if (grabbed)
                    lever?.ForceEndInteraction();
            }

            Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] Grab states set for {resource.ResourceType}, state: {grabbed}");

            int valvePos = resource.FillingState switch
            {
                LocoResourceModuleFillingState.Filling => -1,
                LocoResourceModuleFillingState.Draining => 1,
                _ => 0
            };

            module.OnValvePositionChange(valvePos);

            Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] Valve position set for {resource.ResourceType}, position: {valvePos}");

            // Update remote grab state
            isResourceRemoteGrabbedDict[resource.ResourceType] = grabbed;
        }

        // Refresh the cash register display
        register?.OnUnitsToBuyChanged();

        Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] Car Index: {carSelection}");
        SetCarSelection(carSelection);


        Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] bulk data Plugs {plugData.Count()}");

        // Sync plugs
        foreach (var plug in plugData)
        {
            var result = NetworkedPluggableObject.Get(plug.NetId, out var netPlug);
            Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] Plugs netId: {plug.NetId}, found: {result}");

            netPlug?.ProcessBulkUpdate(plug);
        }

        Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] Plugs synced");

        // Sync faucet position
        if (faucetPositioner != null)
        {
            Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] Faucet notch: {faucetNotch}");

            SetFaucetRotation(faucetNotch);

            while (!faucetTargetReached)
                yield return null;
        }

        // Mark data as refreshed to allow player interactions
        Refreshed = true;

        Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessBulkUpdate_Internal() [{StationName}, {NetId}] Bulk data refreshed");
    }

    /// <summary>
    /// Applies another player's pit stop interaction to this world: a client applying a packet, or
    /// the server applying a remote player's request.
    /// </summary>
    /// <param name="packet">The packet containing interaction data.</param>
    public void ApplyInteractionPacket(CommonPitStopInteractionPacket packet)
    {
        LeverBase grab = null;
        RotaryAmplitudeChecker amplitudeChecker = null;
        LeverBase lever = null;
        LocoResourceModule resourceModule = null;

        Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ApplyInteractionPacket() [{StationName}, {NetId}] Tick: {packet.Tick}, Packet InteractionType: {packet.InteractionType}, ResourceType: {packet.ResourceType}, Value: {packet.Value}");

        // Validate interaction type
        if (!Enum.IsDefined(typeof(PitStopStationInteractionType), packet.InteractionType))
        {
            Multiplayer.LogWarning($"Invalid interaction type: {packet.InteractionType} in ApplyInteractionPacket()");
            return;
        }

        PitStopStationInteractionType interactionType = (PitStopStationInteractionType)packet.InteractionType;

        bool isResourceSelection = interactionType switch
        {
            PitStopStationInteractionType.CarSelectorGrab => false,
            PitStopStationInteractionType.CarSelectorUngrab => false,
            PitStopStationInteractionType.CarSelection => false,

            PitStopStationInteractionType.FaucetGrab => false,
            PitStopStationInteractionType.FaucetUngrab => false,
            PitStopStationInteractionType.FaucetPosition => false,

            _ => true,
        };

        // Validate resource type (no resource type for car selectors
        if (isResourceSelection && !Enum.IsDefined(typeof(ResourceType), packet.ResourceType))
        {
            Multiplayer.LogWarning($"Received invalid ResourceType \"{packet.ResourceType}\" at Pit Stop station {StationName}");
            return;
        }

        ResourceType resourceType = (ResourceType)packet.ResourceType;

        Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessPacket() [{StationName}, {NetId}] {interactionType}, resource type: {resourceType}, state: {packet.Value}");

        // Validate resource module exists
        if (isResourceSelection && !resourceTypeToLocoResourceModule.TryGetValue(resourceType, out resourceModule))
        {
            Multiplayer.LogWarning($"Could not find LocoResourceModule for ResourceType \"{resourceType}\" at Pit Stop station {StationName}");
            return;
        }

        switch (interactionType)
        {
            case PitStopStationInteractionType.Reject:
                //todo: implement rejection
                break;

            case PitStopStationInteractionType.LeverState:
                leverLookup.TryGetValue(resourceType, out lever);

                if (!leverStateLookup.TryGetValue(resourceType, out var tup))
                {
                    Multiplayer.LogError($"Could not find Rotary Amplitude Handler in rotaryAmplitudeLookup for Pit Stop station {StationName}, resource type: {resourceType}");
                    return;
                }
                else
                {
                    (amplitudeChecker, resourceModule, _) = tup;

                    if (packet.Value < RotaryAmplitudeChecker.MIN_REACHED || packet.Value > RotaryAmplitudeChecker.MAX_REACHED)
                    {
                        Multiplayer.LogError($"Invalid lever value ({packet.Value}) received for Pit Stop station {StationName}, resource type: {resourceType}");
                        return;
                    }
                }

                bool grabbed = (packet.Value != 0);
                bool isLocallyGrabbed = isResourceGrabbedDict.TryGetValue(resourceType, out var localGrabbed) && localGrabbed;

                if (!isLocallyGrabbed)
                {
                    lever?.BlockControl(grabbed);
                    if (lever != null)
                        lever.InteractionAllowed = !grabbed;

                    if (grabbed)
                        grab?.ForceEndInteraction();
                }

                Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessPacket() [{StationName}, {NetId}] {interactionType}, resource type: {resourceType}, state: {packet.Value}, grabbed: {grabbed}, resourceModule: {resourceModule != null}, isResourceRemoteGrabbed: {isResourceRemoteGrabbedDict[resourceType]}");

                resourceModule.OnValvePositionChange((int)packet.Value);

                // Update remote grab state and delay set units
                bool wasRemoteGrabbed = isResourceRemoteGrabbedDict[resourceType];
                isResourceRemoteGrabbedDict[resourceType] = grabbed;

                if (wasRemoteGrabbed && !grabbed)
                {
                    CoroutineManager.Instance.StartCoroutine(SetUnitsDelayed(resourceModule));
                }
                break;

            case PitStopStationInteractionType.ResourceUpdate:

                Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessPacket() [{StationName}, {NetId}] {interactionType}, resource type: {resourceType}, state: {packet.Value}, resourceModule: {resourceModule != null}, isResourceRemoteGrabbed: {isResourceRemoteGrabbedDict[resourceType]}");

                // Validate the value range
                if (packet.Value < resourceModule.AbsoluteMinValue || packet.Value > resourceModule.AbsoluteMaxValue)
                {
                    Multiplayer.LogError($"Invalid Pit Stop state value: {packet.Value} for resource {resourceModule.resourceType}");
                    return;
                }

                SetUnits(resourceModule, packet.Value);
                Multiplayer.LogDebug(() => $"NetworkedPitStopStation.ProcessPacket() [{StationName}, {NetId}] {interactionType}, resource type: {resourceType}, state: {packet.Value}, flowing: {resourceModule.IsFlowing}");

                break;

            case PitStopStationInteractionType.CarSelectorGrab:
                //block interaction
                carSelectorGrab?.BlockControl(true);
                if (carSelectorGrab != null)
                    carSelectorGrab.InteractionAllowed = false;
                break;

            case PitStopStationInteractionType.CarSelectorUngrab:
                //allow interaction
                carSelectorGrab?.BlockControl(false);
                if (carSelectorGrab != null)
                    carSelectorGrab.InteractionAllowed = true;
                SetCarSelection((int)packet.Value);
                break;

            case PitStopStationInteractionType.CarSelection:
                SetCarSelection((int)packet.Value);
                break;

            case PitStopStationInteractionType.FaucetGrab:
                //block interaction
                faucetPositionerGrab?.BlockControl(true);
                if (faucetPositionerGrab != null)
                    faucetPositionerGrab.InteractionAllowed = false;
                break;

            case PitStopStationInteractionType.FaucetUngrab:
                //allow interaction
                faucetPositionerGrab?.BlockControl(false);
                if (faucetPositionerGrab != null)
                    faucetPositionerGrab.InteractionAllowed = true;

                SetFaucetRotation((int)packet.Value);

                break;

            case PitStopStationInteractionType.FaucetPosition:

                SetFaucetRotation((int)packet.Value);

                break;
        }
    }
    #endregion
}
