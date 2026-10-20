using DV.Optimizers;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Utils;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Client.Stations;

/// <summary>
/// A client's part of a service station (beside its <see cref="NetworkedPitStopStation"/>): holds
/// the local player's interactions back until the server's state of the station has arrived, and
/// applies that state (sent as the player comes near). Added by <c>NetworkClient.InitialiseStations</c>
/// on a game that isn't the host; the host's stations are its own.
/// </summary>
public class ClientPitStopStation : MonoBehaviour
{
    const float LOADING_TIMEOUT = 5f;

    public NetworkedPitStopStation Station { get; private set; }

    /// <summary>
    /// Sets the station up on this client. Called by hand: a station far from the player can be
    /// inactive, and Unity only runs Awake once it's active.
    /// </summary>
    public void Setup()
    {
        Station = GetComponent<NetworkedPitStopStation>();
        Station.WaitingForServerState = true;

        // The station finds its plugs as its resource modules initialise, after this.
        Station.PlugInitialised += OnPlugInitialised;
        foreach (NetworkedPluggableObject plug in Station.Plugs)
            OnPlugInitialised(plug);
    }

    protected void OnDisable()
    {
        // The server sends the station's state again when the player comes back near.
        if (Station != null)
            Station.WaitingForServerState = true;
    }

    protected void OnDestroy()
    {
        if (Station != null)
            Station.PlugInitialised -= OnPlugInitialised;
    }

    private static void OnPlugInitialised(NetworkedPluggableObject plug) =>
        plug.GetOrAddComponent<ClientPluggableObject>().Setup();

    public void ProcessBulkUpdate(ClientboundPitStopBulkUpdatePacket packet)
    {
        // Packet is broken up due to SubscribeResusable reusing/overwriting packet data
        CoroutineManager.Instance.StartCoroutine(ProcessBulkUpdate_Internal(packet.CarCount, packet.CarSelection, packet.FaucetNotch, packet.ResourceData, packet.PlugData));
    }

    private IEnumerator ProcessBulkUpdate_Internal(int carCount, int carSelection, int faucetNotch, LocoResourceModuleData[] resourceData, PitStopPlugData[] plugData)
    {
        float time = Time.time;
        string stationName = Station.StationName;
        ushort netId = Station.NetId;
        PitStopStation pitStop = Station.Station;

        // Allow pit stop to complete loading and cars to load in the pit stop
        Multiplayer.Log($"Processing bulk update for [{stationName}, {netId}]");

        yield return new WaitUntil
        (
            () =>
            {
                Multiplayer.LogDebug(() => $"ClientPitStopStation.ProcessBulkUpdate_Internal() [{stationName}, {netId}] Initialised: {Station.IsInitialised}, Active:{pitStop?.gameObject?.activeInHierarchy}, Packet Car Count: {carCount}, Station Car Count: {pitStop.pitstop?.carList?.Count}, Car Count Matched: {carCount == pitStop.pitstop?.carList?.Count}, time elapsed: {(Time.time - time)}");

                if (pitStop?.gameObject?.activeInHierarchy == false)
                {
                    // Don't time out if we're waiting for the object to be enabled
                    time = Time.time;
                    return false;
                }

                // Try to trigger colliders manually
                if (Station.IsInitialised && pitStop?.pitstop?.carList != null && carCount != pitStop.pitstop.carList.Count)
                    pitStop?.pitstop?.RefreshPitStopCarPresence();

                return (Station.IsInitialised && pitStop?.pitstop?.carList != null && carCount == pitStop.pitstop.carList.Count)
                || (Time.time - time) > LOADING_TIMEOUT;

            }
        );


        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();

        if ((Time.time - time) > LOADING_TIMEOUT)
            Multiplayer.LogWarning($"PitStop [{stationName}] timed out waiting for load. PitStop Initialised: {Station.IsInitialised}, Packet Car Count: {carCount}, Station Car Count: {pitStop.pitstop?.carList?.Count}, Car Count Matched: {carCount == pitStop.pitstop?.carList?.Count}");

        Multiplayer.LogDebug(() => $"ClientPitStopStation.ProcessBulkUpdate_Internal() [{stationName}] Car count: {carCount}, resource data count: {resourceData.Count()}, resource data: [{string.Join(", ", resourceData.Select(x => $"{x.ResourceType}: {{{string.Join(", ", x.Values)}}}"))}]");
        // Make sure the data elements exist prior to attempting to load them
        Station.InitialiseData();

        Multiplayer.LogDebug(() => $"ClientPitStopStation.ProcessBulkUpdate_Internal() [{stationName}] PitStop bulk data car count matches. Station module count: {pitStop?.locoResourceModules?.resourceModules?.Count()}, Packet resource count: {resourceData?.Count()}");

        // Load the data for each car and resource module
        foreach (var resource in resourceData)
            Station.ApplyResourceState(resource);

        // Refresh the cash register display
        Station.RefreshRegister();

        Multiplayer.LogDebug(() => $"ClientPitStopStation.ProcessBulkUpdate_Internal() [{stationName}, {netId}] Car Index: {carSelection}");
        Station.SetCarSelection(carSelection);


        Multiplayer.LogDebug(() => $"ClientPitStopStation.ProcessBulkUpdate_Internal() [{stationName}, {netId}] bulk data Plugs {plugData.Count()}");

        // Sync plugs
        foreach (var plug in plugData)
        {
            var result = NetworkedPluggableObject.Get(plug.NetId, out var netPlug);
            Multiplayer.LogDebug(() => $"ClientPitStopStation.ProcessBulkUpdate_Internal() [{stationName}, {netId}] Plugs netId: {plug.NetId}, found: {result}");

            if (netPlug != null && netPlug.TryGetComponent(out ClientPluggableObject clientPlug))
                clientPlug.ProcessBulkUpdate(plug);
        }

        Multiplayer.LogDebug(() => $"ClientPitStopStation.ProcessBulkUpdate_Internal() [{stationName}, {netId}] Plugs synced");

        // Sync faucet position
        if (Station.HasFaucet)
        {
            Multiplayer.LogDebug(() => $"ClientPitStopStation.ProcessBulkUpdate_Internal() [{stationName}, {netId}] Faucet notch: {faucetNotch}");

            Station.SetFaucetRotation(faucetNotch);

            while (!Station.FaucetTargetReached)
                yield return null;
        }

        // Mark data as refreshed to allow player interactions
        Station.WaitingForServerState = false;

        Multiplayer.LogDebug(() => $"ClientPitStopStation.ProcessBulkUpdate_Internal() [{stationName}, {netId}] Bulk data refreshed");
    }
}
