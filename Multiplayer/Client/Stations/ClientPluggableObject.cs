using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Utils;
using System.Collections;
using UnityEngine;

namespace Multiplayer.Client.Stations;

/// <summary>
/// A client's part of a service station's plug (beside its <see cref="NetworkedPluggableObject"/>):
/// holds the local player's interactions with it back until the server's state of the plug has
/// arrived, and applies that state (part of the station's, sent as the player comes near). Added
/// by the station's <see cref="ClientPitStopStation"/> as the plug is set up.
/// </summary>
public class ClientPluggableObject : MonoBehaviour
{
    public NetworkedPluggableObject Plug { get; private set; }

    /// <summary>Sets the plug up on this client. Called by hand, as the plug may be inactive.</summary>
    public void Setup()
    {
        if (Plug != null)
            return;
        Plug = GetComponent<NetworkedPluggableObject>();
        Plug.WaitingForServerState = true;
    }

    protected void OnDisable()
    {
        // The server sends the plug's state again, with its station's, when the player comes back near.
        if (Plug != null)
            Plug.WaitingForServerState = true;
    }

    public void ProcessBulkUpdate(PitStopPlugData data)
    {
        Multiplayer.LogDebug(() => $"ClientPluggableObject.ProcessBulkUpdate() netId: {Plug.NetId}");
        CoroutineManager.Instance.StartCoroutine(WaitForInit(data));
    }

    private IEnumerator WaitForInit(PitStopPlugData data)
    {
        yield return new WaitUntil(() => Plug.PluggableObject != null && Plug.PluggableObject.initialized);

        Multiplayer.LogDebug(() => $"ClientPluggableObject.WaitForInit() netId: {Plug.NetId} Complete");

        var interaction = data.State;
        Plug.ProcessInteraction(interaction, data.PlayerId, data.TrainCarNetId, data.SocketIndex, data.Position, data.Rotation);

        //wait 1 frame for plugs that are docking
        yield return null;
        //clear the docking flag
        if (interaction == PlugInteractionType.DockSocket || interaction == PlugInteractionType.DockHome)
            Plug.IsConnecting = false;

        //allow the player to interact
        Plug.WaitingForServerState = false;
    }
}
