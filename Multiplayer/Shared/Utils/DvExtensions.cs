using DV.Localization;
using DV.UI;
using DV.UIFramework;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.World;
using UnityEngine;
using UnityEngine.UI;



namespace Multiplayer.Shared.Utils;

public static class DvExtensions
{
    #region TrainCar

    public static ushort GetNetId(this TrainCar car)
    {
        ushort netId = 0;

        if (car != null && car.TryNetworked(out NetworkedTrainCar networkedTrainCar))
            netId = networkedTrainCar.NetId;
/*
        if (netId == 0)
            Multiplayer.LogWarning($"NetId for {car.carLivery.id} ({car.ID}) isn't initialized!\r\n" + (Multiplayer.Settings.DebugLogging ? new System.Diagnostics.StackTrace() : ""));*/
            //throw new InvalidOperationException($"NetId for {car.carLivery.id} ({car.ID}) isn't initialized!");
        return netId;
    }

    //public static NetworkedTrainCar Networked(this TrainCar trainCar)
    //{
    //    return NetworkedTrainCar.GetFromTrainCar(trainCar);
    //}

    public static bool TryNetworked(this TrainCar trainCar, out NetworkedTrainCar networkedTrainCar)
    {
        return NetworkedTrainCar.TryGetFromTrainCar(trainCar, out networkedTrainCar);
    }

    #endregion

    #region RailTrack

    public static NetworkedRailTrack Networked(this RailTrack railTrack)
    {
        return NetworkedRailTrack.GetFromRailTrack(railTrack);
    }

    #endregion

    #region UI
    public static GameObject UpdateButton(this GameObject pane, string oldButtonName, string newButtonName, string localeKey, string toolTipKey, Sprite icon)
    {
        // Find and rename the button
        GameObject button = pane.FindChildByName(oldButtonName);
        button.name = newButtonName;

        // Update localization and tooltip
        if (button.GetComponentInChildren<Localize>() != null)
        {
            button.GetComponentInChildren<Localize>().key = localeKey;
            foreach(var child in button.GetComponentsInChildren<I2.Loc.Localize>())
            {
                GameObject.Destroy(child);
            }
            ResetTooltip(button);
            button.GetComponentInChildren<Localize>().UpdateLocalization();
        }else if(button.GetComponentInChildren<UIElementTooltip>() != null)
        {
            button.GetComponentInChildren<UIElementTooltip>().enabledKey = localeKey + "__tooltip";
            button.GetComponentInChildren<UIElementTooltip>().disabledKey = localeKey + "__tooltip_disabled";
        }

        // Set the button icon if provided
        if (icon != null)
        {
            SetButtonIcon(button, icon);
        }

        // Enable button interaction
        button.GetComponentInChildren<ButtonDV>().ToggleInteractable(true);

        return button;
    }

    private static void SetButtonIcon(this GameObject button, Sprite icon)
    {
        // Find and set the icon for the button
        GameObject goIcon = button.FindChildByName("[icon]");
        if (goIcon == null)
        {
            Multiplayer.LogError("Failed to find icon!");
            return;
        }

        goIcon.GetComponent<Image>().sprite = icon;
    }

    public static void ResetTooltip(this GameObject button)
    {
        // Reset the tooltip keys for the button
        UIElementTooltip tooltip = button.GetComponent<UIElementTooltip>();
        tooltip.initialized = false;
        tooltip.disabledKey = null;
        tooltip.enabledKey = null;

    }

    #endregion

    #region Utils

    public static Vector3 GetWorldAbsolutePosition(this GameObject go)
    {
        return go.transform.GetWorldAbsolutePosition();
    }

    public static Vector3 GetWorldAbsolutePosition(this Transform transform)
    {
        return transform.position - WorldMover.currentMove;
    }

    /// <summary>Whether this game may pause: the server decides (<see cref="IPauseServer"/>), so never on a remote client.</summary>
    public static bool AllowPause()
    {
        return NetworkLifecycle.Instance.LocalServer is IPauseServer server && server.AllowsPause;
    }
    #endregion
}
