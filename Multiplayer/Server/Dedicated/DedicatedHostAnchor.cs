using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Session;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Dedicated;

/// <summary>
/// Where a dedicated host's world is loaded. DV streams scenery and terrain, loads terrain colliders,
/// shifts its origin and enables nearby items around one point, its PlayerTracker, which follows
/// <see cref="PlayerManager.ActiveCamera"/>. On a dedicated host the local player is a parked body,
/// so this puts a disabled camera in as <see cref="PlayerManager.PlayerCameraOverride"/> and keeps it
/// on a player: the one who has been connected longest. With nobody connected it stays where it
/// was. The body itself stays parked (vanilla saves its position).
/// Physics LOD measures from the player transform instead; see DistanceLodSystemPatch.
/// See docs/design/dedicated-host.md.
/// </summary>
public sealed class DedicatedHostAnchor : MonoBehaviour
{
    // A standing player's eye height, so the anchor sits where a player's camera would.
    private const float EyeHeight = 1.7f;

    private Camera anchorCamera;
    private ServerPlayer followed;

    public static DedicatedHostAnchor Instance { get; private set; }

    /// <summary>The player the world is loaded around, or null.</summary>
    public ServerPlayer Followed => followed;

    /// <summary>Called by the server once the world has loaded.</summary>
    public static void Create()
    {
        if (Instance != null || !NetworkLifecycle.Instance.IsDedicatedHost)
            return;
        // In the game scene, so it goes with the world on unload.
        GameObject go = new("[dedicated host anchor]");
        Instance = go.AddComponent<DedicatedHostAnchor>();
    }

    private void Awake()
    {
        anchorCamera = gameObject.AddComponent<Camera>();
        anchorCamera.enabled = false;   // never renders on its own
        anchorCamera.cullingMask = 0;
        if (PlayerManager.PlayerTransform != null)
            transform.position = PlayerManager.PlayerTransform.position + Vector3.up * EyeHeight;
        PlayerManager.PlayerCameraOverride = anchorCamera;
        Multiplayer.Log("Dedicated host: world anchor created");
    }

    private void OnDestroy()
    {
        if (PlayerManager.PlayerCameraOverride == anchorCamera)
            PlayerManager.PlayerCameraOverride = null;
        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        if (!NetworkLifecycle.Instance.IsDedicatedHost)
            return;

        // DV clears the override on its own camera switches; take it back.
        if (PlayerManager.PlayerCameraOverride != anchorCamera)
            PlayerManager.PlayerCameraOverride = anchorCamera;

        if (followed == null || !IsFollowable(followed))
        {
            ServerPlayer next = NetworkServer.Instance.ServerPlayers
                .Where(IsFollowable)
                .OrderBy(player => player.LastLogin)
                .FirstOrDefault();
            if (next != followed)
                Multiplayer.Log(next == null
                    ? "Dedicated host: nobody to follow; the world stays loaded where it is"
                    : $"Dedicated host: loading the world around {next.Username}");
            followed = next;
        }

        if (followed != null)
            transform.position = followed.WorldPosition + Vector3.up * EyeHeight;
    }

    // Connected, loaded, and has sent a position (until then it would read as the world origin).
    private static bool IsFollowable(ServerPlayer player) =>
        player != null && player.LoadingState == PlayerLoadingState.Complete && player.TrackingData.Position.HasValue &&
        NetworkServer.Instance.TryGetServerPlayer(player.Peer, out ServerPlayer current) && current == player;
}
