using DV.Localization;
using DV.UI;
using DV.UIFramework;
using Multiplayer.Shared.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.Menus.Controls;

// Reusing the game's menu buttons for the mod's own panes.
public static class MenuButtonExtensions
{
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
}
