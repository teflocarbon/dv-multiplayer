using DV;
using Multiplayer.Editor.Components.Player;
using Multiplayer.Shared.Players;
using UnityEngine;

namespace Multiplayer.Client.Players;

/// <summary>
/// The name tag over another player's avatar and the ping it shows: presentation, beside the
/// avatar's <see cref="NetworkedPlayer"/> (its presence) on the same GameObject.
/// </summary>
public class NetworkedPlayerNameTag : MonoBehaviour
{
    private NetworkedPlayer player;
    private NameTag nameTag;

    public int Ping { get; private set; }

    protected void Awake()
    {
        player = GetComponent<NetworkedPlayer>();
        nameTag = GetComponentInChildren<NameTag>();

        nameTag.LookTarget = PlayerManager.ActiveCamera.transform;
        // A named handler so OnDestroy can remove it: the static event otherwise kept every
        // departed player's avatar alive and threw on their destroyed name tag at each camera change.
        PlayerManager.CameraChanged += OnCameraChanged;

        if (name != null)
            nameTag.SetUsername(name);
        player.UsernameChanged += OnUsernameChanged;

        OnSettingsUpdated(Multiplayer.Settings);
        Settings.OnSettingsUpdated += OnSettingsUpdated;
    }

    protected void OnDestroy()
    {
        Settings.OnSettingsUpdated -= OnSettingsUpdated;
        PlayerManager.CameraChanged -= OnCameraChanged;
        if (player != null)
            player.UsernameChanged -= OnUsernameChanged;
    }

    private void OnCameraChanged()
    {
        if (nameTag != null && PlayerManager.ActiveCamera != null)
            nameTag.LookTarget = PlayerManager.ActiveCamera.transform;
    }

    private void OnUsernameChanged(string username)
    {
        nameTag?.SetUsername(username);
    }

    private void OnSettingsUpdated(Settings settings)
    {
        nameTag.ShowUsername(settings.ShowNameTags);
        nameTag.ShowPing(settings.ShowNameTags && settings.ShowPingInNameTags);
    }

    public void SetPing(int ping)
    {
        nameTag?.SetPing(ping);
        Ping = ping;
    }
}

public static class NetworkedPlayerPing
{
    /// <summary>The player's last reported ping, as their name tag shows it; 0 before the first report.</summary>
    public static int GetPing(this NetworkedPlayer player) =>
        player.TryGetComponent(out NetworkedPlayerNameTag tag) ? tag.Ping : 0;

    public static void SetPing(this NetworkedPlayer player, int ping)
    {
        if (player.TryGetComponent(out NetworkedPlayerNameTag tag))
            tag.SetPing(ping);
    }
}
