using DV.CabControls;
using DV.Customization.Gadgets.Implementations;
using DV.HUD;
using HarmonyLib;
using Multiplayer.Client.Session;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// A remote client's control panel (gadgets.md, "Logic that writes the train"): its joysticks and
/// buttons write the car's controls, which on a client with the cab loaded scrolls the player's own
/// lever and with none jumps it across its range. For a panel the server sent, they go to the server
/// as panel inputs instead, checked against the cab levers' leases and applied there as the panel
/// would; the result comes back through the car's control sync. The checks vanilla makes first (power,
/// the joystick's dead zone, the rain fault) are made here too, so a press that would do nothing sends
/// nothing.
/// </summary>
[HarmonyPatch]
internal static class GadgetPanelClientPatches
{
    private static bool ServerRuns(GadgetControlPanelLODObject lod) =>
        NetworkRole.IsRemoteClient && lod.Base != null && GadgetJoinApplier.IsServerSent(lod.Base);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetControlPanelLODObject), "UpdateOverridableControlUsingJoystick")]
    private static bool Joystick(GadgetControlPanelLODObject __instance, float value, InteriorControlsManager.ControlType type)
    {
        if (!ServerRuns(__instance))
            return true;
        if (__instance.IsJoystickValueActive(value) && !__instance.isInFaultState)
            Send(__instance, GadgetPanelInput.Move, type, Mathf.Sign(value));
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetControlPanelLODObject), "UpdateHornUsingJoystick")]
    private static bool Horn(GadgetControlPanelLODObject __instance, float value)
    {
        if (!ServerRuns(__instance))
            return true;
        if (__instance.Base.PowerState && !__instance.isInFaultState)
            Send(__instance, GadgetPanelInput.Horn, InteriorControlsManager.ControlType.Horn, Mathf.Abs(value));
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetControlPanelLODObject), "ButtonStartUpdated")]
    private static bool Starter(GadgetControlPanelLODObject __instance, ValueChangedEventArgs value) =>
        Button(__instance, GadgetPanelInput.Starter, InteriorControlsManager.ControlType.StarterControl, value.newValue);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetControlPanelLODObject), "ButtonCutoffUpdated")]
    private static bool PowerOff(GadgetControlPanelLODObject __instance, ValueChangedEventArgs value) =>
        Button(__instance, GadgetPanelInput.PowerOff, InteriorControlsManager.ControlType.None, value.newValue);

    private static bool Button(GadgetControlPanelLODObject lod, GadgetPanelInput input, InteriorControlsManager.ControlType type, float value)
    {
        if (!ServerRuns(lod))
            return true;
        if (lod.Base.PowerState || value == 0f)
            Send(lod, input, type, value);
        return false;
    }

    private static void Send(GadgetControlPanelLODObject lod, GadgetPanelInput input, InteriorControlsManager.ControlType type, float value)
    {
        ushort itemNetId = lod.Base.GadgetItem?.GetComponent<NetworkedItem>()?.NetId ?? 0;
        if (itemNetId == 0)
            return;
        NetworkClient.Instance?.SendGadgetPanel(new ServerboundGadgetPanelPacket
        {
            ItemNetId = itemNetId,
            Input = (byte)input,
            Control = (byte)type,
            Value = value
        });
    }
}
